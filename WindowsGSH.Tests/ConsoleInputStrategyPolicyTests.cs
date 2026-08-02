using System.Diagnostics;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ConsoleInputStrategyPolicyTests
{
    [Fact]
    public void Legacy_embedded_console_maps_to_redirected()
    {
        var runtime = new ModuleRuntimeDefinition("server.exe", ["server"], AllowsEmbeddedConsole: true);

        Assert.Equal(ConsoleInputStrategy.Redirected, runtime.EffectiveConsoleStrategy);
        Assert.True(ConsoleInputStrategyPolicy.UsesRedirectedStreams(runtime));
    }

    [Theory]
    [InlineData(ConsoleInputStrategy.WindowMessage)]
    [InlineData(ConsoleInputStrategy.RconPreferred)]
    [InlineData(ConsoleInputStrategy.LogTailOnly)]
    [InlineData(ConsoleInputStrategy.None)]
    public void Non_redirected_strategies_do_not_redirect_process_streams(ConsoleInputStrategy strategy)
    {
        var runtime = new ModuleRuntimeDefinition(
            "server.exe",
            ["server"],
            AllowsEmbeddedConsole: true,
            ConsoleStrategy: strategy);

        Assert.False(ConsoleInputStrategyPolicy.UsesRedirectedStreams(runtime));
    }

    [Fact]
    public void Rcon_preferred_disables_console_command_input()
    {
        var module = new StrategyModule(ConsoleInputStrategy.RconPreferred, supportsConsoleCommands: true);

        Assert.True(ConsoleInputStrategyPolicy.PrefersRcon(module));
        Assert.False(ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(module));
        Assert.Contains("RCON", ConsoleInputStrategyPolicy.GetCommandUnavailableMessage(module));
    }

    [Fact]
    public void Window_message_requires_module_command_capability()
    {
        Assert.False(ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(
            new StrategyModule(ConsoleInputStrategy.WindowMessage, supportsConsoleCommands: true)));
        Assert.True(ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(
            new WindowMessageModule()));
    }

    private class StrategyModule(ConsoleInputStrategy strategy, bool supportsConsoleCommands) : TestGameServerModule
    {
        public override ModuleCapabilities Capabilities =>
            base.Capabilities with { SupportsConsoleCommands = supportsConsoleCommands };

        public override ModuleRuntimeDefinition Runtime =>
            new("server.exe", ["server"], ConsoleStrategy: strategy);
    }

    private sealed class WindowMessageModule : StrategyModule, IModuleConsoleCommandCapability
    {
        public WindowMessageModule()
            : base(ConsoleInputStrategy.WindowMessage, supportsConsoleCommands: true)
        {
        }

        public Task<string> ExecuteConsoleCommandAsync(
            ServerInstance instance,
            string command,
            CancellationToken cancellationToken) => Task.FromResult("sent");
    }

    private abstract class TestGameServerModule : IGameServerModule
    {
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public virtual ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public virtual ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }
}
