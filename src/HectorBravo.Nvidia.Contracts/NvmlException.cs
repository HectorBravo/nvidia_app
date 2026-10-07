namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// A failure surfaced by the NVIDIA Management Library (NVML), carrying the
/// underlying <see cref="NvmlErrorCode"/> so callers can branch on it.
/// </summary>
public sealed class NvmlException : Exception
{
    public NvmlErrorCode ErrorCode { get; }
    public string? Function { get; }

    public NvmlException(NvmlErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public NvmlException(NvmlErrorCode errorCode, string function, string message)
        : base(message)
    {
        ErrorCode = errorCode;
        Function = function;
    }

    public NvmlException(NvmlErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public override string ToString()
        => Function is null
            ? base.ToString()
            : $"{base.ToString()} [nvml: {Function}]";
}

/// <summary>
/// NVML return codes. Values mirror <c>nvmlReturn_t</c> in <c>nvml.h</c>.
/// Only the codes the app reports/handles are enumerated here.
/// </summary>
public enum NvmlErrorCode
{
    Success = 0,
    Uninitialized = 1,
    NoPermission = 3,
    NotFound = 4,
    InsufficientSize = 8,
    DriverNotFound = 10,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    InUse = 600,
    IrqIssue = 602,
    GpuIsLost = 702,
    ResetRequired = 703,
    WasReset = 704,
    UnknownError = 999,
    NotSupported = 1024,
}
