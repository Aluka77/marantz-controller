using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MarantzController;

/// <summary>
/// Egysoros, vízszintesen scrollozó (futófény) szövegkijelző fix szélességben.
/// Ha a szöveg elfér, középre igazítva, mozdulatlanul áll; ha hosszabb a
/// rendelkezésre álló szélességnél, jobbról balra végtelen ciklusban fut.
/// A betűméret/szín a <c>TextElement.FontSize</c>/<c>TextElement.Foreground</c>
/// attached property-ken keresztül öröklődik (a XAML-ben ezeket állítjuk be).
/// </summary>
public sealed class Marquee : Canvas
{
    private readonly TextBlock _text;
    private readonly TranslateTransform _xform = new();

    private const double PixelsPerSecond = 26; // futási sebesség (kényelmes olvasáshoz)
    private const double GapPx = 48;            // rés a ciklus végén, mielőtt újraindul

    public Marquee()
    {
        ClipToBounds = true;
        _text = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            RenderTransform = _xform,
        };
        Children.Add(_text);

        SizeChanged += (_, _) => Restart();
        Loaded += (_, _) => Restart();
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Marquee),
            new PropertyMetadata("", OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var m = (Marquee)d;
        m._text.Text = e.NewValue as string ?? "";
        m.Restart();
    }

    private void Restart()
    {
        // Korábbi animáció leállítása.
        _xform.BeginAnimation(TranslateTransform.XProperty, null);

        if (ActualWidth <= 0)
            return;

        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = _text.DesiredSize.Width;
        SetTop(_text, (ActualHeight - _text.DesiredSize.Height) / 2);

        if (textWidth <= ActualWidth || string.IsNullOrEmpty(_text.Text))
        {
            // Elfér: középre, állva.
            _xform.X = (ActualWidth - textWidth) / 2;
            return;
        }

        // Nem fér el: jobbról beúszik, balra kiúszik, végtelen ciklus.
        double from = ActualWidth;
        double to = -(textWidth + GapPx);
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds((from - to) / PixelsPerSecond))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _xform.BeginAnimation(TranslateTransform.XProperty, anim);
    }
}
