// NvmlTest — headless NVML CLI verifier.
//
// Usage:
//   NvmlTest --print
//   NvmlTest --print [--watch [<seconds>]]
//   NvmlTest --probe
// --print prints an nvidia-smi-style table once and exits 0 on success, or 1
// when NVML / a GPU is unavailable (reason on stdout). With --watch it keeps
// clearing and re-printing every <seconds> (default 1 s when the value is
// omitted), like `watch nvidia-smi`; Ctrl-C stops cleanly (exit 0).
// --probe dumps the raw per-process-utilization API return codes (see
// NvmlProbe) and exits.
// Missing / bad args exit 2.
using System.Globalization;
using HectorBravo.Nvidia.Core;
using HectorBravo.Nvidia.Test;

// ── arg parsing (flags are case-insensitive) ─────────────────────
var print = false;
var probe = false;
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
if (!print && !probe || unknown.Count > 0)
{
    Console.Error.WriteLine("Usage: NvmlTest --print [--watch [<seconds>]] | NvmlTest --probe  (no seconds value → 1 s)");
    return 2;
}

// --probe: raw NVML diagnostic, independent of the table printer.
if (probe)
{
    NvmlProbe.Run();
    return 0;
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
