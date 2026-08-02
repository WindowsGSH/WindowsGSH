using WindowsGSH.Core.Windows;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public void Reconcile_creates_updates_and_removes_current_user_registration()
    {
        var store = new FakeStartupStore();
        var service = new StartupRegistrationService(store);
        var firstPath = Path.Combine(Path.GetTempPath(), "Windows GSH", "WindowsGSH.exe");
        var movedPath = Path.Combine(Path.GetTempPath(), "Moved Windows GSH", "WindowsGSH.exe");

        var created = service.Reconcile(enabled: true, firstPath);
        var updated = service.Reconcile(enabled: true, movedPath);
        var removed = service.Reconcile(enabled: false, movedPath);

        Assert.Equal($"\"{Path.GetFullPath(firstPath)}\"", created.Command);
        Assert.Equal($"\"{Path.GetFullPath(movedPath)}\"", updated.Command);
        Assert.Null(removed.Command);
        Assert.Null(store.Value);
        Assert.Equal(2, store.SetCount);
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public void Reconcile_does_not_rewrite_matching_registration()
    {
        var store = new FakeStartupStore();
        var path = Path.Combine(Path.GetTempPath(), "WindowsGSH.exe");
        store.Value = StartupRegistrationService.BuildCommand(path);
        var service = new StartupRegistrationService(store);

        var result = service.Reconcile(enabled: true, path);

        Assert.False(result.Changed);
        Assert.Equal(0, store.SetCount);
    }

    private sealed class FakeStartupStore : ICurrentUserStartupStore
    {
        public string? Value { get; set; }
        public int SetCount { get; private set; }
        public int DeleteCount { get; private set; }

        public string? GetValue(string name) => Value;

        public void SetValue(string name, string command)
        {
            Value = command;
            SetCount++;
        }

        public void DeleteValue(string name)
        {
            Value = null;
            DeleteCount++;
        }
    }
}
