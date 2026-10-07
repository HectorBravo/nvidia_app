namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// A formatted value with an explicit "N/A" concept, so the UI can render
/// missing data the way nvidia-smi does (dashes) instead of zeros.
/// </summary>
public sealed record DisplayValue
{
    public bool IsAvailable { get; init; } = true;
    public string Value { get; init; } = string.Empty;

    public static DisplayValue Na => new()
    {
        IsAvailable = false,
        Value = "N/A",
    };

    public static DisplayValue Ok(string value) => new()
    {
        IsAvailable = true,
        Value = value,
    };
}
