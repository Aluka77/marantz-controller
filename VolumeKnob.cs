using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace MarantzController;

/// <summary>
/// Kör alakú hangerő-vezérlő („knob") krómozott fémgyűrűvel, tisztán vektorosan
/// (nincs képfájl). A króm hatás egy szög szerint változó tónusú gyűrű: a fémet
/// N darab keskeny körcikkből rajzoljuk, mindegyiket a szögéhez tartozó
/// világossággal – ezzel utánozzuk a pörgetett fém négy csillanását és a finom
/// szálcsiszolt striákat. (WPF-ben nincs kúpos gradiens, ez a bevált kerülőút.)
/// A gyűrű egyetlen befagyasztott DrawingImage, tehát 240 cikk is egy elem marad.
///
/// Interakció:
///  - a gyűrűre kattintva/vonszolva odaáll a szöghöz (mint egy igazi potméter),
///  - a belső korongon relatív, függőleges finomhangolás,
///  - görgő: ±0,5, dupla kattintás: némítás.
/// </summary>
public sealed class VolumeKnob : Grid
{
    private const double Size = 176;
    private const double Center = Size / 2;

    // Sugarak kívülről befelé: arany értékív → króm gyűrű → belső korong.
    private const double ArcRadius = 80;
    private const double RingOuter = 70;
    private const double RingInner = 52;
    private const double DiscRadius = 52;

    private const double StartAngle = 135;  // bal-alsó
    private const double SweepAngle = 270;  // háromnegyed kör
    private const int ChromeSegments = 240;

    private const double DragPixelsForFullRange = 260;

    private readonly Path _arc;
    private readonly TextBlock _valueText;
    private readonly TextBlock _subText;

    private bool _dragging;
    private bool _angularMode;
    private System.Windows.Point _dragStart;
    private double _dragStartValue;

    public event Action? InteractionStarted;
    public event Action? InteractionCompleted;
    public event Action? MuteRequested;
    /// <summary>A skálafeliratra (dB/skála) kattintás — a kijelzésmód váltásához.</summary>
    public event Action? ScaleLabelClicked;

    public VolumeKnob()
    {
        Width = Size;
        Height = Size;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent; // hit-test a teljes területen

        // --- Króm gyűrű: egyetlen, befagyasztott vektoros rajz ---
        var chrome = new Image
        {
            Source = BuildChromeRing(),
            Width = Size,
            Height = Size,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.65,
            },
        };

