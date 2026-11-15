using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// Checks GitHub Releases for a newer published build of this app and, on user
/// request, downloads + applies the update (one-click auto-update).
///
/// Release tags are produced by release.ps1 as "gui/&lt;yyyyMMdd_HHmm&gt;" — the
/// compile date (LastWriteTime) of the published exe, which is exactly what the
/// GUI header prints. So "is there a newer build" = does a GitHub release with
/// THIS app's exact compile-date tag exist whose zip asset was uploaded after
/// our local exe was built. If it was, our copy is a stale/older compile of the
/// same (or earlier) commit and updating is safe + correct.
///
/// Applying an update: download the zip to %TEMP%, verify it, then hand off to a
/// small PowerShell helper that waits for this process to exit, backs up the old
/// files, extracts the new ones over the app directory, relaunches the exe and
/// deletes itself. (The app must exit first — its DLLs are locked while running.)
/// </summary>
public sealed class UpdateService
{
    private const string RepoApi = "https://api.github.com/repos/HectorBravo/nvidia_app";

    private static readonly HttpClient Http = CreateHttp();

    private readonly AppLog? _log;

    public UpdateService(AppLog? log = null) => _log = log;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub API requires a User-Agent header; keep the request cheap and API-shaped.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NvidiaMonitor-updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // Anonymous API calls are capped at 60/hour per IP — easy to exhaust with hourly
        // checks, after which every check silently fails (403) and updates go unnoticed.
        // Authenticate when a token is available: GITHUB_TOKEN / GH_TOKEN env vars, or a
        // `gh auth token` on this machine. Requests without a token still work (public repo).
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
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
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

