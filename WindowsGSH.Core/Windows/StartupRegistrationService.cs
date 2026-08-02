using Microsoft.Win32;

namespace WindowsGSH.Core.Windows;

public interface ICurrentUserStartupStore
{
    string? GetValue(string name);

    void SetValue(string name, string command);

    void DeleteValue(string name);
}

public sealed class RegistryCurrentUserStartupStore : ICurrentUserStartupStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current-user startup registry key.");
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public sealed class StartupRegistrationService(
    ICurrentUserStartupStore? store = null,
    string valueName = "WindowsGSH")
{
    private readonly ICurrentUserStartupStore _store = store ?? new RegistryCurrentUserStartupStore();

    public StartupRegistrationResult Reconcile(bool enabled, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Contains('"'))
        {
            throw new ArgumentException("The WindowsGSH executable path contains an unsupported quote character.", nameof(executablePath));
        }

        if (!enabled)
        {
            _store.DeleteValue(valueName);
            return new StartupRegistrationResult(false, null, Changed: true);
        }

        var command = BuildCommand(fullPath);
        var current = _store.GetValue(valueName);
        if (string.Equals(current, command, StringComparison.Ordinal))
        {
            return new StartupRegistrationResult(true, command, Changed: false);
        }

        _store.SetValue(valueName, command);
        return new StartupRegistrationResult(true, command, Changed: true);
    }

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{Path.GetFullPath(executablePath)}\"";
    }
}

public sealed record StartupRegistrationResult(bool IsEnabled, string? Command, bool Changed);
