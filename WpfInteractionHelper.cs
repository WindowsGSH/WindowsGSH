using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfDataGrid = System.Windows.Controls.DataGrid;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfScrollViewer = System.Windows.Controls.ScrollViewer;
using WpfTabControl = System.Windows.Controls.TabControl;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace WindowsGSH;

public static class WpfInteractionHelper
{
    public static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is WpfButtonBase ||
                source is WpfTextBox ||
                source is WpfPasswordBox ||
                source is WpfComboBox ||
                source is WpfListBox ||
                source is WpfDataGrid ||
                source is WpfScrollViewer ||
                source is WpfTabControl ||
                source is Hyperlink)
            {
                return true;
            }

            source = GetParent(source);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject source)
    {
        if (source is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(source);
        }

        if (source is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }

        if (source is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement);
        }

        return null;
    }
}
