// NvmlTest — headless NVML CLI verifier.
//
// Usage:
//   NvmlTest --print
// Prints an nvidia-smi-style table for every GPU and exits 0 on success,
// or 1 when NVML / a GPU is unavailable (with a reason on stdout).
using HectorBravo.Nvidia.Core;
using HectorBravo.Nvidia.Test;

var print = args.Contains("--print");

if (!print)
{
    Console.Error.WriteLine("Usage: NvmlTest --print");
    return 2;
}

var svc = new NvmlCoreService(); // log: null (headless, no AppLog)
await svc.InitializeAsync();

if (!svc.IsAvailable)
{
    Console.WriteLine(svc.StatusMessage);
    return 1;
}

var snap = await svc.RefreshAsync();
Console.WriteLine(SmiFormatter.Format(snap));

await svc.ShutdownAsync();
return 0;
