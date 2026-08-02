using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleRepositorySearchServiceTests
{
    [Theory]
    [InlineData("WindowsGSH.TF2", "tf2", true)]
    [InlineData("windowsgsh.tf2", "TF2", true)]
    [InlineData("WindowsGSH.Team-Fortress-2", "team fortress", true)]
    [InlineData("owner/WindowsGSH.TF2", "tf2", true)]
    [InlineData("WindowsGSM.TF2", "tf2", false)]
    [InlineData("MyWindowsGSH.TF2", "tf2", false)]
    [InlineData("WindowsGSH", "tf2", false)]
    public void Name_match_requires_windows_gsh_prefix(string value, string searchTerm, bool expected)
    {
        Assert.Equal(expected, ModuleRepositorySearchService.IsWindowsGshModuleNameMatch(value, searchTerm));
    }

    [Fact]
    public void Parse_results_filters_to_windows_gsh_repositories()
    {
        const string json = """
        {
          "items": [
            {
              "name": "WindowsGSH.TF2",
              "full_name": "WindowsGSH/WindowsGSH.TF2",
              "owner": { "login": "WindowsGSH", "avatar_url": "https://avatars.example/1.png" },
              "description": "Team Fortress 2",
              "html_url": "https://github.example/WindowsGSH/WindowsGSH.TF2",
              "language": "C#",
              "license": { "spdx_id": "MIT" },
              "default_branch": "main",
              "zipball_url": "https://github.example/zip",
              "stargazers_count": 12,
              "forks_count": 3,
              "open_issues_count": 1,
              "updated_at": "2026-06-17T12:00:00Z"
            },
            {
              "name": "WindowsGSM.TF2",
              "full_name": "someone/WindowsGSM.TF2"
            },
            {
              "name": "TF2",
              "full_name": "someone/TF2"
            }
          ]
        }
        """;

        var results = ModuleRepositorySearchService.ParseGitHubSearchResults(json, "tf2");

        var result = Assert.Single(results);
        Assert.Equal("WindowsGSH.TF2", result.Name);
        Assert.Equal("WindowsGSH/WindowsGSH.TF2", result.FullName);
        Assert.Equal("https://avatars.example/1.png", result.OwnerAvatarUrl);
        Assert.Contains("License MIT", result.DisplayMetadata);
        Assert.Contains("Open issues 1", result.DisplayMetadata);
        Assert.Equal(12, result.Stars);
    }
}
