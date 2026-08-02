using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleDevelopmentDiagnosticTests
{
    [Fact]
    public void Loaded_diagnostic_uses_loaded_prefix()
    {
        var diagnostic = ModuleDevelopmentDiagnostic.Info(
            "module.loaded",
            @"C:\modules\test",
            "Test loaded.");

        Assert.StartsWith("Loaded:", diagnostic.DisplayText);
    }

    [Fact]
    public void Warning_and_error_diagnostics_keep_their_severity_prefix()
    {
        var warning = new ModuleDevelopmentDiagnostic(
            ModuleValidationSeverity.Warning,
            "module.warning",
            "module.json",
            "Review this.");
        var error = ModuleDevelopmentDiagnostic.Error(
            "module.error",
            "module.json",
            "Failed.");

        Assert.StartsWith("Warning:", warning.DisplayText);
        Assert.StartsWith("Error:", error.DisplayText);
    }
}
