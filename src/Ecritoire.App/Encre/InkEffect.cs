using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;

namespace Ecritoire.App;

/// <summary>Styles d'encre, repris des filtres de Papertooth (https://github.com/tbudin/papertooth, MIT).</summary>
public enum InkStyle
{
    /// <summary>Texte net, sans effet.</summary>
    None,
    /// <summary>« pt-ink-text » : la couverture d'encre varie par grandes plaques. Le plus discret.</summary>
    Plates,
    /// <summary>« pt-ink-head » : plaques et bords légèrement rongés.</summary>
    Edges,
    /// <summary>« typewriter » : l'encre bave (flou + seuil), bords rongés, plaques et petits manques.</summary>
    Typewriter,
}

/// <summary>Réglages d'un style : les valeurs par défaut de Papertooth (src/js/filters.js), en pixels indépendants (DIP).</summary>
readonly record struct InkPreset(
    double Displacement, double DisplacementFrequency,
    double BlotFrequencyX, double BlotFrequencyY, double DensityGain, double DensityBias,
    double Spread, double BlurDeviation, double ThresholdSlope, double ThresholdIntercept,
    double Wear, double WearFrequency, double WearGain, double WearBias)
{
    public static InkPreset For(InkStyle style) => style switch
    {
        // density .7 : opacité = 0,7 × plaque + (1 − 0,64 × 0,7)
        InkStyle.Plates => new(0, 1.4, 0.035, 0.08, 0.7, 1 - 0.64 * 0.7, 0, 0.12, 3.2, -0.29, 0, 1.9, -14, 11.08),
        // rough 1, density .8
        InkStyle.Edges => new(1.0, 1.4, 0.03, 0.07, 0.8, 1 - 0.62 * 0.8, 0, 0.12, 3.2, -0.29, 0, 1.9, -14, 11.08),
        // spread .6, rough 1.2, uneven .7, speckle .5
        InkStyle.Typewriter => new(1.2, 1.1, 0.035, 0.09, 1.4 * 0.7, 1 - 0.72 * 0.7, 1, 0.12 + 0.6 * 0.6, 3.2, -0.35 + 0.1 * 0.6,
            1, 1.9, -14, 1 + 14 * (0.8 - 0.16 * 0.5)),
        _ => default,
    };
}

/// <summary>Pixel shader « encre » (Encre/Encre.fx, compilé en Encre/Encre.ps). Les registres sont décrits dans le .fx.</summary>
public sealed class InkEffect : ShaderEffect
{
    static readonly Lazy<PixelShader?> Shader = new(() =>
    {
        try
        {
            var shader = new PixelShader { UriSource = new Uri("pack://application:,,,/Ecritoire;component/Encre/Encre.ps") };
            shader.Freeze();
            return shader;
        }
        catch (Exception e) { App.Log(e); return null; }
    });
    static bool broken;

    static InkEffect()
    {
        // Un shader refusé par le pilote : on n'insiste pas, le texte reste net.
        PixelShader.InvalidPixelShaderEncountered += (_, _) => broken = true;
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(InkEffect), 0, SamplingMode.Bilinear);

    static DependencyProperty Register(string name, int register) =>
        DependencyProperty.Register(name, typeof(Point4D), typeof(InkEffect), new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(register)));

    public static readonly DependencyProperty GeometryProperty = Register(nameof(Geometry), 0);
    public static readonly DependencyProperty PixelProperty = Register(nameof(Pixel), 1);
    public static readonly DependencyProperty DisplacementProperty = Register(nameof(Displacement), 2);
    public static readonly DependencyProperty BlotProperty = Register(nameof(Blot), 3);
    public static readonly DependencyProperty DensityProperty = Register(nameof(Density), 4);
    public static readonly DependencyProperty SpreadProperty = Register(nameof(Spread), 5);
    public static readonly DependencyProperty WearProperty = Register(nameof(Wear), 6);

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point4D Geometry { get => (Point4D)GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public Point4D Pixel { get => (Point4D)GetValue(PixelProperty); set => SetValue(PixelProperty, value); }
    public Point4D Displacement { get => (Point4D)GetValue(DisplacementProperty); set => SetValue(DisplacementProperty, value); }
    public Point4D Blot { get => (Point4D)GetValue(BlotProperty); set => SetValue(BlotProperty, value); }
    public Point4D Density { get => (Point4D)GetValue(DensityProperty); set => SetValue(DensityProperty, value); }
    public Point4D Spread { get => (Point4D)GetValue(SpreadProperty); set => SetValue(SpreadProperty, value); }
    public Point4D Wear { get => (Point4D)GetValue(WearProperty); set => SetValue(WearProperty, value); }

    public InkEffect()
    {
        PixelShader = Shader.Value;
        UpdateShaderValue(InputProperty);
    }

    /// <summary>Le shader est en modèle 3.0 : WPF ne l'applique qu'avec une carte graphique (pas en bureau à distance
    /// logiciel). Sans elle, le texte reste net.</summary>
    public static bool Supported =>
        !broken && Shader.Value != null && RenderCapability.Tier >> 16 >= 2 && RenderCapability.IsPixelShaderVersionSupported(3, 0);
}

