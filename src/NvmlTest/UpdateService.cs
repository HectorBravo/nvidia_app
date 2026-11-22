using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace HectorBravo.Nvidia.Test;

/// <summary>
/// CLI port of the dashboard's auto-update (NvidiaMonitor/UpdateService.cs), scoped to
/// this app's release stream. Checks GitHub Releases for a newer published NvmlTest
/// build and, on `--update`, downloads + applies it without user interaction.
///
/// Release tags are produced by release.ps1 as "cli/&lt;yyyyMMdd_HHmm&gt;" — the compile
/// date (LastWriteTime) stamped into the published exe, which is exactly what the CLI
/// prints as its "Build:" stamp. The ONLY update rule (same as the GUI): the newest
/// published cli/ release ships a zip whose name differs from the zip this build was
/// shipped in (embedded resource "releaseinfo.txt", written by release.ps1) → notify.
/// No timestamps, no tag-format parsing, so a tag-format change can never leave builds
/// stuck without updates. Dev builds have no embedded name and never notify.
///
/// Applying an update: download the zip to %TEMP%, verify it, then hand off to a small
/// PowerShell helper that waits for this process to exit (its DLLs are locked while
/// running), backs up the old files, extracts the new ones over the app directory and
/// deletes itself. Unlike the GUI there is no relaunch — the CLI exits and the user
/// runs the updated exe again.
/// </summary>
public sealed class UpdateService
{
    private const string RepoApi = "https://api.github.com/repos/HectorBravo/nvidia_app";

    /// <summary>Only releases from this app's stream, and only zips that are ours.</summary>
    private const string TagPrefix = "cli/";
    private const string ZipPrefix = "NvmlTest-";

    private static readonly HttpClient Http = CreateHttp();

