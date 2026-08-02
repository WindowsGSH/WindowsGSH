using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleImportPathPlannerTests
{
    [Fact]
    public void GetModulePath_accepts_safe_module_ids()
    {
        var path = ModuleImportPathPlanner.GetModulePath(@"C:\WindowsGSH\modules\installed", "tf2.server_1");

        Assert.Equal(@"C:\WindowsGSH\modules\installed\tf2.server_1", path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".tf2")]
    [InlineData("-tf2")]
    [InlineData(@"..\tf2")]
    [InlineData(@"tf2\evil")]
    [InlineData("tf2/evil")]
    public void GetModulePath_rejects_unsafe_module_ids(string moduleId)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModuleImportPathPlanner.GetModulePath(@"C:\WindowsGSH\modules\installed", moduleId));
    }

    [Fact]
    public void GetAvailableModulePath_adds_numeric_suffix_for_existing_directories()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\WindowsGSH\modules\installed\tf2",
            @"C:\WindowsGSH\modules\installed\tf2-2"
        };

        var path = ModuleImportPathPlanner.GetAvailableModulePath(
            @"C:\WindowsGSH\modules\installed",
            "tf2",
            existing.Contains);

        Assert.Equal(@"C:\WindowsGSH\modules\installed\tf2-3", path);
    }

    [Fact]
    public void GetAvailableFilePath_adds_numeric_suffix_for_existing_files()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\WindowsGSH\module-imports\archive\icarus.zip",
            @"C:\WindowsGSH\module-imports\archive\icarus-2.zip"
        };

        var path = ModuleImportPathPlanner.GetAvailableFilePath(
            @"C:\WindowsGSH\module-imports\archive",
            "icarus.zip",
            existing.Contains);

        Assert.Equal(@"C:\WindowsGSH\module-imports\archive\icarus-3.zip", path);
    }

    [Theory]
    [InlineData(@"..\icarus.zip")]
    [InlineData(@"nested\icarus.zip")]
    [InlineData("nested/icarus.zip")]
    public void GetAvailableFilePath_rejects_file_names_with_paths(string fileName)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModuleImportPathPlanner.GetAvailableFilePath(@"C:\WindowsGSH\module-imports\archive", fileName, _ => false));
    }

    [Fact]
    public void IsPathInsideDirectory_rejects_sibling_prefix_paths()
    {
        Assert.False(ModuleImportPathPlanner.IsPathInsideDirectory(
            @"C:\WindowsGSH\modules\disabled",
            @"C:\WindowsGSH\modules\disabled-copy\tf2"));
    }

    [Fact]
    public void IsPathInsideDirectory_accepts_child_paths()
    {
        Assert.True(ModuleImportPathPlanner.IsPathInsideDirectory(
            @"C:\WindowsGSH\modules\disabled",
            @"C:\WindowsGSH\modules\disabled\tf2"));
    }
}
