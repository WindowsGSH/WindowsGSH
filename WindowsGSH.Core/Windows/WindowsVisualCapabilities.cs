using System.Runtime.InteropServices;

namespace WindowsGSH.Core.Windows;

/// <summary>
/// Whether the running Windows install is a client SKU (Windows 10/11) or a Server SKU
/// (including domain controllers). Some DWM visual effects are documented by Microsoft as
/// client-only with no supported server version, so a capability decision based on build number
/// alone can't tell those apart - Windows Server routinely reports build numbers as high as or
/// higher than the client thresholds those effects require.
/// </summary>
public enum WindowsProductType
{
    Client,
    Server
}

/// <summary>
/// The two pieces of OS identity <see cref="WindowsVisualCapabilities"/> needs: the version
/// (specifically the build number) and whether this is a client or server SKU. Bundled as one
/// injectable value so tests can exercise every Client/Server x build-number combination without
/// needing to run on the real OS being modeled.
/// </summary>
public readonly record struct WindowsOsDescriptor(Version Version, WindowsProductType ProductType)
{
    /// <summary>
    /// The real running OS's version and product type. .NET reads <see cref="Environment.OSVersion"/>
    /// via RtlGetVersion rather than the classic, app-manifest-gated GetVersionEx shim, so - unlike
    /// .NET Framework - no supportedOS manifest entry is required for this to report the true build
    /// number. Product type is read the same way (also via RtlGetVersion, see <see cref="NativeMethods"/>)
    /// for the same reason - GetVersionEx-based product-type detection has the same manifest-gating
    /// problem the build number does.
    /// </summary>
    public static WindowsOsDescriptor Current { get; } = new(Environment.OSVersion.Version, DetectProductType());

    private static WindowsProductType DetectProductType()
    {
        try
        {
            var info = new NativeMethods.RTL_OSVERSIONINFOEXW
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf<NativeMethods.RTL_OSVERSIONINFOEXW>()
            };
            var result = NativeMethods.RtlGetVersion(ref info);
            return MapProductType(result, info.wProductType);
        }
        catch
        {
            // RtlGetVersion itself threw (e.g. the P/Invoke couldn't resolve). Same fail-closed
            // reasoning as MapProductType's failure branch - see there.
            return WindowsProductType.Server;
        }
    }

    /// <summary>
    /// Pure mapping from a raw RtlGetVersion result to a product type, extracted so the
    /// fail-closed path is directly unit-testable without needing to fake the native call
    /// itself. <paramref name="rtlGetVersionResult"/> is RtlGetVersion's own return value (an
    /// NTSTATUS; 0 is success) and <paramref name="wProductType"/> is the struct field it fills
    /// in on success.
    /// </summary>
    internal static WindowsProductType MapProductType(int rtlGetVersionResult, byte wProductType)
    {
        if (rtlGetVersionResult != 0)
        {
            // RtlGetVersion failed - the product type genuinely can't be determined. Treat it as
            // Server rather than Client: Mica/system-backdrop/dark-title-bar are all Client-only
            // per Microsoft's own documentation, so defaulting the unknown case to Server makes
            // those fail closed instead of being enabled on a system whose type was never
            // actually verified.
            return WindowsProductType.Server;
        }

        return wProductType == NativeMethods.VER_NT_WORKSTATION
            ? WindowsProductType.Client
            : WindowsProductType.Server;
    }

    private static class NativeMethods
    {
        internal const byte VER_NT_WORKSTATION = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct RTL_OSVERSIONINFOEXW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll")]
        internal static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW versionInfo);
    }
}

/// <summary>
/// What the running Windows install supports for WPF-UI's Windows-11-only visual effects
/// (Mica/system backdrop, DWM-rounded window corners, the dark title bar attribute). Views must
/// check this instead of branching on <see cref="Environment.OSVersion"/> or product type
/// themselves, so every OS-version/SKU threshold lives in one place.
/// </summary>
public interface IWindowsVisualCapabilities
{
    bool SupportsMica { get; }
    bool SupportsSystemBackdrop { get; }
    bool SupportsRoundedCorners { get; }
    bool SupportsDarkTitleBar { get; }
}

