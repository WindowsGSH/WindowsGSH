using Xunit;

namespace WindowsGSH.Tests;

public sealed class AppLogViewStateTests
{
    [Fact]
    public void BuildVisibleText_preserves_complete_messages_and_filtering()
    {
        var state = new AppLogViewState();
        var messages = new[]
        {
            "[10:00:00] general message",
            "[10:00:01] [server-1] first line\nstack trace line",
            "[10:00:02] [server-2] other server"
        };

        var text = state.BuildVisibleText(messages, "server-1");

        Assert.Contains("first line", text);
        Assert.Contains("stack trace line", text);
        Assert.DoesNotContain("general message", text);
        Assert.DoesNotContain("other server", text);
    }

    [Fact]
    public void ClearView_hides_existing_messages_without_mutating_source()
    {
        var state = new AppLogViewState();
        var messages = new List<string> { "old one", "old two" };

        state.ClearView(messages.Count);
        Assert.Equal(string.Empty, state.BuildVisibleText(messages));
        Assert.Equal(2, messages.Count);

        messages.Add("new line");
        Assert.Contains("new line", state.BuildVisibleText(messages));
        Assert.DoesNotContain("old one", state.BuildVisibleText(messages));
    }

    [Fact]
    public void AutoScroll_toggle_is_independent_of_visible_log_content()
    {
        var state = new AppLogViewState();
        var messages = new[] { "line" };

        state.ToggleAutoScroll();

        Assert.False(state.AutoScrollEnabled);
        Assert.Contains("line", state.BuildVisibleText(messages));

        state.ToggleAutoScroll();
        Assert.True(state.AutoScrollEnabled);
    }
}