/// <summary>
/// Pose l'effet sur la seule couche de texte d'AvalonEdit : le curseur, la sélection et les filets restent nets.
/// Le grain est ancré au document (on suit le défilement) et mesuré en DIP (il ne change pas avec le zoom de l'écran).
/// </summary>
public sealed class InkLook
{
    const double Period = 256; // période du réseau de bruit dans Encre.fx
    readonly TextView view;
    readonly InkEffect effect = new();
    UIElement? layer;
    InkPreset preset;
    double intensity = 1;
    bool active;

    public InkLook(TextEditor editor)
    {
        view = editor.TextArea.TextView;
        view.ScrollOffsetChanged += (_, _) => UpdateGeometry();
        view.SizeChanged += (_, _) => UpdateGeometry();
        view.Loaded += (_, _) => { Attach(); UpdateGeometry(); };
        RenderCapability.TierChanged += (_, _) => Attach();
    }

    /// <summary>Choisit le style et son intensité (1 = réglages de Papertooth ; 0 = net ; jusqu'à 1,5).</summary>
    public void Configure(InkStyle style, double intensity)
    {
        this.intensity = Math.Clamp(intensity, 0, 1.5);
        preset = InkPreset.For(style);
        active = style != InkStyle.None && this.intensity > 0;
        Attach();
        UpdateParameters();
        UpdateGeometry();
    }

    void Attach()
    {
        layer ??= view.Layers.OfType<UIElement>().FirstOrDefault(l => l.GetType().Name == "TextLayer");
        if (layer == null) return;
        var want = active && InkEffect.Supported ? effect : null;
        if (!ReferenceEquals(layer.Effect, want)) layer.Effect = want;
    }

    void UpdateParameters()
    {
        var p = preset;
        double t = intensity;
        effect.Density = new Point4D(p.DensityGain * t, 1 + t * (p.DensityBias - 1), Math.Min(1, p.Spread * t), Math.Min(1, p.Wear * t));
        effect.Spread = new Point4D(p.ThresholdSlope, p.ThresholdIntercept, p.WearFrequency, 0);
    }

    void UpdateGeometry()
    {
        if (!active || layer == null) return;
        var p = preset;
        double w = Math.Max(1, view.ActualWidth), h = Math.Max(1, view.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(view);
        // Origine du bruit = position dans le document ; prise modulo la période en double précision pour que le shader
        // (en float) garde un grain fin même au bout d'un long chapitre.
        double ox = view.HorizontalOffset, oy = view.VerticalOffset;
        static double Mod(double v) => v - Period * Math.Floor(v / Period);
        // Poids du flou 3 × 3 (gaussienne d'écart type BlurDeviation, en pixels de l'écran).
        double sd = Math.Max(0.05, p.BlurDeviation * dpi.DpiScaleX);
        double side = Math.Exp(-1 / (2 * sd * sd)), corner = Math.Exp(-2 / (2 * sd * sd));

        effect.Geometry = new Point4D(w, h, 1 / w, 1 / h);
        effect.Pixel = new Point4D(1 / (w * dpi.DpiScaleX), 1 / (h * dpi.DpiScaleY), side, corner);
        effect.Displacement = new Point4D(p.Displacement * intensity, p.DisplacementFrequency,
            Mod(ox * p.DisplacementFrequency), Mod(oy * p.DisplacementFrequency));
        effect.Blot = new Point4D(p.BlotFrequencyX, p.BlotFrequencyY, Mod(ox * p.BlotFrequencyX), Mod(oy * p.BlotFrequencyY));
        effect.Wear = new Point4D(Mod(ox * p.WearFrequency), Mod(oy * p.WearFrequency), p.WearGain, p.WearBias);
    }
}