public sealed class WindowsVisualCapabilities : IWindowsVisualCapabilities
{
    // Windows 10 1809 (October 2018 Update) client - first build where the undocumented
    // DWMWA_USE_IMMERSIVE_DARK_MODE window attribute value actually took effect. Microsoft's own
    // documentation only lists this attribute from Windows 11 build 22000 and documents no
    // corresponding Windows Server support at all - so this threshold (and the attribute call a
    // later sub-chunk will make behind it) is treated as Client-only/best-effort, gated the same
    // way as Mica/the system backdrop below, not as an officially-supported Server feature.
    internal const int Windows10DarkTitleBarBuild = 17763;

    // Windows 11 21H2 (Client) - DWM began rounding top-level window corners automatically
    // (DWMWA_WINDOW_CORNER_PREFERENCE).
    internal const int Windows11Build = 22000;

    // Windows Server 2022 - Microsoft documents this as the minimum supported Server release for
    // DWMWA_WINDOW_CORNER_PREFERENCE, distinct from (and lower than) the Client threshold above:
    // Server 2022's build (20348) predates Windows 11's own build 22000, so a single build-only
    // threshold shared between Client and Server would wrongly report Server 2022 as unsupported.
    internal const int WindowsServer2022RoundedCornersBuild = 20348;

    // Windows 11 22H2 - the documented minimum client for DWMWA_SYSTEMBACKDROP_TYPE (which
    // DWMSBT_MAINWINDOW/Mica and the Acrylic/Tabbed backdrop variants both go through) is build
    // 22621, not 22000. Build 22000 had other, undocumented Mica techniques that WPF-UI has its
    // own compatibility handling for, but that's a different thing from this documented API, so
    // both Mica and the system backdrop use this same, later threshold rather than the earlier
    // Windows 11 21H2 one.
    internal const int Windows11_22H2Build = 22621;

    public static WindowsVisualCapabilities Current { get; } = new(WindowsOsDescriptor.Current);

    public bool SupportsMica { get; }
    public bool SupportsSystemBackdrop { get; }
    public bool SupportsRoundedCorners { get; }
    public bool SupportsDarkTitleBar { get; }

    public WindowsVisualCapabilities(WindowsOsDescriptor os)
    {
        // Windows 11 is still major.minor 10.0 in NT version numbering - only the build number
        // tells Windows 10 and 11 apart. Anything outside that major.minor family (a much older
        // Windows, or a fabricated/misreported version) must not fall through to a build-number
        // comparison that was only ever validated against the Windows 10/11 numbering scheme -
        // e.g. a spoofed/misreported Version(6, 3, 22621) must not read as "supports everything."
        var isWindows10OrLaterFamily = os.Version.Major == 10 && os.Version.Minor == 0;
        var build = isWindows10OrLaterFamily ? os.Version.Build : 0;
        var isClient = os.ProductType == WindowsProductType.Client;

        // Dark title bar, Mica, and the system-backdrop API are all Client-only per Microsoft's
        // own documentation (DWMWA_USE_IMMERSIVE_DARK_MODE and DWM_SYSTEMBACKDROP_TYPE both list
        // no supported Server version) - all three fail closed on Server regardless of build
        // number, rather than assuming a high server build number implies desktop-shell support
        // that was never verified to exist there.
        SupportsDarkTitleBar = isClient && build >= Windows10DarkTitleBarBuild;
        SupportsMica = isClient && build >= Windows11_22H2Build;
        SupportsSystemBackdrop = isClient && build >= Windows11_22H2Build;

        // Rounded-corner preference is different: Microsoft documents it as supported on both
        // Client (from Windows 11 build 22000) and Server (from Windows Server 2022, build
        // 20348) - two different, independently-documented thresholds rather than one shared
        // build number, since Server 2022's build predates Windows 11's.
        SupportsRoundedCorners = build >= (isClient ? Windows11Build : WindowsServer2022RoundedCornersBuild);
    }
}
