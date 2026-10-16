using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Ecritoire.App;

/// <summary>Petite fenêtre « Renommer » : un champ, Annuler / OK.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string label, string value)
    {
        var box = new TextBox { Text = value, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 6, 0, 14) };
        var ok = new Button { Content = "Renommer", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };
        var panel = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = label }, box, buttons } };
        var window = new Window
        {
            Title = title, Owner = owner, Content = panel, Width = 380, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = owner.FontFamily, FontSize = 13, Background = (System.Windows.Media.Brush)owner.FindResource("Entete"),
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
