using WindowsGSH.Core.Java;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class JavaRuntimeManagerTests
{
    [Fact]
    public void Validate_rejects_runtime_below_module_minimum()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: _ => true,
            getEnvironmentVariable: _ => null,
            runVersionCommand: _ => "openjdk version \"17.0.12\"");
        var manager = new JavaRuntimeManager(locator);

        var result = manager.Validate(@"C:\Java\bin\java.exe", minimumMajor: 21);

        Assert.False(result.IsValid);
        Assert.Contains("Java 21+", result.Message);
    }

    [Fact]
    public void BuildJvmCommandLine_uses_typed_memory_and_preserves_raw_optional_arguments()
    {
        var settings = ServerJavaSettings.Default with
        {
            InitialMemoryMb = 2048,
            MaximumMemoryMb = 8192,
            AdditionalJvmArguments = "-XX:+UseG1GC"
        };

        var arguments = JavaRuntimeManager.BuildJvmCommandLine(settings);

        Assert.Equal("-Xms2048M -Xmx8192M -XX:+UseG1GC", arguments);
    }

    [Fact]
    public void Validate_rejects_found_runtime_with_unparseable_version()
    {
        var locator = new JavaRuntimeLocator(
            fileExists: _ => true,
            getEnvironmentVariable: _ => null,
            runVersionCommand: _ => "unexpected java output");
        var manager = new JavaRuntimeManager(locator);

        var result = manager.Validate(@"C:\Java\java.exe", minimumMajor: 21);

        Assert.False(result.IsValid);
        Assert.Contains("could not be parsed", result.Message);
    }

    [Fact]
    public void ValidateMemory_rejects_maximum_below_initial()
    {
        var settings = ServerJavaSettings.Default with
        {
            InitialMemoryMb = 4096,
            MaximumMemoryMb = 2048
        };

        var exception = Assert.Throws<InvalidOperationException>(() => JavaRuntimeManager.ValidateMemory(settings));

        Assert.Contains("greater than or equal", exception.Message);
    }
}
