using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MarantzController;

public partial class MainWindow : Window
{
    private double _fullWidth = 820;

    // Mini nézet FIX szélessége – ne ugráljon a most-szóló szöveg hosszától.
    // (380-ról 20%-kal keskenyebb: kevésbé fekvő téglalap, jobb borító-arányok.)
    private const double CompactWidth = 304;

    // A teljes nézet minimum szélessége (a XAML MinWidth-tel egyezik).
    private const double FullMinWidth = 620;

    // Melyik módot alkalmaztuk utoljára (null = még egyszer sem).
    private bool? _appliedCompact;

    public MainWindow()
    {
        InitializeComponent();

        // A hangerő-csúszka húzásának kezdetét/végét jelezzük a ViewModelnek,
        // hogy húzás közben a vevő visszaigazolásai ne rángassák a csúszkát.
        WireVolumeSlider(VolumeSliderCompact);

        // A knob ugyanazt a drag-védelmet használja, mint a csúszka.
        Knob.InteractionStarted += () => Vm?.BeginVolumeDrag();
        Knob.InteractionCompleted += () => Vm?.EndVolumeDrag();
        Knob.MuteRequested += () => Vm?.MuteToggleCommand.Execute(null);
        Knob.ScaleLabelClicked += () => Vm?.ToggleVolumeScaleCommand.Execute(null);

        DataContextChanged += OnDataContextChanged;
        if (DataContext is MainViewModel vm)
            HookVm(vm);

        // EQ-görbe: layout és értékváltozás után frissül.
        EqItems.SizeChanged += (_, _) => ScheduleEqCurveUpdate();
        Loaded += (_, _) => ScheduleEqCurveUpdate();

        // Induláskor is a tartalomhoz méretezzünk (ne maradjon üres sáv alul).
        Loaded += (_, _) => ApplyCompactMode(Vm?.IsCompact == true);

        // --mini kapcsolóval az app rögtön mini nézetben indul.
        if (Environment.GetCommandLineArgs().Contains("--mini") && Vm is { } startVm)
            Loaded += (_, _) => startVm.IsCompact = true;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void WireVolumeSlider(System.Windows.Controls.Slider slider)
    {
        slider.PreviewMouseLeftButtonDown += (_, _) => Vm?.BeginVolumeDrag();
        slider.PreviewMouseLeftButtonUp += (_, _) => Vm?.EndVolumeDrag();
    }

    // Win11: lekerekített ablaksarkok kérése a DWM-től (borderless ablaknál nem automatikus).
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch
        {
            // Win10 vagy régebbi DWM: marad a szögletes sarok – nem hiba.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---- Címsor / ablakvezérlés ----

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch { /* gyors dupla-events alatt előfordulhat */ }
    }

    private void MiniRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Gombok/csúszkák lenyelik a kattintást; ami ideér, az húzható felület.
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- ViewModel-kapcsolás ----

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            UnhookVm(oldVm);
        if (e.NewValue is MainViewModel newVm)
            HookVm(newVm);
    }

    private void HookVm(MainViewModel vm)
    {
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.ChannelLevels.CollectionChanged += OnChannelLevelsChanged;
        foreach (var ch in vm.ChannelLevels)
            ch.PropertyChanged += OnChannelValueChanged;
    }

