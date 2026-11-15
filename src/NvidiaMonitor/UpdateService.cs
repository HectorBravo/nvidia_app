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
/// Release tags are produced by release.ps1 as "gui/&lt;yyyy-MM-dd-HHmm&gt;" — the
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
    private const int AssetFreshnessToleranceSeconds = 120; // ignore same-minute rebuild jitter

    private static readonly HttpClient Http = CreateHttp();

    private readonly AppLog? _log;

    public UpdateService(AppLog? log = null) => _log = log;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub API requires a User-Agent header; keep the request cheap and API-shaped.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NvidiaMonitor-updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The compile date (LastWriteTime) of the running exe — same stamp the header prints.</summary>
    public static DateTime BuildStamp =>
        new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).LastWriteTime;

    private static string BuildTag => "gui/" + BuildStamp.ToString("yyyy-MM-dd-HHmm");

    /// <summary>Metadata for an available update, once found.</summary>
    public sealed record UpdateInfo(DateTime PublishedAtUtc, string DownloadUrl, string ReleaseUrl);

    /// <summary>
    /// Queries GitHub for the release carrying this build's tag. Returns non-null
    /// only when that release exists AND its asset is clearly newer than our local
    /// exe (meaning we run an older compile). Any failure (offline, rate limit,
    /// parse) returns null — the check must never disturb the user.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var tag = BuildTag;
            var url = $"{RepoApi}/releases/tags/{Uri.EscapeDataString(tag)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 simply means "no published release for this build" → up to date.
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("assets", out var assets)
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

            DateTime publishedUtc = DateTime.UtcNow;
            if (asset.TryGetProperty("updated_at", out var updEl)
                && updEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(updEl.GetString(), out var parsed))
            {
                publishedUtc = parsed.UtcDateTime;
            }

            string releaseUrl = string.Empty;
            if (root.TryGetProperty("html_url", out var htmlEl) && htmlEl.ValueKind == JsonValueKind.String)
            {
                releaseUrl = htmlEl.GetString() ?? string.Empty;
            }

            // Only offer the update when the published asset is meaningfully newer
            // than our local exe (guards against same-minute rebuild jitter).
            var localUtc = BuildStamp.ToUniversalTime();
            if ((publishedUtc - localUtc).TotalSeconds <= AssetFreshnessToleranceSeconds)
            {
                return null;
            }

            _log?.Info($"Update available: release {tag} published {publishedUtc:yyyy-MM-dd HH:mm} UTC.");
            return new UpdateInfo(publishedUtc, dlEl.GetString()!, releaseUrl);
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
        var tag = BuildTag.Replace('/', '-');
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
        var script =
            "$ErrorActionPreference='Stop'\n" +
            $"$pid={pid}\n" +
            $"$zip='{Esc(zipPath)}'\n" +
            $"$appDir='{Esc(appDir)}'\n" +
            $"$backup='{Esc(backupDir)}'\n" +
            "$deadline=(Get-Date).AddSeconds(60)\n" +
            "while((Get-Date) -lt $deadline){ if(-not (Get-Process -Id $pid -ErrorAction SilentlyContinue)){break}; Start-Sleep -Milliseconds 500 }\n" +
            "Start-Sleep -Milliseconds 1000\n" + // let the OS finish releasing handles
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
