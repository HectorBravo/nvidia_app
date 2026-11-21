// NvmlTest — headless NVML CLI verifier.
//
// Usage:
//   NvmlTest --print
//   NvmlTest --print [--watch [<seconds>]]
//   NvmlTest --probe
//   NvmlTest --update
// --print prints an nvidia-smi-style table once and exits 0 on success, or 1
// when NVML / a GPU is unavailable (reason on stdout). With --watch it keeps
// clearing and re-printing every <seconds> (default 1 s when the value is
// omitted), like `watch nvidia-smi`; Ctrl-C stops cleanly (exit 0).
// --probe dumps the raw per-process-utilization API return codes (see
// NvmlProbe) and exits.
// --update checks GitHub Releases for a newer published build of this tool, then
// downloads + installs it over the current directory (a background helper does
// the swap once this process exits; no relaunch). Exit 0 = updated or already
// up to date, 3 = update failed, 1 = NVML unavailable while printing.
// Release builds also print a blue "new version available" line under the table
// when a newer cli/ release exists (dev builds never check).
// Missing / bad args exit 2.
using System.Globalization;
using HectorBravo.Nvidia.Core;
using HectorBravo.Nvidia.Test;

// ── arg parsing (flags are case-insensitive) ─────────────────────
var print = false;
var probe = false;
var update = false;
double? interval = null;
var unknown = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (a.Equals("--print", StringComparison.OrdinalIgnoreCase))
    {
        print = true;
    }
    else if (a.Equals("--probe", StringComparison.OrdinalIgnoreCase))
    {
        probe = true;
    }
    else if (a.Equals("--update", StringComparison.OrdinalIgnoreCase))
    {
        update = true;
    }
    else if (a.Equals("--watch", StringComparison.OrdinalIgnoreCase))
    {
        // --watch implies --print. The seconds value is optional:
        //   --watch 5      → 5 s interval
        //   --watch        → 1 s (default when no value is supplied)
        //   --watch abc    → error (an explicit non-numeric value is a mistake)
        if (i + 1 >= args.Length)
        {
            print = true; // watch implies print
            interval = 1; // default live-feed interval
        }
        else if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            print = true; // watch implies print
            interval = Math.Max(secs, 0.1); // clamp to avoid a busy loop
        }
        else
        {
            Console.Error.WriteLine("Error: --watch requires a numeric seconds value (or omit it for the 1s default)");
            return 2;
        }
    }
    else
    {
        unknown.Add(a);
    }
}

// No recognized mode, or anything we don't understand → usage on stderr.
if (!print && !probe && !update || unknown.Count > 0)
{
    Console.Error.WriteLine("Usage: NvmlTest --print [--watch [<seconds>]] | NvmlTest --probe | NvmlTest --update  (no seconds value → 1 s)");
    return 2;
}

// Blue console text, restored afterwards — used only for the update notice.
static void WriteBlueLine(string message)
{
    var previous = Console.ForegroundColor;
    try
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(message);
    }
    catch (Exception)
    {
        Console.WriteLine(message); // no console buffer (redirected output): plain text is fine
    }
    finally
    {
        try { Console.ForegroundColor = previous; } catch (Exception) { }
    }
}

// ── update service (shared by --update and the blue notice) ───────
var updater = new UpdateService();

