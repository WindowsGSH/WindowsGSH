using WindowsGSH;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleFieldFormServiceTests
{
    [Fact]
    public void GetVisibilityControllerKeysReturnsOnlyReferencedFields()
    {
        var fields = new[]
        {
            new ConfigFieldDefinition("server.map", "Map", ConfigFieldType.Select),
            new ConfigFieldDefinition("server.needPassword", "Require Password", ConfigFieldType.Boolean),
            new ConfigFieldDefinition(
                "server.password",
                "Password",
                ConfigFieldType.Password,
                VisibleWhen: new FieldVisibilityCondition("server.needPassword", IsTruthy: true)),
            new ConfigFieldDefinition(
                "server.mode",
                "Mode",
                ConfigFieldType.Select,
                VisibleWhen: new FieldVisibilityCondition(" server.visibility ", EqualsValue: "private"))
        };

        var controllerKeys = ModuleFieldFormService.GetVisibilityControllerKeys(fields);

        Assert.Equal(2, controllerKeys.Count);
        Assert.Contains("server.needPassword", controllerKeys);
        Assert.Contains("server.visibility", controllerKeys);
        Assert.DoesNotContain("server.map", controllerKeys);
    }

    [Fact]
    public void GetComboBoxValuePrefersNewSelectionOverStaleEditableText()
    {
        var value = ModuleFieldFormService.GetComboBoxValue("EU", "All");

        Assert.Equal("EU", value);
    }

    [Fact]
    public void GetComboBoxValueUsesEditableTextWhenNoItemIsSelected()
    {
        var value = ModuleFieldFormService.GetComboBoxValue(null, "Custom");

        Assert.Equal("Custom", value);
    }

    [Fact]
    public void ValidateSettingsRejectsMissingRequiredValue()
    {
        var field = new ConfigFieldDefinition("server.name", "Server Name", ConfigFieldType.Text, Required: true);
        var settings = new Dictionary<string, object?> { ["server.name"] = "" };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleFieldFormService.ValidateSettings([field], settings));

        Assert.Equal("Server Name is required.", exception.Message);
    }

    [Fact]
    public void ValidateSettingsRejectsPortOutsideAllowedRange()
    {
        var field = new ConfigFieldDefinition("network.port", "Port", ConfigFieldType.Port);
        var settings = new Dictionary<string, object?> { ["network.port"] = 70000 };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleFieldFormService.ValidateSettings([field], settings));

        Assert.Equal("Port must be between 1 and 65535.", exception.Message);
    }

    [Fact]
    public void ValidateSettingsSkipsRegexForBlankOptionalValues()
    {
        var field = new ConfigFieldDefinition(
            "server.password",
            "Server Password",
            ConfigFieldType.Password,
            Required: false,
            ValidationPattern: "^.{6,}$",
            ValidationMessage: "Password must be at least six characters.");
        var settings = new Dictionary<string, object?> { ["server.password"] = "" };

        ModuleFieldFormService.ValidateSettings([field], settings);
    }

    [Fact]
    public void ValidateSettingsStillRejectsNonBlankRegexMismatch()
    {
        var field = new ConfigFieldDefinition(
            "server.password",
            "Server Password",
            ConfigFieldType.Password,
            Required: false,
            ValidationPattern: "^.{6,}$",
            ValidationMessage: "Password must be at least six characters.");
        var settings = new Dictionary<string, object?> { ["server.password"] = "short" };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleFieldFormService.ValidateSettings([field], settings));

        Assert.Equal("Password must be at least six characters.", exception.Message);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("", false)]
    public void IsFieldVisibleSupportsTruthyConditions(object? value, bool expected)
    {
        var field = new ConfigFieldDefinition(
            "advanced.value",
            "Advanced",
            ConfigFieldType.Text,
            VisibleWhen: new FieldVisibilityCondition("advanced.enabled", IsTruthy: true));

        Assert.Equal(expected, ModuleFieldFormService.IsFieldVisible(field, key => value));
    }

    [Fact]
    public void IsFieldVisibleSupportsEqualsConditions()
    {
        var field = new ConfigFieldDefinition(
            "server.password",
            "Password",
            ConfigFieldType.Password,
            VisibleWhen: new FieldVisibilityCondition("server.visibility", EqualsValue: "Private"));

        Assert.True(ModuleFieldFormService.IsFieldVisible(field, key => "private"));
        Assert.False(ModuleFieldFormService.IsFieldVisible(field, key => "public"));
    }

    [Fact]
    public void ToJsonValuePreservesBooleanAndIntegerValues()
    {
        var booleanField = new ConfigFieldDefinition("server.public", "Public", ConfigFieldType.Boolean);
        var portField = new ConfigFieldDefinition("network.port", "Port", ConfigFieldType.Port);

        Assert.Equal("true", ModuleFieldFormService.ToJsonValue(booleanField, true)?.ToJsonString());
        Assert.Equal("27015", ModuleFieldFormService.ToJsonValue(portField, "27015")?.ToJsonString());
    }
}