    private void UnhookVm(MainViewModel vm)
    {
        vm.PropertyChanged -= OnVmPropertyChanged;
        vm.ChannelLevels.CollectionChanged -= OnChannelLevelsChanged;
        foreach (var ch in vm.ChannelLevels)
            ch.PropertyChanged -= OnChannelValueChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCompact))
            ApplyCompactMode(Vm?.IsCompact == true);
        else if (e.PropertyName == nameof(MainViewModel.IsChannelLevelsExpanded))
            ScheduleEqCurveUpdate();
    }

    private void OnChannelLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Új elemek (csatlakozáskor újraépül a lista) – feliratkozás + újrarajzolás.
        if (e.NewItems is not null)
            foreach (ChannelLevel ch in e.NewItems)
                ch.PropertyChanged += OnChannelValueChanged;
        if (e.OldItems is not null)
            foreach (ChannelLevel ch in e.OldItems)
                ch.PropertyChanged -= OnChannelValueChanged;
        ScheduleEqCurveUpdate();
    }

    private void OnChannelValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelLevel.Value))
            ScheduleEqCurveUpdate();
    }

    // ---- Kompakt mód ----

    /// <summary>
    /// Mindkét nézet a TARTALOMHOZ igazítja a magasságát (SizeToContent.Height), így
    /// nem marad üres sáv alul, ha a szekciók be vannak csukva. A MaxHeight a képernyő
    /// munkaterületére korlátoz – ha a tartalom ennél magasabb, a ScrollViewer görgeti.
    /// </summary>
    private void ApplyCompactMode(bool compact)
    {
        // A JOBB szél marad a helyén: a nézetváltás balra növeszti/húzza az ablakot.
        // Ezt MINDKÉT irányban alkalmazni kell. Ha csak a visszatérés horgonyozna
        // jobbra, a mini-be lépés viszont balra, akkor minden oda-vissza váltás
        // (teljes szélesség − mini szélesség)-nyit sodorna balra az ablakon.
        // Az első hívásnál (induláskor) nem mozgatunk: ott az ablak indulási
        // pozíciója az érvényes.
        bool anchorRight = _appliedCompact is not null && !double.IsNaN(Left);
        double rightEdge = Left + (ActualWidth > 0 ? ActualWidth : Width);

        // A szélesség-állítás előtt KI kell kapcsolni a SizeToContent-et, különben a
        // WindowChrome-os ablaknál a Width értékadás elveszik (mérve: miniből
        // visszatérve 620 maradt a 820 helyett).
        SizeToContent = SizeToContent.Manual;

        if (compact)
        {
            // A teljes nézet szélességét CSAK belépéskor mentjük. Ha már kompakt
            // módban vagyunk és ez újra lefut, az ActualWidth a mini szélesség –
            // azt elmentve a visszatérés a MinWidth-re esett vissza (620 a 820 helyett).
            if (_appliedCompact != true)
                _fullWidth = Math.Max(ActualWidth > 0 ? ActualWidth : Width, FullMinWidth);
            // FONTOS: a Min/MaxWidth-tel is leszorítjuk. A puszta Width-értékadás
            // WindowChrome-os, SizeToContent-es ablaknál nem érvényesült megbízhatóan
            // (mérve: 380 helyett 560 maradt) – a MaxWidth viszont kényszeríti.
            MinWidth = CompactWidth;
            MaxWidth = CompactWidth;
            Width = CompactWidth;
        }
        else
        {
            // MaxWidth ELŐBB, különben egy pillanatra MinWidth > MaxWidth állna fenn.
            MaxWidth = double.PositiveInfinity;
            MinWidth = FullMinWidth;
            Width = _fullWidth;
        }

        if (anchorRight)
        {
            double target = compact ? CompactWidth : _fullWidth;
            var wa = GetWorkArea();
            double left = rightEdge - target;
            // Ne lógjon ki: előbb a jobb, aztán a bal szélre vágunk (a bal nyer,
            // ha az ablak szélesebb, mint a munkaterület).
            if (left + target > wa.Right)
                left = wa.Right - target;
            if (left < wa.Left)
                left = wa.Left;
            Left = left;
        }

        MaxHeight = GetWorkArea().Height;
        SizeToContent = SizeToContent.Height;
        _appliedCompact = compact;
    }

    /// <summary>
    /// Az ABLAKOT tartalmazó monitor munkaterülete WPF-egységben (DIP).
    /// A SystemParameters.WorkArea csak az elsődleges kijelzőt ismeri – egy balra
    /// eső második monitoron (negatív koordináták) rossz helyre rántaná az ablakot.
    /// </summary>
    private Rect GetWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                // A Win32 adat fizikai pixel; a Left/Width DIP – át kell váltani.
                var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                             ?? Matrix.Identity;
                var topLeft = matrix.Transform(new Point(mi.rcWork.Left, mi.rcWork.Top));
                var bottomRight = matrix.Transform(new Point(mi.rcWork.Right, mi.rcWork.Bottom));
                return new Rect(topLeft, bottomRight);
            }
        }
        return SystemParameters.WorkArea;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }

    // ---- EQ-görbe (arany vonal a fader-fejek közt) ----

    private bool _eqUpdateQueued;

    private void ScheduleEqCurveUpdate()
    {
        if (_eqUpdateQueued)
            return;
        _eqUpdateQueued = true;
        // Layout után fusson, hogy a fader-pozíciók már véglegesek legyenek.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _eqUpdateQueued = false;
            UpdateEqCurve();
        });
    }

    private void UpdateEqCurve()
    {
        EqCurve.Points.Clear();
        if (Vm?.IsChannelLevelsExpanded != true || EqItems.Items.Count == 0)
            return;

        var points = new PointCollection();
        for (int i = 0; i < EqItems.Items.Count; i++)
        {
            if (EqItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            var fader = FindDescendant<System.Windows.Controls.Slider>(container);
            if (fader is null || fader.ActualHeight <= 0)
                continue;

            // A thumb középpontja a fader értéke alapján (thumb magasság ~15 px).
            const double thumbH = 15;
            double frac = (fader.Value - fader.Minimum) / (fader.Maximum - fader.Minimum);
            var origin = fader.TransformToVisual(EqCanvas).Transform(default);
            double x = origin.X + fader.ActualWidth / 2;
            double y = origin.Y + thumbH / 2 + (1 - frac) * (fader.ActualHeight - thumbH);
            points.Add(new System.Windows.Point(x, y));
        }

        if (points.Count >= 2)
            EqCurve.Points = points;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
                return hit;
            if (FindDescendant<T>(child) is T deeper)
                return deeper;
        }
        return null;
    }
}
