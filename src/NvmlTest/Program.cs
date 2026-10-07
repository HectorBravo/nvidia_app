// NvmlTest — headless NVML CLI verifier.
//
// Usage:
//   NvmlTest --print
//   NvmlTest --print [--watch <seconds>]
// --print prints an nvidia-smi-style table once and exits 0 on success, or 1
// when NVML / a GPU is unavailable (reason on stdout). With --watch it keeps
// clearing and re-printing every <seconds>, like `watch nvidia-smi`; Ctrl-C
// stops cleanly (exit 0). Missing / bad args exit 2.
using System.Globalization;
using HectorBravo.Nvidia.Core;
using HectorBravo.Nvidia.Test;

// ── arg parsing (flags are case-insensitive) ─────────────────────
var print = false;
double? interval = null;
var unknown = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (a.Equals("--print", StringComparison.OrdinalIgnoreCase))
    {
        print = true;
    }
    else if (a.Equals("--watch", StringComparison.OrdinalIgnoreCase))
    {
        // --watch implies --print, and requires a numeric value right after it.
        if (i + 1 >= args.Length ||
            !double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            Console.Error.WriteLine("Error: --watch requires a numeric seconds value");
            return 2;
        }
        print = true; // watch implies print
        interval = Math.Max(secs, 0.1); // clamp to avoid a busy loop
    }
    else
    {
        unknown.Add(a);
    }
}

// No recognized mode, or anything we don't understand → usage on stderr.
if (!print || unknown.Count > 0)
{
    Console.Error.WriteLine("Usage: NvmlTest --print [--watch <seconds>]");
    return 2;
}

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
    var snap = await svc.RefreshAsync();
    Console.WriteLine(SmiFormatter.Format(snap));
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

try
{
    while (!cts.IsCancellationRequested)
    {
        Console.Clear(); // wipe so the re-render feels "live", like `watch`
        Console.WriteLine($"NvmlTest --watch (every {interval.Value.ToString("0.##", CultureInfo.InvariantCulture)}s) — Ctrl-C to stop");
        try
        {
            var snap = await svc.RefreshAsync();
            Console.WriteLine(SmiFormatter.Format(snap));
        }
        catch (Exception ex) // NVML hiccup: warn on stderr, keep going; next tick retries
        {
            Console.Error.WriteLine($"warn: refresh failed — {ex.Message}; retrying next interval");
        }

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
