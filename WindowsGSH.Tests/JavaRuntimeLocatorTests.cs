using WindowsGSH.Core.Java;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class JavaRuntimeLocatorTests
{
    [Fact]
    public void Locate_prefers_configured_path()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\Java\bin\java.exe",
            getEnvironmentVariable: _ => null,
            runVersionCommand: _ => "openjdk version \"21.0.5\"");

        var result = locator.Locate(@"C:\Java\bin\java.exe");

        Assert.True(result.Found);
        Assert.Equal(@"C:\Java\bin\java.exe", result.ExecutablePath);
        Assert.Equal(21, result.MajorVersion);
    }

    [Fact]
    public void Locate_uses_java_home_before_path()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\JavaHome\bin\java.exe" || path == @"C:\PathJava\java.exe",
            getEnvironmentVariable: name => name switch
            {
                "JAVA_HOME" => @"C:\JavaHome",
                "PATH" => @"C:\PathJava",
                _ => null
            },
            runVersionCommand: path => path == @"C:\JavaHome\bin\java.exe"
                ? "openjdk version \"25\""
                : "openjdk version \"21.0.5\"");

        var result = locator.Locate();

        Assert.True(result.Found);
        Assert.Equal(@"C:\JavaHome\bin\java.exe", result.ExecutablePath);
        Assert.Equal(25, result.MajorVersion);
    }

    [Fact]
    public void Locate_returns_missing_when_no_candidate_exists()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: _ => false,
            getEnvironmentVariable: _ => null,
            runVersionCommand: _ => throw new InvalidOperationException("should not run"));

        var result = locator.Locate();

        Assert.False(result.Found);
        Assert.Contains("java.exe was not found", result.ErrorMessage);
    }

    [Fact]
    public void Locate_accepts_configured_directory_path()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\Java\bin\java.exe",
            getEnvironmentVariable: _ => null,
            runVersionCommand: _ => "openjdk version \"26.0.1\"");

        var result = locator.Locate(@"C:\Java\bin");

        Assert.True(result.Found);
        Assert.Equal(@"C:\Java\bin\java.exe", result.ExecutablePath);
        Assert.Equal(26, result.MajorVersion);
    }

    [Fact]
    public void NormalizeExecutablePath_converts_directory_to_java_executable()
    {
        Assert.Equal(@"C:\Java\java.exe", JavaRuntimeLocator.NormalizeExecutablePath(@"C:\Java"));
        Assert.Equal(@"C:\Java\bin\java.exe", JavaRuntimeLocator.NormalizeExecutablePath(@"C:\Java\bin\java.exe"));
    }

    [Fact]
    public void Locate_reports_missing_configured_path_without_falling_back()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\PathJava\java.exe",
            getEnvironmentVariable: name => name == "PATH" ? @"C:\PathJava" : null,
            runVersionCommand: _ => "openjdk version \"21.0.5\"");

        var result = locator.Locate(@"C:\MissingJava\bin\java.exe");

        Assert.False(result.Found);
        Assert.Contains("Configured Java path was not found", result.ErrorMessage);
        Assert.Contains(@"C:\MissingJava\bin\java.exe", result.ErrorMessage);
    }

    [Fact]
    public void Locate_reports_configured_path_failure_without_falling_back()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\BrokenJava\bin\java.exe" || path == @"C:\PathJava\java.exe",
            getEnvironmentVariable: name => name == "PATH" ? @"C:\PathJava" : null,
            runVersionCommand: path => path == @"C:\BrokenJava\bin\java.exe"
                ? throw new InvalidOperationException("java -version failed")
                : "openjdk version \"21.0.5\"");

        var result = locator.Locate(@"C:\BrokenJava\bin\java.exe");

        Assert.False(result.Found);
        Assert.Equal(@"C:\BrokenJava\bin\java.exe", result.ExecutablePath);
        Assert.Contains("Configured Java path failed", result.ErrorMessage);
        Assert.Contains("java -version failed", result.ErrorMessage);
    }

    [Fact]
    public void Locate_falls_back_to_path_when_java_home_candidate_fails()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\BrokenJavaHome\bin\java.exe" || path == @"C:\PathJava\java.exe",
            getEnvironmentVariable: name => name switch
            {
                "JAVA_HOME" => @"C:\BrokenJavaHome",
                "PATH" => @"C:\PathJava",
                _ => null
            },
            runVersionCommand: path => path == @"C:\BrokenJavaHome\bin\java.exe"
                ? throw new InvalidOperationException("java -version timed out.")
                : "openjdk version \"25.0.1\"");

        var result = locator.Locate();

        Assert.True(result.Found);
        Assert.Equal(@"C:\PathJava\java.exe", result.ExecutablePath);
        Assert.Equal(25, result.MajorVersion);
    }

    [Fact]
    public void Locate_reports_all_failed_environment_candidates()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\BrokenJavaHome\bin\java.exe" || path == @"C:\BrokenPathJava\java.exe",
            getEnvironmentVariable: name => name switch
            {
                "JAVA_HOME" => @"C:\BrokenJavaHome",
                "PATH" => @"C:\BrokenPathJava",
                _ => null
            },
            runVersionCommand: path => path == @"C:\BrokenJavaHome\bin\java.exe"
                ? throw new InvalidOperationException("timeout")
                : throw new InvalidOperationException("empty output"));

        var result = locator.Locate();

        Assert.False(result.Found);
        Assert.Contains(@"C:\BrokenJavaHome\bin\java.exe: timeout", result.ErrorMessage);
        Assert.Contains(@"C:\BrokenPathJava\java.exe: empty output", result.ErrorMessage);
    }

    [Fact]
    public void Locate_returns_found_when_version_text_is_not_parseable()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path == @"C:\Java\bin\java.exe",
            getEnvironmentVariable: _ => null,
            runVersionCommand: _ => "unexpected java text");

        var result = locator.Locate(@"C:\Java\bin\java.exe");

        Assert.True(result.Found);
        Assert.Equal(@"C:\Java\bin\java.exe", result.ExecutablePath);
        Assert.Null(result.MajorVersion);
        Assert.Equal("unexpected java text", result.RawVersionText);
    }

    [Fact]
    public void Discover_returns_known_java_home_and_path_runtimes_once()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: path => path is @"C:\Known\java.exe" or @"C:\JavaHome\bin\java.exe" or @"C:\PathJava\java.exe",
            getEnvironmentVariable: name => name switch
            {
                "JAVA_HOME" => @"C:\JavaHome",
                "PATH" => @"C:\PathJava;C:\Known",
                _ => null
            },
            runVersionCommand: path => path.Contains("JavaHome", StringComparison.Ordinal)
                ? "openjdk version \"25.0.1\""
                : "openjdk version \"21.0.5\"");

        var runtimes = locator.Discover([@"C:\Known\java.exe", @"C:\Known"]);

        Assert.Equal(3, runtimes.Count);
        Assert.Contains(runtimes, runtime => runtime.ExecutablePath == @"C:\JavaHome\bin\java.exe" && runtime.MajorVersion == 25);
        Assert.Single(runtimes, runtime => runtime.ExecutablePath == @"C:\Known\java.exe");
    }
}
