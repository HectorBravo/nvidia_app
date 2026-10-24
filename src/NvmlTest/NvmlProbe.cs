// Raw diagnostic for the per-process GPU-utilization APIs in the installed
// nvml.dll (bypasses the NvmlCoreService contract layer so the real NVML
// return codes are visible):
//
//   NvmlTest --probe
//
// For every GPU it prints:
//   * the legacy nvmlDeviceGetProcessUtilization NULL-probe (rc, count) and a
//     real read if samples exist, and
//   * the batched nvmlDeviceGetProcessesUtilizationInfo with the currently
//     used version constant, the recomputed NVML_STRUCT_VERSION sizes, and a
//     size sweep — so it is unambiguous which (if any) version the installed
//     driver accepts.
//
// 13.x nvml.h error table: 0 SUCCESS, 2 INVALID_ARGUMENT, 3 NOT_SUPPORTED,
// 6 NOT_FOUND, 7 INSUFFICIENT_SIZE, 8 INSUFFICIENT_POWER, 15 GPU_IS_LOST,
// 21 NO_DATA, 25 ARGUMENT_VERSION_MISMATCH.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HectorBravo.Nvidia.Test;

internal static class NvmlProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ShutdownFn();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int InitFn();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CountFn(IntPtr countPtr);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandleFn(uint index, IntPtr devicePtr);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LegacyUtilFn(IntPtr device, IntPtr samples, IntPtr countPtr, ulong lastSeen);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int BatchUtilFn(IntPtr device, IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlVersionFn(IntPtr buffer, uint length);

    private static string[] SearchPaths =>
    [
        Path.Combine(Environment.GetEnvironmentVariable("CommonProgramFiles(x86)") ?? @"C:\Program Files (x86)\Common Files",
            "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files",
            "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        @"C:\Windows\System32\nvml.dll",
        "nvml.dll",
    ];

    private static readonly byte[] Z24 = new byte[24];

    private static void Zero24(IntPtr ptr) =>
        Marshal.Copy(Z24, 0, ptr, 24);

    private static uint U32(IntPtr ptr, int offset) =>
        (uint)Marshal.ReadInt32(IntPtr.Add(ptr, offset));

    private static T? Resolve<T>(IntPtr module, params string[] names) where T : Delegate
    {
        foreach (var name in names)
        {
            var fp = GetProcAddress(module, name);
            if (fp != IntPtr.Zero)
            {
                return (T)Marshal.GetDelegateForFunctionPointer(fp, typeof(T));
            }
        }

        return null;
    }

    public static void Run()
    {
        IntPtr module = IntPtr.Zero;
        var path = string.Empty;
        foreach (var p in SearchPaths)
        {
            module = LoadLibraryW(p);
            if (module != IntPtr.Zero)
            {
                path = p;
                break;
            }
        }

        if (module == IntPtr.Zero)
        {
            Console.WriteLine("nvml.dll not found in any known location.");
            return;
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        Console.WriteLine($"loaded: {path}  fileVersion={versionInfo.FileVersion}  productVersion={versionInfo.ProductVersion}");

        var init = Resolve<InitFn>(module, "nvmlInit_v2", "nvmlInit");
        if (init == null || init() != 0)
        {
            Console.WriteLine("nvmlInit unavailable or failed.");
            return;
        }

        var countFn = Resolve<CountFn>(module, "nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
        var handleFn = Resolve<HandleFn>(module, "nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
        var legacy = Resolve<LegacyUtilFn>(module, "nvmlDeviceGetProcessUtilization");
        var batch = Resolve<BatchUtilFn>(module, "nvmlDeviceGetProcessesUtilizationInfo");
        var nvmlVersion = Resolve<NvmlVersionFn>(module, "nvmlSystemGetNvmlVersion");

        if (nvmlVersion != null)
        {
            var vbuf = Marshal.AllocHGlobal(80);
            try
            {
                nvmlVersion(vbuf, 80);
                Console.WriteLine($"nvmlVersion: {Marshal.PtrToStringAnsi(vbuf)}");
            }
            finally
            {
                Marshal.FreeHGlobal(vbuf);
            }
        }

        var gpuCount = 0;
        var cPtr = Marshal.AllocHGlobal(4);
        try
        {
            if (countFn != null && countFn(cPtr) == 0)
            {
                gpuCount = Marshal.ReadInt32(cPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(cPtr);
        }

        Console.WriteLine($"GPUs: {gpuCount}");

        var hPtr = Marshal.AllocHGlobal(IntPtr.Size);
        var count4 = Marshal.AllocHGlobal(4);
        var sampleArr = Marshal.AllocHGlobal(25 * 32);
        var batchInfo = Marshal.AllocHGlobal(24);
        var batchArr = Marshal.AllocHGlobal(25 * 40);
        try
        {
            for (var i = 0; i < gpuCount; i++)
            {
                var handle = IntPtr.Zero;
                if (handleFn != null && handleFn((uint)i, hPtr) == 0)
                {
                    handle = Marshal.ReadIntPtr(hPtr);
                }

                if (handle == IntPtr.Zero)
                {
                    Console.WriteLine($"-- GPU[{i}]: no handle");
                    continue;
                }

                Console.WriteLine($"-- GPU[{i}]");

                // Legacy 13.x signature: (device, samples*, count*, lastSeen)
                if (legacy != null)
                {
                    // Exact two-phase protocol the NvmlCoreService reader uses:
                    // probe(NULL) -> rc==7 + required count -> re-call with that count.
                    Marshal.WriteInt32(count4, 0);
                    var lrc = legacy(handle, IntPtr.Zero, count4, 0);
                    var ln = Marshal.ReadInt32(count4);
                    Console.WriteLine($"  legacy probe(NULL): rc={lrc} required={ln}");

                    if (ln > 0)
                    {
                        var n = Math.Min(ln, 256); // same cap as the reader
                        // 2880 bytes covers 72 x 40 (v1 layout) with headroom.
                        var bigArr = Marshal.AllocHGlobal(2880);
                        try
                        {
                            Marshal.WriteInt32(count4, n);
                            var r2 = legacy(handle, bigArr, count4, 0);
                            var w = Marshal.ReadInt32(count4);
                            Console.WriteLine($"  legacy read(n={n}): rc={r2} written={w}");

                            if (i == 2)
                            {
                                // Full hex dump of the first 512 bytes (only for
                                // the GPU under dmon load) to decode the record layout.
                                Console.WriteLine("  hex[0..511]:");
                                var hex = new char[2500];
                                var hp = 0;
                                for (var b = 0; b < 512; b++)
                                {
                                    if (b % 16 == 0)
                                    {
                                        var label = $"  {b:X3}: ";
                                        for (var k = 0; k < label.Length; k++)
                                        {
                                            hex[hp++] = label[k];
                                        }
                                    }

                                    var byteVal = Marshal.ReadByte(bigArr, b);
                                    hex[hp++] = (char)('0' + (byteVal >> 4 & 15));
                                    hex[hp++] = (char)('0' + (byteVal & 15));
                                    if (b % 16 != 15)
                                    {
                                        hex[hp++] = (char)' ';
                                    }
                                }

                                Console.WriteLine(new string(hex, 0, hp).TrimEnd());
                            }

                            // Sum u32 fields over every candidate record stride,
                            // to see which stride/offset the driver actually used.
                            var totalBytes = Math.Min(2880, w * 40);
                            Console.WriteLine($"  stride sums over first {totalBytes} bytes:");
                            foreach (var stride in new[] { 16, 24, 32, 40 })
                            {
                                var parts = new List<string>();
                                foreach (var fieldOff in new[] { 0, 4, 8, 12, 16, 20, 24, 28 })
                                {
                                    var sum = 0;
                                    var cnt = 0;
                                    for (var o = 0; o + stride <= totalBytes; o += stride)
                                    {
                                        if (o + fieldOff + 4 <= totalBytes)
                                        {
                                            sum += Marshal.ReadInt32(bigArr, o + fieldOff);
                                            cnt++;
                                        }
                                    }

                                    if (cnt > 0)
                                    {
                                        parts.Add($"o{fieldOff}:{sum}");
                                    }
                                }

                                Console.WriteLine($"    stride {stride}: " + string.Join("  ", parts));
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(bigArr);
                        }
                    }
                }
                else
                {
                    Console.WriteLine("  legacy: symbol not present");
                }

                if (batch != null)
                {
                    // NVML_STRUCT_VERSION(data, ver) = sizeof(struct v{ver})
                    //   | ((ver & 0xFF) << 16) | (((ver >> 8) & 0xFF) << 24)
                    // v0 struct = 1008 bytes, v1 struct = 1016 bytes.
                    static uint Enc(uint size, uint n) =>
                        size | ((n & 0xFF) << 16) | (((n >> 8) & 0xFF) << 24);

                    var candidates = new (string label, uint version)[]
                    {
                        ("v1 1016|1<<16", Enc(1016, 1)),
                        ("v0 1008|0", Enc(1008, 0)),
                        ("v2 1016|2<<16", Enc(1016, 2)),
                        ("v24 1016|24<<16", Enc(1016, 24)),
                        ("v1std 24|1<<24", 24 | (1u << 24)),
                        ("v1std 32|1<<24", 32 | (1u << 24)),
                        ("v1std 48|1<<24", 48 | (1u << 24)),
                    };
                    foreach (var (label, version) in candidates)
                    {
                        Zero24(batchInfo);
                        Marshal.WriteInt32(batchInfo, 0, (int)version);
                        var brc = batch(handle, batchInfo);
                        var bc = Marshal.ReadInt32(batchInfo, 4);
                        Console.WriteLine($"  batch probe {label} (version=0x{version:X8}): rc={brc} count={bc}");
                        if (brc == 0 && bc > 0)
                        {
                            var n = Math.Min(bc, 25);
                            Marshal.WriteInt32(batchInfo, 4, n);
                            Marshal.WriteIntPtr(batchInfo, 16, batchArr);
                            var r2 = batch(handle, batchInfo);
                            var w = Marshal.ReadInt32(batchInfo, 4);
                            Console.WriteLine($"  batch read ({label}): rc={r2} written={w}");
                            for (var s = 0; s < w; s++)
                            {
                                var off = s * 40;
                                var pid = U32(batchArr, off + 8);
                                var sm = U32(batchArr, off + 12);
                                var mem = U32(batchArr, off + 16);
                                Console.WriteLine($"    info[{s}]: pid={pid} sm={sm} mem={mem}");
                            }
                        }
                    }

                    Console.WriteLine("  version sweep (sizes 1008/1016, N=1..6):");
                    var any = false;
                    for (uint size = 1008; size <= 1016; size += 8)
                    {
                        for (uint n = 1; n <= 6; n++)
                        {
                            var version = Enc(size, n);
                            Zero24(batchInfo);
                            Marshal.WriteInt32(batchInfo, 0, (int)version);
                            var brc = batch(handle, batchInfo);
                            var bc = Marshal.ReadInt32(batchInfo, 4);
                            if (brc == 0)
                            {
                                any = true;
                                Console.WriteLine($"    size={size} N={n} (0x{version:X8}): rc=0 count={bc}  <== ACCEPTED");
                            }
                        }
                    }

                    if (!any)
                    {
                        Console.WriteLine("    (no version accepted)");
                    }
                }
                else
                {
                    Console.WriteLine("  batch: symbol not present");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(hPtr);
            Marshal.FreeHGlobal(count4);
            Marshal.FreeHGlobal(sampleArr);
            Marshal.FreeHGlobal(batchInfo);
            Marshal.FreeHGlobal(batchArr);
        }

        Resolve<ShutdownFn>(module, "nvmlShutdown")?.Invoke();
    }
}
