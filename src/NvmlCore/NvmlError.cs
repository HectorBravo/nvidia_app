namespace HectorBravo.Nvidia.Core;

/// <summary>
/// Header-accurate NVML return codes from nvml.h 13.4.92, plus a short
/// human description used for AppLog / StatusMessage. The Contracts
/// <c>NvmlErrorCode</c> enum's numbers do not match the header, so this
/// local set is the source of truth for interpreting raw return values.
/// </summary>
internal static class NvmlErr
{
    public const int Success = 0;
    public const int Uninitialized = 1;
    public const int InvalidArgument = 2;
    public const int NotSupported = 3;
    public const int NoPermission = 4;
    public const int AlreadyInitialized = 5;
    public const int NotFound = 6;
    public const int InsufficientSize = 7;
    public const int InsufficientPower = 8;
    public const int DriverNotLoaded = 9;
    public const int Timeout = 10;
    public const int IrqIssue = 11;
    public const int LibraryNotFound = 12;
    public const int FunctionNotFound = 13;
    public const int CorruptedInfoRom = 14;
    public const int GpuIsLost = 15;
    public const int Unknown = 999;

    public static string Name(int code) => code switch
    {
        Success => "SUCCESS",
        Uninitialized => "UNINITIALIZED",
        InvalidArgument => "INVALID_ARGUMENT",
        NotSupported => "NOT_SUPPORTED",
        NoPermission => "NO_PERMISSION",
        AlreadyInitialized => "ALREADY_INITIALIZED",
        NotFound => "NOT_FOUND",
        InsufficientSize => "INSUFFICIENT_SIZE",
        InsufficientPower => "INSUFFICIENT_POWER",
        DriverNotLoaded => "DRIVER_NOT_LOADED",
        Timeout => "TIMEOUT",
        IrqIssue => "IRQ_ISSUE",
        LibraryNotFound => "LIBRARY_NOT_FOUND",
        FunctionNotFound => "FUNCTION_NOT_FOUND",
        CorruptedInfoRom => "CORRUPTED_INFOROM",
        GpuIsLost => "GPU_IS_LOST",
        Unknown => "UNKNOWN",
        _ => $"CODE_{code}",
    };

    /// <summary>One-line reason for an unexpected NVML error, for logging.</summary>
    public static string Describe(int code) => code switch
    {
        DriverNotLoaded => "NVIDIA driver is not loaded.",
        LibraryNotFound => "NVML shared library could not be found or loaded.",
        FunctionNotFound => "This NVML version does not implement that function.",
        NoPermission => "The current user lacks permission for this operation.",
        NotSupported => "The operation is not supported on this device.",
        GpuIsLost => "The GPU has fallen off the bus or is otherwise inaccessible.",
        _ => $"NVML returned {Name(code)}.",
    };
}
