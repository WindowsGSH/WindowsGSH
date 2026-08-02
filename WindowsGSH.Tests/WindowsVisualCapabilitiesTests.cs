using WindowsGSH.Core.Windows;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class WindowsVisualCapabilitiesTests
{
    [Fact]
    public void Windows_10_pre_1809_client_supports_nothing()
    {
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, 17134), WindowsProductType.Client));

        Assert.False(capabilities.SupportsDarkTitleBar);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
        Assert.False(capabilities.SupportsRoundedCorners);
    }

    [Fact]
    public void Windows_10_1809_client_supports_dark_title_bar_only()
    {
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, WindowsVisualCapabilities.Windows10DarkTitleBarBuild), WindowsProductType.Client));

        Assert.True(capabilities.SupportsDarkTitleBar);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
        Assert.False(capabilities.SupportsRoundedCorners);
    }

    [Fact]
    public void Windows_10_latest_client_still_does_not_support_windows_11_only_effects()
    {
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, 19045), WindowsProductType.Client));

        Assert.True(capabilities.SupportsDarkTitleBar);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
        Assert.False(capabilities.SupportsRoundedCorners);
    }

    [Fact]
    public void Windows_11_21H2_client_supports_rounded_corners_but_not_mica_or_backdrop()
    {
        // Build 22000 is Windows 11's first build, but Microsoft documents
        // DWMWA_SYSTEMBACKDROP_TYPE's minimum supported client as build 22621 - Mica and the
        // system backdrop must not report as supported here even though this is already
        // Windows 11, because the documented API for them isn't present yet at this build.
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, WindowsVisualCapabilities.Windows11Build), WindowsProductType.Client));

        Assert.True(capabilities.SupportsDarkTitleBar);
        Assert.True(capabilities.SupportsRoundedCorners);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
    }

    [Fact]
    public void Windows_11_22H2_client_supports_everything()
    {
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, WindowsVisualCapabilities.Windows11_22H2Build), WindowsProductType.Client));

        Assert.True(capabilities.SupportsDarkTitleBar);
        Assert.True(capabilities.SupportsMica);
        Assert.True(capabilities.SupportsRoundedCorners);
        Assert.True(capabilities.SupportsSystemBackdrop);
    }

    [Fact]
    public void Windows_server_2019_does_not_support_mica_backdrop_or_dark_title_bar()
    {
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, 17763), WindowsProductType.Server));

        Assert.False(capabilities.SupportsDarkTitleBar);
        Assert.False(capabilities.SupportsRoundedCorners);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
    }

    [Fact]
    public void Windows_server_2022_supports_rounded_corners_but_not_mica_backdrop_or_dark_title_bar()
    {
        // Server 2022's build (20348) is below the Client rounded-corners threshold (22000), but
        // Microsoft documents Server 2022 itself as the minimum supported Server release for
        // DWMWA_WINDOW_CORNER_PREFERENCE - so this must still report true, via the separate,
        // lower Server threshold, not the Client one.
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, 20348), WindowsProductType.Server));

        Assert.False(capabilities.SupportsDarkTitleBar);
        Assert.True(capabilities.SupportsRoundedCorners);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
    }

    [Fact]
    public void Windows_server_2019_predates_the_server_rounded_corners_threshold_too()
    {
        // Unlike Server 2022 above, Server 2019's build (17763) is below Microsoft's documented
        // Server threshold (20348) as well, so this one still expects nothing enabled at all.
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, 17763), WindowsProductType.Server));

        Assert.False(capabilities.SupportsRoundedCorners);
    }

    [Fact]
    public void Windows_server_2025_high_build_still_does_not_support_mica_backdrop_or_dark_title_bar()
    {
        // The regression this test exists for: Server 2025's build (26100) is higher than both
        // the Windows 11 21H2 (22000) and 22H2 (22621) client thresholds. A build-number-only
        // decision would have reported Mica, the system backdrop, and the dark title bar as
        // supported here, even though Microsoft documents no supported server version for any of
        // the three - only product type stops that. Rounded-corner preference is documented as
        // server-supported separately, so it's still expected true here, driven by build number
        // alone same as on a client.
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(10, 0, 26100), WindowsProductType.Server));

        Assert.False(capabilities.SupportsDarkTitleBar);
        Assert.True(capabilities.SupportsRoundedCorners);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
    }

    [Fact]
    public void A_pre_windows_10_major_version_with_a_fabricated_high_build_supports_nothing()
    {
        // Only Windows 10/11's "10.0" major.minor family is modeled by the build-number
        // thresholds above. A misreported or fabricated version outside that family (e.g. a
        // Windows 8.1-style 6.3) must not fall through to a build comparison that was never
        // validated against a different OS's numbering scheme, no matter how high the build
        // number looks.
        var capabilities = new WindowsVisualCapabilities(
            new WindowsOsDescriptor(new Version(6, 3, 25000), WindowsProductType.Client));

        Assert.False(capabilities.SupportsDarkTitleBar);
        Assert.False(capabilities.SupportsRoundedCorners);
        Assert.False(capabilities.SupportsMica);
        Assert.False(capabilities.SupportsSystemBackdrop);
    }

    [Fact]
    public void Current_reflects_the_real_running_os_descriptor()
    {
        var expected = new WindowsVisualCapabilities(WindowsOsDescriptor.Current);

        Assert.Equal(expected.SupportsMica, WindowsVisualCapabilities.Current.SupportsMica);
        Assert.Equal(expected.SupportsSystemBackdrop, WindowsVisualCapabilities.Current.SupportsSystemBackdrop);
        Assert.Equal(expected.SupportsRoundedCorners, WindowsVisualCapabilities.Current.SupportsRoundedCorners);
        Assert.Equal(expected.SupportsDarkTitleBar, WindowsVisualCapabilities.Current.SupportsDarkTitleBar);
    }

    [Fact]
    public void Current_os_descriptor_detects_a_product_type_without_throwing()
    {
        // Smoke test for the RtlGetVersion P/Invoke itself, on whatever real OS runs this suite -
        // just confirms it resolves to one of the two defined enum values instead of throwing or
        // silently producing an invalid value.
        var productType = WindowsOsDescriptor.Current.ProductType;

        Assert.True(productType is WindowsProductType.Client or WindowsProductType.Server);
    }

    [Fact]
    public void MapProductType_reports_client_for_a_successful_workstation_result()
    {
        Assert.Equal(WindowsProductType.Client, WindowsOsDescriptor.MapProductType(rtlGetVersionResult: 0, wProductType: 1));
    }

    [Fact]
    public void MapProductType_reports_server_for_a_successful_non_workstation_result()
    {
        // 3 = VER_NT_SERVER; 2 (VER_NT_DOMAIN_CONTROLLER) is also Server-family and should map
        // the same way - only the workstation value (1) is ever Client.
        Assert.Equal(WindowsProductType.Server, WindowsOsDescriptor.MapProductType(rtlGetVersionResult: 0, wProductType: 3));
        Assert.Equal(WindowsProductType.Server, WindowsOsDescriptor.MapProductType(rtlGetVersionResult: 0, wProductType: 2));
    }

    [Fact]
    public void MapProductType_fails_closed_to_server_when_RtlGetVersion_itself_fails()
    {
        // This is the fail-closed path that matters most - the native call reporting failure
        // (a non-zero NTSTATUS) must never be read as Client, even if wProductType happens to
        // still contain 1 (e.g. an uninitialized/zeroed struct on some failure path) - the whole
        // point of failing closed is not trusting output that came with a failure code attached.
        Assert.Equal(WindowsProductType.Server, WindowsOsDescriptor.MapProductType(rtlGetVersionResult: -1, wProductType: 1));
    }
}