    public UpdateService() { }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API requires a User-Agent header; keep the request cheap and API-shaped.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NvmlTest-updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // Anonymous API calls are capped at 60/hour per IP — easy to exhaust with a live
        // --watch feed, after which every check silently fails (403) and updates go
        // unnoticed. Authenticate when a token is available: GITHUB_TOKEN / GH_TOKEN env
        // vars, or a `gh auth token` on this machine. Requests without a token still work.
        var token = GitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>Best available GitHub token: env vars first, then the gh CLI (once per process).</summary>
    private static string? _cachedToken;
    private static string? GitHubToken()
    {
        if (_cachedToken != null) return _cachedToken == "" ? null : _cachedToken;
        try
        {
            foreach (var name in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
            {
                var t = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(t)) { _cachedToken = t.Trim(); return _cachedToken; }
            }
            // Ask the GitHub CLI, if installed and authenticated (hidden window, 3 s cap).
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p != null && p.WaitForExit(3000) && p.ExitCode == 0)
            {
                var t = p.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrWhiteSpace(t)) { _cachedToken = t; return _cachedToken; }
            }
        }
        catch { /* gh not installed / not authenticated — stay anonymous */ }
        _cachedToken = ""; // remember: no token available
        return null;
    }

    /// <summary>The compile date (LastWriteTime) of the running dll — same stamp the CLI prints.</summary>
    public static DateTime BuildStamp =>
        new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).LastWriteTime;

    /// <summary>
    /// Identity of THIS build: the name of the release zip it was shipped in, read from
    /// the embedded resource "releaseinfo.txt" (written by release.ps1 at publish time —
    /// e.g. "NvmlTest-20261119_0949.zip"). Empty for dev builds never packaged into a
    /// release zip; such builds never notify.
    /// </summary>
    public static string ReleaseZipName
    {
        get
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream("releaseinfo.txt");
                if (s == null) return string.Empty;
                using var r = new StreamReader(s);
                return r.ReadToEnd().Trim();
            }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// Identity of THIS build as a release TAG (e.g. "cli/20261121_1344"): the embedded
    /// zip name without its extension. Empty for dev builds — callers show "dev build".
    /// (The tag's date part is exactly what release.ps1 stamps into the binaries.)
    /// </summary>
    public static string CurrentReleaseTag
    {
        get
        {
            var zip = ReleaseZipName;
            if (zip.Length == 0) return string.Empty;
            var stamp = Path.GetFileNameWithoutExtension(zip); // "NvmlTest-<stamp>" → "<stamp>" part kept whole below
            var dash = stamp.IndexOf('-');
            return dash >= 0 ? "cli/" + stamp[(dash + 1)..] : "cli/" + stamp;
        }
    }

    /// <summary>
    /// The ONLY update rule: the newest published cli release ships a zip whose name
    /// differs from the zip this build was shipped in (embedded resource) → an update is
    /// available. No timestamps, no tag-format parsing.
    /// </summary>
    private static bool IsNewerRelease(string newestZipName) =>
        !string.Equals(newestZipName, ReleaseZipName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Metadata for an available update, once found.</summary>
    public sealed record UpdateInfo(DateTime PublishedAtUtc, string DownloadUrl, string ReleaseUrl, string Tag, string ZipName);

    /// <summary>
    /// Asks GitHub for the newest "cli/&lt;compile-date&gt;" release and offers it when it is
    /// newer than this build (see <see cref="IsNewerRelease"/>). Any failure (offline, rate
    /// limit, parse) returns null — the check must never disturb the user or the table.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            // A dev build (no embedded zip name) can never decide what is newer — stay quiet.
            if (ReleaseZipName.Length == 0) return null;

            var url = $"{RepoApi}/releases?per_page=100";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            // Newest cli release = highest `published_at` (falling back to `created_at`).
            DateTime? bestPublished = null;
            JsonElement bestEl = default;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tag_name", out var tagEl)) continue;
                var tagStr = tagEl.GetString();
                if (tagStr == null || !tagStr.StartsWith(TagPrefix, StringComparison.Ordinal)) continue; // gui/ — not ours

                DateTime relPublishedUtc = DateTime.MinValue;
                if (!el.TryGetProperty("published_at", out var paEl) || paEl.ValueKind != JsonValueKind.String)
                    el.TryGetProperty("created_at", out paEl); // fallback for drafts/edge cases
                if (paEl.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(paEl.GetString(), out var caParsed))
                {
                    relPublishedUtc = caParsed.UtcDateTime;
                }

                if (bestPublished == null || relPublishedUtc > bestPublished)
                {
                    bestPublished = relPublishedUtc;
                    bestEl = el;
                }
            }

            if (bestPublished == null) return null; // no cli releases at all

            if (!bestEl.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array
                || assets.GetArrayLength() == 0)
            {
                return null; // release without a zip → nothing to install
            }

            // Prefer OUR asset (a cli release could in principle carry several zips).
            JsonElement asset = default;
            var foundAsset = false;
            foreach (var a in assets.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var nEl)
                    && nEl.ValueKind == JsonValueKind.String
                    && (nEl.GetString() ?? string.Empty).StartsWith(ZipPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    asset = a;
                    foundAsset = true;
                    break;
                }
            }
            if (!foundAsset) return null; // our stream has no NvmlTest zip → not installable

            if (!asset.TryGetProperty("browser_download_url", out var dlEl)
                || dlEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(dlEl.GetString()))
            {
                return null;
            }

            // The ONLY rule: the newest release's zip name vs this build's embedded zip name.
            string newestZipName = asset.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;
            if (!IsNewerRelease(newestZipName)) return null; // we ARE the newest published build

            string releaseUrl = string.Empty;
            if (bestEl.TryGetProperty("html_url", out var htmlEl) && htmlEl.ValueKind == JsonValueKind.String)
            {
                releaseUrl = htmlEl.GetString() ?? string.Empty;
            }

            var bestTagStr = bestEl.GetProperty("tag_name").GetString();
            return new UpdateInfo(bestPublished.Value, dlEl.GetString()!, releaseUrl, bestTagStr ?? string.Empty, newestZipName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // never let an update check break the table
        }
    }

    /// <summary>
    /// Downloads the update zip to %TEMP%, verifies it, installs the swap helper and
    /// launches it, then returns — the caller exits so the helper can replace the files.
    /// Throws on any failure (nothing is deleted or replaced unless the download
    /// verified); the caller reports the failure and keeps running.
    /// </summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var tag = Path.GetFileNameWithoutExtension(info.ZipName);
        var temp = Path.GetTempPath();
        var zipPath = Path.Combine(temp, $"NvmlTest-update-{tag}.zip");
        var scriptPath = Path.Combine(temp, "NvmlTest-update.ps1");

        // ── download ───────────────────────────────────────────────
        progress?.Report("Downloading update…");
        using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    progress?.Report($"Downloading update… {done * 100 / total}%");
                }
            }
        }

        // ── verify the zip before touching anything ────────────────
        progress?.Report("Verifying update…");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var hasExe = false;
            foreach (var entry in zip.Entries)
            {
                // The release zips are flat, but reject path-escaping entries regardless.
                var name = entry.FullName;
                if (name.Contains("..", StringComparison.Ordinal)
                    || Path.IsPathRooted(name) || name.Contains(':'))
                {
                    throw new InvalidDataException($"Unsafe entry in update zip: {name}");
                }
                if (name.Equals("NvmlTest.exe", StringComparison.OrdinalIgnoreCase))
                {
                    hasExe = true;
                }
            }
            if (!hasExe)
            {
                throw new InvalidDataException("Update zip does not contain NvmlTest.exe.");
            }
        }

        // ── install the swap helper and launch it ──────────────────
        progress?.Report("Applying update…");
        var appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        var backupDir = Path.Combine(temp, $"NvmlTest-backup-{tag}");
        var pid = Environment.ProcessId;

        // Waits for this process to die (releasing the file locks), backs up the current
        // files, extracts the new ones over the app dir, and removes itself. No relaunch:
        // the CLI is a one-shot tool — the user runs the updated exe when they want it.
        // NOTE: never name helper variables $pid/$lock — PowerShell reserves them.
        var script =
            "$ErrorActionPreference='Stop'\n" +
            $"$appPid={pid}\n" +
            $"$zip='{Esc(zipPath)}'\n" +
            $"$appDir='{Esc(appDir)}'\n" +
            $"$backup='{Esc(backupDir)}'\n" +
            "$deadline=(Get-Date).AddSeconds(60)\n" +
            "while((Get-Date) -lt $deadline){ if(-not (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){break}; Start-Sleep -Milliseconds 500 }\n" +
            "Start-Sleep -Milliseconds 1000\n" + // let the OS finish releasing handles
            "New-Item -ItemType Directory -Force -Path $backup | Out-Null\n" +
            "Get-ChildItem -LiteralPath $appDir -Include *.exe,*.dll,*.json -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $backup -Force }\n" +
            "Expand-Archive -LiteralPath $zip -DestinationPath $appDir -Force\n" +
            $"Remove-Item -LiteralPath '{Esc(scriptPath)}' -ErrorAction SilentlyContinue\n";
        await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Could not start the update helper.");
        }
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
