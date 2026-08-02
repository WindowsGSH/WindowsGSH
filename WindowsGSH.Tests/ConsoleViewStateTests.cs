using WindowsGSH;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ConsoleViewStateTests
{
    // ── BuildVisibleText ─────────────────────────────────────────────────────

    [Fact]
    public void BuildVisibleText_returns_all_lines_when_no_filter_or_hidden_count()
    {
        var state = new ConsoleViewState();
        var lines = new[] { "line A", "line B", "line C" };

        var result = state.BuildVisibleText(lines);

        Assert.Contains("line A", result);
        Assert.Contains("line B", result);
        Assert.Contains("line C", result);
    }

    [Fact]
    public void BuildVisibleText_skips_lines_before_hidden_count()
    {
        var state = new ConsoleViewState();
        var lines = new[] { "old 1", "old 2", "new 3" };
        state.ClearView(2);

        var result = state.BuildVisibleText(lines);

        Assert.DoesNotContain("old 1", result);
        Assert.DoesNotContain("old 2", result);
        Assert.Contains("new 3", result);
    }

    [Fact]
    public void BuildVisibleText_hides_all_lines_when_hidden_count_equals_total()
    {
        var state = new ConsoleViewState();
        var lines = new[] { "a", "b", "c" };
        state.ClearView(3);

        var result = state.BuildVisibleText(lines);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildVisibleText_filters_by_search_text_case_insensitive()
    {
        var state = new ConsoleViewState();
        state.SetSearch("ERROR");
        var lines = new[] { "[Info] started", "[Error] crash", "[Warn] low mem" };

        var result = state.BuildVisibleText(lines);

        Assert.DoesNotContain("[Info]", result);
        Assert.Contains("[Error] crash", result);
        Assert.DoesNotContain("[Warn]", result);
    }

    [Fact]
    public void BuildVisibleText_applies_hidden_count_and_search_together()
    {
        var state = new ConsoleViewState();
        state.ClearView(1);
        state.SetSearch("warn");
        var lines = new[] { "[Warn] old", "[Info] new", "[Warn] new warn" };

        // Line 0 is hidden; lines 1 and 2 are visible but only line 2 matches search.
        var result = state.BuildVisibleText(lines);

        Assert.DoesNotContain("[Warn] old", result);
        Assert.DoesNotContain("[Info]", result);
        Assert.Contains("[Warn] new warn", result);
    }

    [Fact]
    public void BuildVisibleText_clears_search_when_SetSearch_called_with_null()
    {
        var state = new ConsoleViewState();
        state.SetSearch("error");
        state.SetSearch(null);
        var lines = new[] { "line 1", "line 2" };

        var result = state.BuildVisibleText(lines);

        Assert.Contains("line 1", result);
        Assert.Contains("line 2", result);
    }

    [Fact]
    public void BuildVisibleText_clears_search_when_SetSearch_called_with_whitespace()
    {
        var state = new ConsoleViewState();
        state.SetSearch("error");
        state.SetSearch("   ");
        var lines = new[] { "line 1" };

        var result = state.BuildVisibleText(lines);

        Assert.Contains("line 1", result);
    }

    [Fact]
    public void BuildVisibleText_handles_empty_list()
    {
        var state = new ConsoleViewState();

        var result = state.BuildVisibleText(Array.Empty<string>());

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildVisibleText_clamps_hidden_count_beyond_list_length()
    {
        var state = new ConsoleViewState();
        state.ClearView(100);
        var lines = new[] { "a", "b" };

        var result = state.BuildVisibleText(lines);

        Assert.Equal(string.Empty, result);
    }

    // ── Auto-scroll ──────────────────────────────────────────────────────────

    [Fact]
    public void AutoScrollEnabled_is_true_by_default()
    {
        var state = new ConsoleViewState();

        Assert.True(state.AutoScrollEnabled);
    }

    [Fact]
    public void ToggleAutoScroll_disables_when_enabled()
    {
        var state = new ConsoleViewState();

        state.ToggleAutoScroll();

        Assert.False(state.AutoScrollEnabled);
    }

    [Fact]
    public void ToggleAutoScroll_re_enables_after_second_call()
    {
        var state = new ConsoleViewState();
        state.ToggleAutoScroll();

        state.ToggleAutoScroll();

        Assert.True(state.AutoScrollEnabled);
    }

    // ── IsSearchActive ───────────────────────────────────────────────────────

    [Fact]
    public void IsSearchActive_is_false_by_default()
    {
        var state = new ConsoleViewState();

        Assert.False(state.IsSearchActive);
    }

    [Fact]
    public void IsSearchActive_is_true_after_SetSearch_with_text()
    {
        var state = new ConsoleViewState();

        state.SetSearch("error");

        Assert.True(state.IsSearchActive);
    }

    [Fact]
    public void IsSearchActive_is_false_after_SetSearch_cleared()
    {
        var state = new ConsoleViewState();
        state.SetSearch("error");

        state.SetSearch(null);

        Assert.False(state.IsSearchActive);
    }

    // ── ClearView ────────────────────────────────────────────────────────────

    [Fact]
    public void ClearView_hides_existing_lines_and_shows_new_ones()
    {
        var state = new ConsoleViewState();
        var lines = new List<string> { "old 1", "old 2" };
        state.ClearView(lines.Count);
        lines.Add("new 3");

        var result = state.BuildVisibleText(lines);

        Assert.DoesNotContain("old 1", result);
        Assert.DoesNotContain("old 2", result);
        Assert.Contains("new 3", result);
    }

    [Fact]
    public void ClearView_with_zero_does_nothing()
    {
        var state = new ConsoleViewState();
        state.ClearView(0);
        var lines = new[] { "a", "b" };

        var result = state.BuildVisibleText(lines);

        Assert.Contains("a", result);
        Assert.Contains("b", result);
    }

    // ── OnLinesRemoved (buffer-pruning adjustment) ───────────────────────────

    [Fact]
    public void OnLinesRemoved_decrements_hidden_count_by_removed_amount()
    {
        var state = new ConsoleViewState();
        state.ClearView(5);

        state.OnLinesRemoved(2);

        var lines = new[] { "a", "b", "c", "d", "e" };
        // hidden count was 5, decremented to 3 → shows lines at index 3 and 4
        var result = state.BuildVisibleText(lines);
        Assert.DoesNotContain("a", result);
        Assert.DoesNotContain("b", result);
        Assert.DoesNotContain("c", result);
        Assert.Contains("d", result);
        Assert.Contains("e", result);
    }

    [Fact]
    public void OnLinesRemoved_clamps_to_zero_when_removed_exceeds_hidden_count()
    {
        var state = new ConsoleViewState();
        state.ClearView(2);

        state.OnLinesRemoved(5);

        var lines = new[] { "a", "b", "c" };
        var result = state.BuildVisibleText(lines);
        Assert.Contains("a", result);
        Assert.Contains("b", result);
        Assert.Contains("c", result);
    }

    [Fact]
    public void OnLinesRemoved_does_nothing_when_hidden_count_is_already_zero()
    {
        var state = new ConsoleViewState();

        state.OnLinesRemoved(3);

        var lines = new[] { "x", "y" };
        var result = state.BuildVisibleText(lines);
        Assert.Contains("x", result);
        Assert.Contains("y", result);
    }

    [Fact]
    public void ClearView_then_buffer_prune_keeps_new_lines_visible()
    {
        // Simulates the full-buffer scenario: clear at 500, buffer prunes 1 entry,
        // then 1 new line arrives — the new line must remain visible.
        var state = new ConsoleViewState();

        // Simulate a full buffer of 5 lines, then clear.
        var lines = new List<string> { "old0", "old1", "old2", "old3", "old4" };
        state.ClearView(lines.Count); // hiddenLineCount = 5

        // Buffer prunes oldest entry.
        lines.RemoveAt(0); // lines = ["old1", "old2", "old3", "old4"]
        state.OnLinesRemoved(1); // hiddenLineCount = 4

        // New line arrives.
        lines.Add("new5"); // lines = ["old1", "old2", "old3", "old4", "new5"]

        var result = state.BuildVisibleText(lines);

        Assert.DoesNotContain("old1", result);
        Assert.DoesNotContain("old2", result);
        Assert.DoesNotContain("old3", result);
        Assert.DoesNotContain("old4", result);
        Assert.Contains("new5", result);
    }

    [Fact]
    public void ClearView_then_multiple_prunes_keeps_only_post_clear_lines_visible()
    {
        var state = new ConsoleViewState();
        var lines = new List<string> { "alpha", "beta", "gamma", "delta", "epsilon" };
        state.ClearView(lines.Count); // hiddenLineCount = 5

        // Two lines pruned, two new lines added.
        lines.RemoveAt(0);
        lines.RemoveAt(0);
        state.OnLinesRemoved(2); // hiddenLineCount = 3
        lines.Add("zeta");
        lines.Add("theta"); // lines = ["gamma","delta","epsilon","zeta","theta"]

        var result = state.BuildVisibleText(lines);

        Assert.DoesNotContain("gamma", result);
        Assert.DoesNotContain("delta", result);
        Assert.DoesNotContain("epsilon", result);
        Assert.Contains("zeta", result);
        Assert.Contains("theta", result);
    }

    // ── Sustained buffer-cycling stress test ─────────────────────────────────

    [Fact]
    public void ClearView_survives_sustained_high_volume_with_buffer_cycling()
    {
        const int bufferLimit = 500;
        const int totalLines = 5000;

        var state = new ConsoleViewState();

        // Fill buffer to capacity with "boot" lines, then clear.
        var lines = new List<string>(bufferLimit);
        for (var i = 0; i < bufferLimit; i++)
            lines.Add($"boot-{i}");
        state.ClearView(lines.Count);

        // Simulate sustained high-volume output: add 5000 more lines, cycling the buffer.
        for (var i = 0; i < totalLines; i++)
        {
            lines.Add($"live-{i}");
            if (lines.Count > bufferLimit)
            {
                lines.RemoveAt(0);
                state.OnLinesRemoved(1);
            }
        }

        var result = state.BuildVisibleText(lines);

        // No boot lines survive in the buffer after 5000 live lines — all cycled out.
        Assert.DoesNotContain("boot-", result);
        // The most recent lines must be visible.
        Assert.Contains($"live-{totalLines - 1}", result);
        Assert.Contains($"live-{totalLines - 2}", result);
    }
}
