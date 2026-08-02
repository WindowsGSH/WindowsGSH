using System.IO;
using System.Windows;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;

namespace WindowsGSH;

public partial class BackupPreviewWindow : Wpf.Ui.Controls.FluentWindow
{
    public BackupPreviewWindow(BackupPreview preview)
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        SubtitleTextBlock.Text = preview.FileName;
        FileCountTextBlock.Text = preview.FileCount.ToString();
        FileTargetCountTextBlock.Text = preview.FileTargets.Count.ToString();
        FolderCountTextBlock.Text = preview.FolderTargets.Count.ToString();
        SizeTextBlock.Text = FormatBytes(preview.SizeBytes);
        ScopeTextBlock.Text = preview.RestoreSummary;
        ManifestTextBox.Text = string.IsNullOrWhiteSpace(preview.ManifestText) ? "(none)" : preview.ManifestText;
        FilesTextBox.Text = preview.Files.Count == 0
            ? "(no files)"
            : string.Join(Environment.NewLine, preview.Files);

        var folders = preview.Files
            .Select(GetTopFolder)
            .GroupBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key} ({group.Count()} files)")
            .ToArray();

        var targets = new List<string>();
        targets.Add("Folder targets");
        targets.AddRange(preview.FolderTargets.Count == 0 ? ["  (none)"] : preview.FolderTargets.Select(path => "  " + path));
        targets.Add("File targets");
        targets.AddRange(preview.FileTargets.Count == 0 ? ["  (none)"] : preview.FileTargets.Select(path => "  " + path));
        if (preview.MissingTargets.Count > 0)
        {
            targets.Add("Missing when backup was created");
            targets.AddRange(preview.MissingTargets.Select(path => "  " + path));
        }
        if (preview.FileTargets.Count == 0 && preview.FolderTargets.Count == 0)
        {
            targets.Add("Archive folders (legacy manifest)");
            targets.AddRange(folders.Length == 0 ? ["  (none)"] : folders.Select(folder => "  " + folder));
        }

        FolderSummaryListBox.ItemsSource = targets;
    }

    private static string GetTopFolder(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var slash = normalized.IndexOf('/');
        return slash <= 0 ? Path.GetFileName(normalized) : normalized[..slash];
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes} B";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
