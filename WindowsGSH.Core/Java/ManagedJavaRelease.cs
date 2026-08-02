namespace WindowsGSH.Core.Java;

public sealed record ManagedJavaRelease(
    string Vendor,
    int MajorVersion,
    string Architecture,
    string ReleaseVersion,
    string DownloadUrl,
    string ArchiveSha256,
    long MaxArchiveSizeBytes,
    string LicenseUrl,
    string SourceUrl)
{
    public string Id => $"{Vendor}-{MajorVersion}-{Architecture}";

    public string InstallSubPath => Path.Combine(Vendor, MajorVersion.ToString(), Architecture);
}
