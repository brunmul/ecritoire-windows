using System.Windows;
using System.Windows.Controls;

namespace Ecritoire.App;

/// <summary>Rendu « encre » du texte (menu ⋯ › Rendu de l'encre) : style et intensité, gardés dans les réglages.</summary>
public partial class MainWindow
{
    InkLook? ink;

    void InitInk()
    {
        ink = new InkLook(Editor);
        InkIntensitySlider.Value = Math.Clamp(settings.InkIntensity, InkIntensitySlider.Minimum, InkIntensitySlider.Maximum);
        ApplyInk();
    }

    void ApplyInk()
    {
        ink?.Configure(settings.InkStyle, settings.InkIntensity);
        foreach (var item in InkMenu.Items.OfType<MenuItem>())
            if (item.Tag is string tag) item.IsChecked = tag == settings.InkStyle.ToString();
        InkIntensityItem.IsEnabled = settings.InkStyle != InkStyle.None;
        InkIntensityText.Text = $"{Math.Round(settings.InkIntensity * 100)} %";
        InkMenu.ToolTip = InkEffect.Supported ? null : "Demande une carte graphique : en bureau à distance, le texte reste net.";
    }

    void InkStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<InkStyle>(tag, out var style)) settings.InkStyle = style;
        ApplyInk();
    }

    void InkIntensityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (InkIntensityText == null) return; // pendant InitializeComponent
        settings.InkIntensity = Math.Round(e.NewValue, 2);
        ApplyInk();
    }
}
