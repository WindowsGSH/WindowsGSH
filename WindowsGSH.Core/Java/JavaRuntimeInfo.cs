namespace WindowsGSH.Core.Java;

public sealed record JavaRuntimeInfo(
    string ExecutablePath,
    int? MajorVersion,
    string RawVersionText,
    string Vendor,
    string? ErrorMessage)
{
    public bool Found => string.IsNullOrWhiteSpace(ErrorMessage);

    public static JavaRuntimeInfo Missing(string errorMessage)
    {
        return new JavaRuntimeInfo(string.Empty, null, string.Empty, string.Empty, errorMessage);
    }
}