// --update: self-update and exit — no table, no NVML unless already printing.
if (update && !print && !probe)
{
    Console.WriteLine($"Build: {UpdateService.BuildStamp:yyyy-MM-dd HH:mm}");
    var progress = new Progress<string>(Console.WriteLine);
    try
    {
        var info = await updater.CheckAsync();
        if (info is null)
        {
            // Either we are the newest published build, or the check could not run /
            // this is a dev build with no release identity. Say which one we know.
            Console.WriteLine(UpdateService.ReleaseZipName.Length == 0
                ? "No update: this build has no release identity (dev build)."
                : "Already up to date.");
            return 0;
        }

        Console.WriteLine($"New version available: {info.Tag} ({info.ZipName})");
        await updater.DownloadAndApplyAsync(info, progress);
        Console.WriteLine("Update staged — the files are replaced once this process exits.");
        Console.WriteLine("Run NvmlTest.exe again when it finishes to use the new version.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"update failed: {ex.Message}");
        Console.Error.WriteLine("Nothing was replaced; the current build is untouched.");
        return 3;
    }
}

// --probe: raw NVML diagnostic, independent of the table printer.
if (probe)
{
    NvmlProbe.Run();
    return 0;
}

// Build stamp — same compile date the WPF dashboard shows in its header
// (LastWriteTime of the executing assembly), so releases can be matched to
// the binary by eye.
Console.WriteLine($"Build: {new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).LastWriteTime:yyyy-MM-dd HH:mm}");

var svc = new NvmlCoreService(); // log: null (headless, no AppLog)
await svc.InitializeAsync();

if (!svc.IsAvailable)
{
    Console.WriteLine(svc.StatusMessage);
    return 1;
}

// One-shot: print the table once and exit.
if (interval is null)
{
    // Kick off the update check in parallel with the NVML refresh; it never blocks
    // longer than its own HTTP timeout, and any failure means "no notice".
    var checkTask = updater.CheckAsync();

    var snap = await svc.RefreshAsync();
    var (ramUsed, ramTotal) = SystemMemory.Read(); // machine-wide, not per-GPU
    Console.WriteLine(SmiFormatter.Format(snap, ramUsed, ramTotal));

    var info = await CheckOrDefaultAsync(checkTask);
    if (info is not null) PrintNotice(info);

    await svc.ShutdownAsync();
    return 0;
}

// Live mode: clear + re-render every interval until Ctrl-C.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // don't hard-kill; let the loop shut down cleanly
    cts.Cancel();
};

// Update notice state: checked at startup and re-checked every 10 min while the
// feed runs, so a long-lived --watch sees a release published after it started.
UpdateService.UpdateInfo? updateInfo = null;
var nextCheckAt = DateTime.MinValue;
Task<UpdateService.UpdateInfo?>? checkTask2 = updater.CheckAsync();

try
{
    while (!cts.IsCancellationRequested)
    {
        if (checkTask2 is { IsCompleted: true })
        {
            updateInfo = await CheckOrDefaultAsync(checkTask2);
            checkTask2 = null;
        }
        else if (checkTask2 is null && DateTime.UtcNow >= nextCheckAt)
        {
            checkTask2 = updater.CheckAsync();
            nextCheckAt = DateTime.UtcNow.AddMinutes(10);
        }

        try
        {
            Console.Clear(); // wipe so the re-render feels "live", like `watch`
        }
        catch (Exception)
        {
            // Cosmetic only: skip the wipe when stdout has no console buffer
            // (e.g. the output is redirected to a file) and carry on.
        }
        Console.WriteLine($"NvmlTest --watch (every {interval.Value.ToString("0.##", CultureInfo.InvariantCulture)}s) — Ctrl-C to stop");
        try
        {
            var snap = await svc.RefreshAsync();
            var (ramUsed, ramTotal) = SystemMemory.Read(); // re-read every tick so the live feed tracks the OS
            Console.WriteLine(SmiFormatter.Format(snap, ramUsed, ramTotal));
        }
        catch (Exception ex) // NVML hiccup: warn on stderr, keep going; next tick retries
        {
            Console.Error.WriteLine($"warn: refresh failed — {ex.Message}; retrying next interval");
        }

        if (updateInfo is not null) PrintNotice(updateInfo);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(interval.Value), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C during the wait — the loop condition ends the iteration.
        }
    }
}
finally
{
    await svc.ShutdownAsync(); // guaranteed to run, even on an unexpected exit
}

return 0;

// The update notice must never take the table down with it: swallow anything
// the check threw (offline, rate limit, bad payload) as "no notice".
static async Task<UpdateService.UpdateInfo?> CheckOrDefaultAsync(Task<UpdateService.UpdateInfo?> task)
{
    try
    {
        return await task;
    }
    catch (Exception)
    {
        return null;
    }
}

// One blue line under the table — display only, `--update` does the install.
static void PrintNotice(UpdateService.UpdateInfo info) =>
    WriteBlueLine($"New version available: {info.Tag} ({info.ZipName}) — run: NvmlTest --update");

