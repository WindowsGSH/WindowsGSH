using System.Windows;
using System.Windows.Controls;
using WindowsGSH.Core.Modules;

namespace WindowsGSH;

// Shared by ServerConfigEditorWindow and InstallServerWindow so a layout fix only needs to be
// made once. These two windows used to keep independent copies of this method, which let the
// label column drift out of sync (missing TextWrapping/margin in one copy caused long labels to
// run into the adjacent control).
internal static class ConfigFieldRowBuilder
{
    public static string GetFieldLabel(ConfigFieldDefinition field)
    {
        return field.RestartRequired ? $"{field.Label} (restart)" : field.Label;
    }

    public static void AddRow(StackPanel panel, string label, FrameworkElement control, string? description)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0)
        };
        labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var stack = new StackPanel();
        stack.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionBlock = new TextBlock { Text = description, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            descriptionBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            stack.Children.Add(descriptionBlock);
        }

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        panel.Children.Add(grid);
    }
}
