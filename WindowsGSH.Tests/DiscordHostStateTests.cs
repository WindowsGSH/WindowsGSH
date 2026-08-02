using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordHostStateTests
{
    [Fact]
    public void Tracker_records_safe_state_transitions_and_identity()
    {
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var tracker = new DiscordHostStateTracker(() => now);
        var observed = new List<DiscordHostStateSnapshot>();
        tracker.Changed += (_, snapshot) => observed.Add(snapshot);

        tracker.Transition(DiscordHostState.Connecting);
        tracker.Transition(DiscordHostState.Connected);
        tracker.Transition(DiscordHostState.Ready, "WindowsGSH Bot", 123456789);

        Assert.Equal(
            [DiscordHostState.Connecting, DiscordHostState.Connected, DiscordHostState.Ready],
            observed.Select(item => item.State));
        Assert.Equal("WindowsGSH Bot", tracker.Current.BotUsername);
        Assert.Equal((ulong)123456789, tracker.Current.ApplicationId);
        Assert.Equal(now, tracker.Current.ChangedAt);
    }

    [Fact]
    public void Tracker_redacts_secret_material_from_failure()
    {
        var tracker = new DiscordHostStateTracker();

        tracker.Transition(
            DiscordHostState.Failed,
            failure: "token=very-secret-value https://discord.com/api/webhooks/123/private");

        Assert.DoesNotContain("very-secret-value", tracker.Current.LastFailure);
        Assert.DoesNotContain("/123/private", tracker.Current.LastFailure);
        Assert.Contains("[REDACTED]", tracker.Current.LastFailure);
    }

    [Fact]
    public void Tracker_can_recover_from_failure_to_ready()
    {
        var tracker = new DiscordHostStateTracker();
        tracker.Transition(DiscordHostState.Failed, failure: "gateway failed");

        tracker.Transition(DiscordHostState.Connecting);
        tracker.Transition(DiscordHostState.Ready, "Recovered Bot", 456);

        Assert.Equal(DiscordHostState.Ready, tracker.Current.State);
        Assert.Null(tracker.Current.LastFailure);
        Assert.Equal("Recovered Bot", tracker.Current.BotUsername);
    }

    [Fact]
    public void Invite_link_requires_identity_and_minimum_permissions()
    {
        Assert.Null(DiscordInviteLink.Create(null));

        var invite = DiscordInviteLink.Create(123);

        Assert.Equal(
            $"https://discord.com/oauth2/authorize?client_id=123&permissions={DiscordInviteLink.MinimumPermissions}&scope=bot",
            invite);
        Assert.Equal((ulong)84_992, DiscordInviteLink.MinimumPermissions);
    }

    [Fact]
    public async Task Lifecycle_gate_queues_waiting_lifecycle_work()
    {
        var gate = new DiscordLifecycleGate();
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        var waitingStart = gate.EnterAsync();
        var waitingStop = gate.EnterAsync();
        Assert.False(waitingStart.IsCompleted);
        Assert.False(waitingStop.IsCompleted);

        gate.Exit();
        await waitingStart;
        Assert.False(waitingStop.IsCompleted);
        gate.Exit();
        await waitingStop;
        gate.Exit();
    }
}
