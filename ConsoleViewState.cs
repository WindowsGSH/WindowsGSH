using System.Text;

namespace WindowsGSH;

public sealed class ConsoleViewState
{
    public bool AutoScrollEnabled { get; private set; } = true;

    private int _hiddenLineCount;
    private string? _searchText;

    public bool IsSearchActive => _searchText != null;

    public void ToggleAutoScroll() => AutoScrollEnabled = !AutoScrollEnabled;

    public void ClearView(int currentLineCount) =>
        _hiddenLineCount = Math.Max(0, currentLineCount);

    // Called when the bounded buffer removes old entries from the front.
    // Keeps _hiddenLineCount pointing at the same logical position in the log.
    public void OnLinesRemoved(int count) =>
        _hiddenLineCount = Math.Max(0, _hiddenLineCount - count);

    public void SetSearch(string? text) =>
        _searchText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // Rebuilds the full visible text from the raw formatted-string log,
    // applying both the hidden-line cutoff and the active search filter.
    public string BuildVisibleText(IReadOnlyList<string> formattedLines)
    {
        var start = Math.Min(_hiddenLineCount, formattedLines.Count);
        var search = _searchText;
        var builder = new StringBuilder();
        for (var i = start; i < formattedLines.Count; i++)
        {
            var line = formattedLines[i];
            if (search != null && !line.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }
}
