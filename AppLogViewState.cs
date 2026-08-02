using System.Text;

namespace WindowsGSH;

public sealed class AppLogViewState
{
    public bool AutoScrollEnabled { get; private set; } = true;

    public int HiddenMessageCount { get; private set; }

    public void ToggleAutoScroll()
    {
        AutoScrollEnabled = !AutoScrollEnabled;
    }

    public void ClearView(int currentMessageCount)
    {
        HiddenMessageCount = Math.Max(0, currentMessageCount);
    }

    public string BuildVisibleText(
        IReadOnlyList<string> messages,
        string? source = null)
    {
        var start = Math.Min(HiddenMessageCount, messages.Count);
        var tag = string.IsNullOrWhiteSpace(source) ? null : $"[{source}]";
        var builder = new StringBuilder();
        for (var index = start; index < messages.Count; index++)
        {
            var message = messages[index];
            if (tag != null && !message.Contains(tag, StringComparison.Ordinal))
            {
                continue;
            }

            builder.AppendLine(message);
        }

        return builder.ToString();
    }
}
