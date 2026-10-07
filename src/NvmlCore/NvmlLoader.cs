using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HectorBravo.Nvidia.Core;

/// <summary>
/// Win32 P/Invoke surface: LoadLibraryW / GetProcAddress. Only these two
/// native calls are hard-coded; every NVML entry point is resolved by name
/// through <see cref="NvmlLoader"/>.
/// </summary>
internal static class Win32
{
    private const string Kernel = "kernel32.dll";

    [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string lpFileName);

    [DllImport(Kernel, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    public static IntPtr LoadLibrary(string name)
    {
        var h = LoadLibraryW(name);
        if (h == IntPtr.Zero)
        {
            throw new DllNotFoundException($"LoadLibraryW failed for '{name}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        return h;
    }
}

/// <summary>
/// Loads <c>nvml.dll</c> and resolves each NVML entry point by name into a
/// typed delegate, preferring the <c>_v2</c>/<c>_v3</c> symbol and falling
/// back to the unversioned name. Resolution is lazy and thread-safe; a
/// missing symbol yields a null delegate (the caller degrades that metric
/// to N/A rather than crashing).
/// </summary>
internal sealed class NvmlLoader
{
    private readonly object _gate = new();
    private IntPtr _module = IntPtr.Zero;
    private readonly Dictionary<string, Delegate> _cache = new(StringComparer.Ordinal);

    /// <summary>nvml.dll search order.</summary>
    private static string[] SearchPaths =>
    [
        Path.Combine(Environment.GetEnvironmentVariable("CommonProgramFiles(x86)") ?? @"C:\Program Files (x86)\Common Files",
            "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files",
            "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        @"C:\Windows\System32\nvml.dll",
        "nvml.dll",
    ];

    public string? LoadedPath { get; private set; }

    public bool IsLoaded => _module != IntPtr.Zero;

    /// <summary>Tries each known location; returns the loaded path or null.</summary>
    public bool TryLoad(Action<string>? log = null)
    {
        lock (_gate)
        {
            if (_module != IntPtr.Zero)
            {
                return true;
            }

            foreach (var path in SearchPaths)
            {
                try
                {
                    _module = Win32.LoadLibrary(path);
                    LoadedPath = path;
                    log?.Invoke($"nvml.dll loaded from {path}");
                    return true;
                }
                catch (DllNotFoundException ex)
                {
                    log?.Invoke($"nvml.dll not found at '{path}': {ex.Message}");
                }
            }

            log?.Invoke("nvml.dll could not be loaded from any known location.");
            return false;
        }
    }

    /// <summary>
    /// Resolves the first existing symbol in <paramref name="candidates"/>
    /// (versioned first) into a delegate of type <paramref name="delType"/>.
    /// Caches the result by the delegate type name. Returns null if no
    /// candidate symbol exists (the caller degrades that metric to N/A).
    /// </summary>
    public Delegate? Get(Type delType, params string[] candidates)
    {
        if (_module == IntPtr.Zero)
        {
            return null;
        }

        lock (_gate)
        {
            // Key by type AND first candidate symbol so that distinct symbols
            // bound to the same delegate type (e.g. compute vs graphics running
            // processes) do not collide in the cache.
            var key = candidates.Length > 0
                ? delType.FullName! + "::" + candidates[0]
                : delType.FullName!;
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            foreach (var name in candidates)
            {
                var fnPtr = Win32.GetProcAddress(_module, name);
                if (fnPtr == IntPtr.Zero)
                {
                    continue;
                }

                var del = Marshal.GetDelegateForFunctionPointer(fnPtr, delType);
                _cache[key] = del;
                return del;
            }

            return null;
        }
    }

    public void Unload(Action<string>? log = null)
    {
        lock (_gate)
        {
            if (_module == IntPtr.Zero)
            {
                return;
            }

            try
            {
                _cache.Clear();
                log?.Invoke($"nvml.dll unloaded ({LoadedPath})");
            }
            finally
            {
                _module = IntPtr.Zero;
            }
        }
    }
}
