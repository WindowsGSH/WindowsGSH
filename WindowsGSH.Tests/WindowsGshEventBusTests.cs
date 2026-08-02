using WindowsGSH.Core.Events;
using WindowsGSH.Core.Operations;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class WindowsGshEventBusTests
{
    [Fact]
    public void Publish_DeliversEventsInSubscriptionOrder()
    {
        var bus = new WindowsGshEventBus();
        var received = new List<string>();
        bus.Subscribe<TestEvent>(message => received.Add("first:" + message.Value));
        bus.Subscribe<TestEvent>(message => received.Add("second:" + message.Value));

        bus.Publish(new TestEvent("hello"));

        Assert.Equal(["first:hello", "second:hello"], received);
    }

    [Fact]
    public void Dispose_UnsubscribesHandler()
    {
        var bus = new WindowsGshEventBus();
        var received = 0;
        var subscription = bus.Subscribe<TestEvent>(_ => received++);

        subscription.Dispose();
        bus.Publish(new TestEvent("ignored"));

        Assert.Equal(0, received);
    }

    [Fact]
    public void Publish_IsolatesSubscriberExceptions()
    {
        var bus = new WindowsGshEventBus();
        var received = false;
        bus.Subscribe<TestEvent>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<TestEvent>(_ => received = true);

        bus.Publish(new TestEvent("hello"));

        Assert.True(received);
    }

    [Fact]
    public void LogBridge_ForwardsServerLogEvents()
    {
        var bus = new WindowsGshEventBus();
        var messages = new List<(string Message, string? Source)>();
        using var bridge = WindowsGshEventLogBridge.Connect(bus, (message, source) => messages.Add((message, source)));

        bus.Publish(new ServerLogEvent(
            DateTimeOffset.UtcNow,
            "server1",
            "module",
            WindowsGshEventSeverity.Warning,
            "test",
            "Something happened",
            "details"));

        var message = Assert.Single(messages);
        Assert.Equal("Something happened: details", message.Message);
        Assert.Equal("server1", message.Source);
    }

    [Fact]
    public void LogThrottle_SuppressesDuplicateMessagesWithinInterval()
    {
        var now = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var current = now;
        var throttle = new WindowsGshEventLogThrottle(TimeSpan.FromMinutes(5), () => current);

        Assert.True(throttle.ShouldPublish("query:server1", "offline"));
        Assert.False(throttle.ShouldPublish("query:server1", "offline"));
        Assert.True(throttle.ShouldPublish("query:server1", "different"));
        current = now.AddMinutes(6);
        Assert.True(throttle.ShouldPublish("query:server1", "different"));
    }

    [Fact]
    public void OperationManager_PublishesOperationChangedEvents()
    {
        var bus = new WindowsGshEventBus();
        var events = new List<ServerOperationChangedEvent>();
        bus.Subscribe<ServerOperationChangedEvent>(events.Add);
        var manager = new ServerOperationManager(_ => { }, bus);

        Assert.True(manager.TryBeginServerOperation("server1", "Server", ServerOperationKind.Start, out var scope, out _));
        scope!.Dispose();

        Assert.Equal(2, events.Count);
        Assert.Equal(ServerOperationStatus.Running, events[0].Status);
        Assert.Equal(ServerOperationStatus.Completed, events[1].Status);
        Assert.Equal("server1", events[1].ServerId);
    }

    private sealed record TestEvent(string Value);
}
