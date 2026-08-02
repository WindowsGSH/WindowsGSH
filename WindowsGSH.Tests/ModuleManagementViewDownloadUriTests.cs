using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleManagementViewDownloadUriTests
{
    [Theory]
    [InlineData(
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded",
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded/archive/HEAD.zip")]
    [InlineData(
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded.git",
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded/archive/HEAD.zip")]
    [InlineData(
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded/tree/release",
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded/archive/refs/heads/release.zip")]
    [InlineData(
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded/tree/feature/module-update",
        "https://github.com/WindowsGSH/WindowsGSH.Enshrouded/archive/refs/heads/feature/module-update.zip")]
    public void ResolveModuleDownloadUri_converts_github_repo_urls_to_archive_zips(
        string sourceUrl,
        string expectedUrl)
    {
        var resolved = ModuleManagementView.ResolveModuleDownloadUri(new Uri(sourceUrl));

        Assert.Equal(expectedUrl, resolved.ToString());
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("release/current")]
    public void ResolveModuleDownloadUri_uses_supplied_default_branch_for_plain_github_repo_urls(
        string defaultBranch)
    {
        var resolved = ModuleManagementView.ResolveModuleDownloadUri(
            new Uri("https://github.com/WindowsGSH/WindowsGSH.Enshrouded"),
            defaultBranch);

        Assert.Equal(
            $"https://github.com/WindowsGSH/WindowsGSH.Enshrouded/archive/refs/heads/{defaultBranch}.zip",
            resolved.ToString());
    }

    [Fact]
    public void ResolveModuleDownloadUri_leaves_direct_zip_urls_unchanged()
    {
        var sourceUrl = new Uri("https://example.test/modules/module.zip");

        var resolved = ModuleManagementView.ResolveModuleDownloadUri(sourceUrl);

        Assert.Equal(sourceUrl, resolved);
    }
}