    /// <summary>The compile date (LastWriteTime) of the running exe — same stamp the header prints.</summary>
    public static DateTime BuildStamp =>
        new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).LastWriteTime;

    /// <summary>
    /// Identity of THIS build: the name of the release zip it was shipped in, read from
    /// the embedded resource "releaseinfo.txt" (written by release.ps1 at publish time —
    /// e.g. "NvidiaMonitor-20261116_0226.zip"). Empty for dev builds that were never
    /// packaged into a release zip.
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
    /// The ONLY update rule: the newest published gui release ships a zip whose name
    /// differs from the zip this build was shipped in (embedded resource) → an update
    /// is available. No timestamps, no tag-format parsing.
    /// </summary>
    private static bool IsNewerRelease(string newestZipName) =>
        !string.Equals(newestZipName, ReleaseZipName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Metadata for an available update, once found.</summary>
    public sealed record UpdateInfo(DateTime PublishedAtUtc, string DownloadUrl, string ReleaseUrl, string Tag);

    /// <summary>
    /// Asks GitHub for the newest "gui/&lt;compile-date&gt;" release and offers it when
    /// it is newer than this build (see <see cref="IsNewerRelease"/> — format-tolerant,
    /// so a tag-format change can never leave builds stuck without updates). Any
    /// failure (offline, rate limit, parse) returns null — the check must never
    /// disturb the user.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{RepoApi}/releases?per_page=100";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            // Newest gui release = highest `published_at` (falling back to `created_at`).
            DateTime? bestPublished = null;
            JsonElement bestEl = default;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tag_name", out var tagEl)) continue;
                var tagStr = tagEl.GetString();
                if (tagStr == null || !tagStr.StartsWith("gui/", StringComparison.Ordinal)) continue; // cli/ — not ours

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

            if (bestPublished == null) return null; // no gui releases at all

            if (!bestEl.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array
                || assets.GetArrayLength() == 0)
            {
                return null; // release without a zip → nothing to install
            }

            var asset = assets[0];
            if (!asset.TryGetProperty("browser_download_url", out var dlEl)
                || dlEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            // The ONLY rule: the newest release's zip name vs this build's embedded zip name.
            string newestZipName = asset.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;
            if (!IsNewerRelease(newestZipName)) return null; // we ARE the newest published build

            DateTime publishedUtc = bestPublished.Value;

            string releaseUrl = string.Empty;
            if (bestEl.TryGetProperty("html_url", out var htmlEl) && htmlEl.ValueKind == JsonValueKind.String)
            {
                releaseUrl = htmlEl.GetString() ?? string.Empty;
            }

            var bestTagStr = bestEl.GetProperty("tag_name").GetString();
            _log?.Info($"Update available: release {bestTagStr} (zip {newestZipName} != local {ReleaseZipName}).");
            return new UpdateInfo(publishedUtc, dlEl.GetString()!, releaseUrl, bestTagStr!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Info($"Update check failed (ignored): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the update zip to %TEMP%, verifies it, installs the swap helper,
    /// launches it and shuts the app down so the helper can replace the files.
    /// Throws on any failure (nothing is deleted or replaced unless the download
    /// verified) — the caller reports the failure in the UI and stays running.
    /// </summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var tag = (ReleaseZipName.Length > 0 ? Path.GetFileNameWithoutExtension(ReleaseZipName) : BuildStamp.ToString("yyyyMMdd_HHmm"));
        var temp = Path.GetTempPath();
        var zipPath = Path.Combine(temp, $"NvidiaMonitor-update-{tag}.zip");
        var scriptPath = Path.Combine(temp, "NvidiaMonitor-update.ps1");

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
                if (string.Equals(Path.GetFileName(name), "NvidiaMonitor.exe", StringComparison.OrdinalIgnoreCase))
                {
                    hasExe = true;
                }
            }
            if (!hasExe)
            {
                throw new InvalidDataException("Update zip does not contain NvidiaMonitor.exe.");
            }
        }

        // ── install + launch the swap helper, then exit ────────────
        progress?.Report("Applying update — restarting…");
        var appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        var backupDir = Path.Combine(temp, $"NvidiaMonitor-backup-{tag}");
        var pid = Environment.ProcessId;

        // Waits for this process to die (releasing the file locks), backs up the
        // current files, extracts the new ones over the app dir, relaunches, and
        // removes itself. Runs hidden via cmd → powershell.
        // NOTE: never name helper variables $pid/$lock — PowerShell reserves them.
        var script =
            "$ErrorActionPreference='Stop'\n" +
            $"$appPid={pid}\n" +
            $"$zip='{Esc(zipPath)}'\n" +
            $"$appDir='{Esc(appDir)}'\n" +
            $"$backup='{Esc(backupDir)}'\n" +
            "$lockFile=Join-Path $env:TEMP 'NvidiaMonitor.lock'\n" +
            "$deadline=(Get-Date).AddSeconds(60)\n" +
            "while((Get-Date) -lt $deadline){ if(-not (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){break}; Start-Sleep -Milliseconds 500 }\n" +
            "Start-Sleep -Milliseconds 1000\n" + // let the OS finish releasing handles
            // The relaunched instance must not be refused by the single-instance
            // guard: clear the dead instance's lockfile AND wait until its
            // heartbeat is provably stale (> 6 s old, the guard's own threshold).
            "if(Test-Path $lockFile){ Remove-Item -LiteralPath $lockFile -Force -ErrorAction SilentlyContinue }\n" +
            "$lockDeadline=(Get-Date).AddSeconds(15)\n" +
            "while((Get-Date) -lt $lockDeadline){ if(-not (Test-Path $lockFile)){break}; $age=((Get-Date)-[datetime](Get-Item -LiteralPath $lockFile).LastWriteTime).TotalMilliseconds; if($age -gt 7000){break}; Start-Sleep -Milliseconds 500 }\n" +
            "if(Test-Path $lockFile){ Remove-Item -LiteralPath $lockFile -Force -ErrorAction SilentlyContinue }\n" +
            "New-Item -ItemType Directory -Force -Path $backup | Out-Null\n" +
            "Get-ChildItem -LiteralPath $appDir -Include *.exe,*.dll,*.json -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $backup -Force }\n" +
            "Expand-Archive -LiteralPath $zip -DestinationPath $appDir -Force\n" +
            "Start-Process -FilePath (Join-Path $appDir 'NvidiaMonitor.exe') -WorkingDirectory $appDir\n" +
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

        // Everything is staged; quit so the helper can take over the files.
        System.Windows.Application.Current?.Dispatcher.Invoke(
            () => System.Windows.Application.Current.Shutdown());
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
