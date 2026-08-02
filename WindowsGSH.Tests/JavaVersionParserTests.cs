using WindowsGSH.Core.Java;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class JavaVersionParserTests
{
    [Theory]
    [InlineData("java version \"1.8.0_421\"", 8)]
    [InlineData("openjdk version \"17.0.12\" 2024-07-16", 17)]
    [InlineData("openjdk version \"21.0.5\" 2024-10-15 LTS", 21)]
    [InlineData("openjdk version \"25\" 2025-09-16", 25)]
    [InlineData("openjdk version \"26.0.1\" 2026-10-20", 26)]
    [InlineData("openjdk version 27-ea", 27)]
    public void ParseMajorVersion_handles_legacy_current_and_future_versions(string versionText, int expected)
    {
        Assert.Equal(expected, JavaVersionParser.ParseMajorVersion(versionText));
    }

    [Theory]
    [InlineData("openjdk version \"21.0.5\" 2024-10-15 LTS", "OpenJDK")]
    [InlineData("java version \"1.8.0_421\"", "Java")]
    [InlineData("Temurin version \"21.0.5\"", "Temurin")]
    public void ParseVendor_returns_obvious_vendor_text(string versionText, string expected)
    {
        Assert.Equal(expected, JavaVersionParser.ParseVendor(versionText));
    }

    [Fact]
    public void ParseMajorVersion_returns_null_for_unrecognized_text()
    {
        Assert.Null(JavaVersionParser.ParseMajorVersion("not java"));
    }
}
