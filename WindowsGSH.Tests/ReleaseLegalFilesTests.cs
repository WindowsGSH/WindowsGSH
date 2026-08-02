using System.Xml.Linq;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ReleaseLegalFilesTests
{
    private static readonly string[] RequiredLegalFiles =
    [
        "LICENSE.md",
        "NOTICE.md",
        "TRADEMARKS.md",
        "SECURITY.md",
        "THIRD_PARTY_NOTICES.md"
    ];

    [Fact]
    public void App_project_publishes_required_legal_files_as_separate_files()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(repositoryRoot, "WindowsGSH.csproj"));
        var noneItems = project.Descendants("None").ToDictionary(
            element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in RequiredLegalFiles)
        {
            Assert.True(File.Exists(Path.Combine(repositoryRoot, fileName)), $"Required legal file is missing: {fileName}");
            Assert.True(noneItems.TryGetValue(fileName, out var item), $"{fileName} is not declared for publication.");
            Assert.Equal("PreserveNewest", (string?)item!.Attribute("CopyToPublishDirectory"));
            Assert.Equal("true", (string?)item.Attribute("ExcludeFromSingleFile"));
        }
    }

    [Fact]
    public void Release_script_archives_the_publish_directory()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Publish-Release.ps1"));

        Assert.Contains("Compress-Archive -Path (Join-Path $publishPath \"*\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Third_party_notice_lists_every_direct_production_package()
    {
        var repositoryRoot = FindRepositoryRoot();
        var notice = File.ReadAllText(Path.Combine(repositoryRoot, "THIRD_PARTY_NOTICES.md"));
        var productionProjects = new[]
        {
            "WindowsGSH.csproj",
            Path.Combine("WindowsGSH.Core", "WindowsGSH.Core.csproj"),
            Path.Combine("WindowsGSH.Data", "WindowsGSH.Data.csproj"),
            Path.Combine("WindowsGSH.Discord", "WindowsGSH.Discord.csproj")
        };

        foreach (var projectPath in productionProjects)
        {
            var project = XDocument.Load(Path.Combine(repositoryRoot, projectPath));
            foreach (var package in project.Descendants("PackageReference"))
            {
                var id = (string?)package.Attribute("Include");
                var version = (string?)package.Attribute("Version");
                Assert.False(string.IsNullOrWhiteSpace(id));
                Assert.False(string.IsNullOrWhiteSpace(version));
                Assert.Contains($"`{id}`", notice, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(version!, notice, StringComparison.Ordinal);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WindowsGSH.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the WindowsGSH repository root.");
    }
}