        // --- Arany pálya (a még nem kitöltött rész) ---
        var track = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x16, 0xF0, 0xC8, 0x7A)),
            StrokeThickness = 4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = BuildArc(SweepAngle),
            IsHitTestVisible = false,
        };

        // --- Arany értékív ---
        _arc = new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0xC8, 0x7A)),
            StrokeThickness = 4.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0xF0, 0xC8, 0x7A),
                BlurRadius = 13,
                ShadowDepth = 0,
                Opacity = 0.7,
            },
        };

        // --- Belső korong: sötét, felülről megvilágítva, vékony fémperemmel ---
        var disc = new Ellipse
        {
            Width = DiscRadius * 2,
            Height = DiscRadius * 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x20, 0x20, 0x24)),
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new System.Windows.Point(0.5, 0.28),
                Center = new System.Windows.Point(0.5, 0.42),
                RadiusX = 0.8,
                RadiusY = 0.8,
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x2B, 0x2B, 0x30), 0),
                    new GradientStop(Color.FromRgb(0x17, 0x17, 0x1A), 0.72),
                    new GradientStop(Color.FromRgb(0x0D, 0x0D, 0x0F), 1),
                },
            },
            IsHitTestVisible = false,
        };

        // A korong felső belső pereme: halvány fényvisszaverődés (üvegesség).
        var discSheen = new Ellipse
        {
            Width = DiscRadius * 2 - 2,
            Height = DiscRadius * 2 - 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
            Stroke = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.45),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
                },
            },
        };

        var label = new TextBlock
        {
            Text = "V O L U M E",
            FontSize = 8.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x92)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _valueText = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.Light,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        };

        _subText = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xA0, 0x5C)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Toggle display: relative scale / absolute dB",
        };
        _subText.MouseLeftButtonDown += (_, e) => { ScaleLabelClicked?.Invoke(); e.Handled = true; };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(label);
        stack.Children.Add(_valueText);
        stack.Children.Add(_subText);

        Children.Add(chrome);
        Children.Add(track);
        Children.Add(_arc);
        Children.Add(disc);
        Children.Add(discSheen);
        Children.Add(stack);

        UpdateArc();
    }

    // ---- DP-k ----

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(VolumeKnob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((VolumeKnob)d).UpdateArc()));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(VolumeKnob),
        new PropertyMetadata(98.0, (d, _) => ((VolumeKnob)d).UpdateArc()));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(VolumeKnob),
        new PropertyMetadata("", (d, e) => ((VolumeKnob)d)._valueText.Text = (string)e.NewValue));

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(VolumeKnob),
        new PropertyMetadata("", (d, e) => ((VolumeKnob)d)._subText.Text = (string)e.NewValue));

    public string SubText
    {
        get => (string)GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    // ---- Króm gyűrű felépítése ----

    /// <summary>
    /// A fémgyűrű tónusa a szög függvényében. Négy csillanás (pörgetett fém),
    /// felülről jövő fény, és finom, magas frekvenciás striák a csiszolt hatáshoz.
    /// </summary>
    private static double ChromeTone(double deg)
    {
        double th = deg * Math.PI / 180.0;

        // Négy csillanás a kör körül (|cos 2θ| két maximumot ad félkörönként).
        // A nagy kitevő KESKENY csillanást ad széles sötét mezőkkel – így a fém
        // sötét szobát tükröző, polírozott krómnak látszik, nem világos gyűrűnek.
        double lobes = Math.Abs(Math.Cos(2 * (th - 0.38)));
        double b = 0.09 + 0.84 * Math.Pow(lobes, 2.4);

        // Felülről jövő fény: a felső ív (y-down rendszerben sin θ < 0) világosabb.
        double topLight = (1 - Math.Sin(th)) / 2;
        b *= 0.74 + 0.34 * topLight;

        // Szálcsiszolt striák (visszafogottan – nagyobb amplitúdónál moiré-zik).
        b += 0.018 * Math.Sin(deg * 5.7) + 0.010 * Math.Sin(deg * 13.3);

        return Math.Clamp(b, 0.04, 1.0);
    }

    private static ImageSource BuildChromeRing()
    {
        var group = new DrawingGroup();

        // A körcikkek: mindegyik a saját szögéhez tartozó fémtónussal.
        double step = 360.0 / ChromeSegments;
        for (int i = 0; i < ChromeSegments; i++)
        {
            double a0 = i * step;
            double a1 = a0 + step + 0.35; // apró átfedés, hogy ne legyen hajszálrés
            double tone = ChromeTone(a0 + step / 2);

            byte v = (byte)Math.Round(tone * 255);
            // Alig meleg szürke: a hideg kék helyett illeszkedik az arany témához.
            var color = Color.FromRgb(v, (byte)(v * 0.99), (byte)(v * 0.96));

            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(color), null, BuildRingSegment(a0, a1)));
        }

        // Peremek: kívül sötét kontúr, belül világosabb – ez adja a fazettát.
        group.Children.Add(new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x0A, 0x0A, 0x0C)), 1.2),
            new EllipseGeometry(new System.Windows.Point(Center, Center), RingOuter, RingOuter)));
        group.Children.Add(new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1),
            new EllipseGeometry(new System.Windows.Point(Center, Center), RingInner + 0.6, RingInner + 0.6)));
        group.Children.Add(new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x0A, 0x0A, 0x0C)), 1),
            new EllipseGeometry(new System.Windows.Point(Center, Center), RingInner - 0.6, RingInner - 0.6)));

        group.Freeze();
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }

    /// <summary>Egy gyűrű-szelet (körcikk-gyűrű) geometriája a0..a1 fok között.</summary>
    private static Geometry BuildRingSegment(double a0, double a1)
    {
        var pOuter0 = OnCircle(a0, RingOuter);
        var pOuter1 = OnCircle(a1, RingOuter);
        var pInner1 = OnCircle(a1, RingInner);
        var pInner0 = OnCircle(a0, RingInner);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pOuter0, true, true);
            ctx.ArcTo(pOuter1, new System.Windows.Size(RingOuter, RingOuter), 0, false,
                      SweepDirection.Clockwise, true, false);
            ctx.LineTo(pInner1, true, false);
            ctx.ArcTo(pInner0, new System.Windows.Size(RingInner, RingInner), 0, false,
                      SweepDirection.Counterclockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    // ---- Geometria ----

    private static System.Windows.Point OnCircle(double angleDeg, double radius)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new System.Windows.Point(
            Center + radius * Math.Cos(rad),
            Center + radius * Math.Sin(rad));
    }

    private static Geometry BuildArc(double sweep)
    {
        sweep = Math.Clamp(sweep, 0, SweepAngle);
        if (sweep < 0.5)
            return Geometry.Empty;

        var start = OnCircle(StartAngle, ArcRadius);
        var end = OnCircle(StartAngle + sweep, ArcRadius);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new System.Windows.Size(ArcRadius, ArcRadius), 0,
                      sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    private void UpdateArc()
    {
        double max = Maximum <= 0 ? 1 : Maximum;
        double frac = Math.Clamp(Value / max, 0, 1);
        _arc.Data = BuildArc(SweepAngle * frac);
    }

    // ---- Interakció ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            MuteRequested?.Invoke();
            e.Handled = true;
            return;
        }

        var p = e.GetPosition(this);
        double r = Radius(p);

        // A knob körén kívül (sarkok) ne csináljunk semmit.
        if (r > ArcRadius + 8)
            return;

        _dragging = true;
        _dragStart = p;
        _dragStartValue = Value;

        // A fémgyűrűn/íven kattintva odaállunk a szöghöz; a belső korongon
        // relatív finomhangolás indul.
        _angularMode = r >= RingInner;
        if (_angularMode && TryValueFromPoint(p, out double v))
            Value = v;

        CaptureMouse();
        InteractionStarted?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;

        var p = e.GetPosition(this);
        if (_angularMode)
        {
            if (TryValueFromPoint(p, out double v))
                Value = v;
            return;
        }

        double dy = _dragStart.Y - p.Y; // felfelé pozitív
        double delta = dy * Maximum / DragPixelsForFullRange;
        Value = Math.Clamp(_dragStartValue + delta, 0, Maximum);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        InteractionCompleted?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = Math.Clamp(Value + (e.Delta > 0 ? 0.5 : -0.5), 0, Maximum);
        e.Handled = true;
    }

    private static double Radius(System.Windows.Point p)
    {
        double dx = p.X - Center, dy = p.Y - Center;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Pont → hangerő a kör szöge alapján. A skála a StartAngle-tól SweepAngle-ig
    /// tart óramutató irányban; a hézagban (alul) a közelebbi végállásra kerekítünk.
    /// </summary>
    private bool TryValueFromPoint(System.Windows.Point p, out double value)
    {
        value = 0;
        double dx = p.X - Center, dy = p.Y - Center;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
            return false;

        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double rel = (deg - StartAngle) % 360;
        if (rel < 0) rel += 360;

        if (rel > SweepAngle)
        {
            double distToEnd = rel - SweepAngle;
            double distToStart = 360 - rel;
            rel = distToEnd < distToStart ? SweepAngle : 0;
        }

        value = Math.Clamp(rel / SweepAngle * Maximum, 0, Maximum);
        return true;
    }
}
