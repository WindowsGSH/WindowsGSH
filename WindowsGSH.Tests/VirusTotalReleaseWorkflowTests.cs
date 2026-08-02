using System.Text.RegularExpressions;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class VirusTotalReleaseWorkflowTests
{
    [Fact]
    public void Release_workflow_scans_before_creating_the_draft_release()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));

        var scanIndex = workflow.IndexOf("Scan release archive with VirusTotal", StringComparison.Ordinal);
        var notesIndex = workflow.IndexOf("Create release notes", StringComparison.Ordinal);
        var releaseIndex = workflow.IndexOf("Create GitHub Release", StringComparison.Ordinal);

        Assert.True(scanIndex >= 0);
        Assert.True(notesIndex > scanIndex);
        Assert.True(releaseIndex > notesIndex);
        Assert.Contains("VIRUSTOTAL_API_KEY: ${{ secrets.VIRUSTOTAL_API_KEY }}", workflow, StringComparison.Ordinal);
        Assert.Contains("gh @arguments", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("softprops/action-gh-release", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void VirusTotal_script_uses_large_file_upload_and_bounded_quota_safe_polling()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Submit-VirusTotalScan.ps1"));

        Assert.Contains("/api/v3/files/upload_url", script, StringComparison.Ordinal);
        Assert.Contains("/api/v3/analyses/$analysisId", script, StringComparison.Ordinal);
        Assert.Contains("[System.Diagnostics.Stopwatch]::StartNew()", script, StringComparison.Ordinal);
        Assert.Contains("[int]$PollIntervalSeconds = 30", script, StringComparison.Ordinal);
        Assert.Contains("[int]$TimeoutMinutes = 15", script, StringComparison.Ordinal);
        Assert.Contains("$statusCode -eq 429", script, StringComparison.Ordinal);
        Assert.Contains("[System.Net.Http.MultipartFormDataContent]::new()", script, StringComparison.Ordinal);
        Assert.Contains("[System.Net.Http.StreamContent]::new($fileStream)", script, StringComparison.Ordinal);
        Assert.Contains("[System.Net.Http.Headers.ContentDispositionHeaderValue]::new(\"form-data\")", script, StringComparison.Ordinal);
        Assert.Contains("$contentDisposition.Name = '\"file\"'", script, StringComparison.Ordinal);
        Assert.Contains("$multipart.Add($fileContent)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$multipart.Add($fileContent, \"file\", $file.Name)", script, StringComparison.Ordinal);
        Assert.Contains("filename*", script, StringComparison.Ordinal);
        Assert.Contains("application/octet-stream", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Form @{ file = $file }", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"Write-(Host|Output).*ApiKey", RegexOptions.IgnoreCase), script);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "WindowsGSH.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
