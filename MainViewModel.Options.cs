using System.Collections.ObjectModel;
using System.Windows.Input;

namespace MarantzController;

// ---------------------------------------------------------------------------
//  A vevő beállítás-katalógusa (Audyssey, hangszín, mélyláda, surround,
//  hangszóró-kalibráció, rendszer). Külön fájlban, hogy a MainViewModel
//  vezérlési logikája olvasható maradjon.
//
//  KÉT hatókör van, és ez lényeges:
//   • _audioParams (PS…)  – a vevő FORRÁSONKÉNT külön tárolja őket, ezért
//     minden forrás- és hangzásmód-váltás után újra le kell kérdezni.
//   • _systemParams (SS…, ECO) – globális/preset-szintű, elég ritkán frissíteni.
// ---------------------------------------------------------------------------

public sealed partial class MainViewModel
{
    private readonly AvOptionRegistry _audioParams = new();
    private readonly AvOptionRegistry _systemParams = new();

    // A bejövő jel adatai: ezeket a vevő NEM küldi magától, ezért pollozzuk.
    private readonly AvOptionRegistry _signalParams = new();

    // A keresztváltás és az LFE aluláteresztő választéka (SR5015 kézikönyv szerint).
    private static readonly (string, string)[] CrossoverChoices =
    {
        ("040", "40"), ("060", "60"), ("080", "80"), ("090", "90"), ("100", "100"),
        ("110", "110"), ("120", "120"), ("150", "150"), ("200", "200"), ("250", "250"),
    };

    private static readonly (string, string)[] LfeLpfChoices =
    {
        ("080", "80"), ("090", "90"), ("100", "100"), ("110", "110"), ("120", "120"),
        ("150", "150"), ("180", "180"), ("200", "200"), ("250", "250"),
    };

    private static readonly (string, string)[] OnOff = { ("ON", "On"), ("OFF", "Off") };

    // ---- Hangkép (dashboard „SOUND" kártya) ----

    public AvOption MultEq { get; private set; } = null!;
    public AvOption DynamicEq { get; private set; } = null!;
    public AvOption ReferenceLevel { get; private set; } = null!;
    public AvOption DynamicVolume { get; private set; } = null!;
    public AvOption ToneControl { get; private set; } = null!;
    public AvLevel Bass { get; private set; } = null!;
    public AvLevel Treble { get; private set; } = null!;
    public AvOption MDax { get; private set; } = null!;

    // ---- Mélyláda (az AUDIO EQUALIZER kártya csíkja) ----

    public AvOption SubwooferOn { get; private set; } = null!;
    public AvLevel SubwooferLevel { get; private set; } = null!;
    public AvLevel LfeLevel { get; private set; } = null!;

    // ---- Beállítások ablak: Hangszórók ----

    public AvOption FrontSize { get; private set; } = null!;
    public AvOption CenterSize { get; private set; } = null!;
    public AvOption SurroundSize { get; private set; } = null!;
    public AvOption SubwooferMode { get; private set; } = null!;
    public AvOption LfeLowPass { get; private set; } = null!;
    public AvOption CrossoverMode { get; private set; } = null!;
    public AvOption CrossoverAll { get; private set; } = null!;
    public AvOption CrossoverFront { get; private set; } = null!;
    public AvOption CrossoverCenter { get; private set; } = null!;
    public AvOption CrossoverSurround { get; private set; } = null!;

    // ---- Beállítások ablak: Rendszer ----

    public AvOption DisplayOut { get; private set; } = null!;
    public AvOption VolumeLimit { get; private set; } = null!;
    public AvOption PowerOnLevel { get; private set; } = null!;
    public AvOption MuteLevel { get; private set; } = null!;
    public AvOption EcoMode { get; private set; } = null!;
    public AvOption SignalFormat { get; private set; } = null!;
    public AvOption SignalLayout { get; private set; } = null!;
    public AvOption SampleRate { get; private set; } = null!;

    // ---- Csoportok az ItemsControl-okhoz ----

    public ObservableCollection<AvOption> AudysseyOptions { get; } = new();
    public ObservableCollection<AvOption> ToneOptions { get; } = new();
    public ObservableCollection<AvLevel> ToneLevels { get; } = new();
    public ObservableCollection<AvOption> SurroundOptions { get; } = new();
    public ObservableCollection<AvLevel> SurroundLevels { get; } = new();
    public ObservableCollection<AvOption> SpeakerOptions { get; } = new();
    public ObservableCollection<AvOption> CrossoverOptions { get; } = new();
    public ObservableCollection<AvOption> SystemOptions { get; } = new();

    /// <summary>A katalógus felépítése. A konstruktorból hívjuk.</summary>
    private void BuildOptionCatalog()
    {
        void Send(string cmd) => _ = SendAsync(cmd);

        // ---------------- Forrásonként tárolt (PS…) ----------------

        MultEq = _audioParams.Add(new AvOption("PSMULTEQ:", "MultEQ XT", Send, new[]
        {
            ("AUDYSSEY", "Audyssey"), ("BYP.LR", "Bypass L+R"), ("FLAT", "Flat"), ("OFF", "Off"),
        }));
        DynamicEq = _audioParams.Add(new AvOption("PSDYNEQ ", "Dynamic EQ", Send, OnOff));
        ReferenceLevel = _audioParams.Add(new AvOption("PSREFLEV ", "Reference level offset", Send, new[]
        {
            ("0", "0 dB"), ("5", "5 dB"), ("10", "10 dB"), ("15", "15 dB"),
        }));
        DynamicVolume = _audioParams.Add(new AvOption("PSDYNVOL ", "Dynamic Volume", Send, new[]
        {
            ("OFF", "Off"), ("LIT", "Light"), ("MED", "Medium"), ("HEV", "Heavy"),
        }));

        ToneControl = _audioParams.Add(new AvOption("PSTONE CTRL ", "Tone control", Send, OnOff));
        // Élőben mérve: a hangszín 1 dB-es lépésű (a „PSBAS 505" fél lépést a vevő
        // eldobja), és CSAK bekapcsolt Tone Control mellett fogadja el az értéket.
        Bass = _audioParams.Add(new AvLevel("PSBAS ", "Bass", Send,
            AvLevelKind.HalfStepDb, 44, 56, 1, zero: 50));
        Treble = _audioParams.Add(new AvLevel("PSTRE ", "Treble", Send,
            AvLevelKind.HalfStepDb, 44, 56, 1, zero: 50));

        MDax = _audioParams.Add(new AvOption("PSMDAX ", "M-DAX", Send, new[]
        {
            ("OFF", "Off"), ("LOW", "Low"), ("MID", "Medium"), ("HI", "High"),
        }));

        SubwooferOn = _audioParams.Add(new AvOption("PSSWR ", "Subwoofer", Send, OnOff));
        SubwooferLevel = _audioParams.Add(new AvLevel("PSSWL ", "Subwoofer level", Send,
            AvLevelKind.HalfStepDb, 38, 62, 0.5, zero: 50));
        LfeLevel = _audioParams.Add(new AvLevel("PSLFE ", "LFE level", Send,
            AvLevelKind.SignedInteger, -10, 0, 1));

        // Surround paraméterek – nagy részük hangzásmód-függő, ezért ha nem
        // válaszolnak, a UI-ból automatikusan eltűnnek (IsAvailable = false).
        AddSurround(new AvOption("PSCINEMA EQ.", "Cinema EQ", Send, OnOff));
        AddSurround(new AvOption("PSLOM ", "Loudness Management", Send, OnOff));
        AddSurround(new AvOption("PSDRC ", "Dynamic compression", Send, new[]
        {
            ("OFF", "Off"), ("LOW", "Low"), ("MID", "Medium"), ("HI", "High"), ("AUTO", "Auto"),
        }));
        AddSurround(new AvOption("PSSPV ", "Speaker virtualiser", Send, OnOff));
        AddSurround(new AvOption("PSCES ", "Center Spread", Send, OnOff));
        AddSurround(new AvOption("PSNEURAL ", "DTS Neural:X", Send, OnOff));

        AddSurroundLevel(new AvLevel("PSDIC ", "Dialog Control", Send,
            AvLevelKind.Integer, 0, 6, 1, digits: 2, unit: ""));
        AddSurroundLevel(new AvLevel("PSCLV ", "Center level", Send,
            AvLevelKind.HalfStepDb, 38, 62, 0.5, zero: 50));
        AddSurroundLevel(new AvLevel("PSEFF ", "Effect level", Send,
            AvLevelKind.Integer, 0, 15, 1, digits: 2, unit: ""));
        AddSurroundLevel(new AvLevel("PSDEL ", "Delay", Send,
            AvLevelKind.Integer, 0, 300, 10, digits: 3, unit: " ms"));
        AddSurroundLevel(new AvLevel("PSDELAY ", "Audio delay", Send,
            AvLevelKind.Integer, 0, 999, 10, digits: 3, unit: " ms"));

        // ---------------- Globális / preset-szintű (SS…, ECO) ----------------

        FrontSize = AddSpeaker(new AvOption("SSSPCFRO ", "Front", Send,
            new[] { ("LAR", "Large"), ("SMA", "Small") }, query: "SSSPC ?"));
        CenterSize = AddSpeaker(new AvOption("SSSPCCEN ", "Center", Send,
            new[] { ("LAR", "Large"), ("SMA", "Small") }, query: "SSSPC ?"));
        SurroundSize = AddSpeaker(new AvOption("SSSPCSUA ", "Surround", Send,
            new[] { ("LAR", "Large"), ("SMA", "Small") }, query: "SSSPC ?"));
        SubwooferMode = AddSpeaker(new AvOption("SSSWM ", "Subwoofer mode", Send,
            new[] { ("LFE", "LFE only"), ("L+M", "LFE + Main") }));
        LfeLowPass = AddSpeaker(new AvOption("SSLFL ", "LFE low-pass (Hz)", Send, LfeLpfChoices));

        CrossoverMode = AddCrossover(new AvOption("SSCFR ", "Mode", Send,
            new[] { ("ALL", "Uniform"), ("IDV", "Individual") }, query: "SSCFR ?"));
        CrossoverAll = AddCrossover(new AvOption("SSCFRALL ", "All speakers (Hz)", Send,
            CrossoverChoices, query: "SSCFR ?"));
        CrossoverFront = AddCrossover(new AvOption("SSCFRFRO ", "Front (Hz)", Send,
            CrossoverChoices, query: "SSCFR ?"));
        CrossoverCenter = AddCrossover(new AvOption("SSCFRCEN ", "Center (Hz)", Send,
            CrossoverChoices, query: "SSCFR ?"));
        CrossoverSurround = AddCrossover(new AvOption("SSCFRSUA ", "Surround (Hz)", Send,
            CrossoverChoices, query: "SSCFR ?"));

        // HDMI kijelző-kimenet. A vevőnek két monitor-kimenete van; a válasz alakja
        // „VSMONIAUTO" (szóköz nélkül), ezért a prefix is szóköz nélküli.
        // AUTO mellett mindkét kijelző közös nevezőre áll – 1080p projektor mellett
        // ez visszabutítja a 4K TV-t is, ezért érdemes kézzel választani.
        DisplayOut = _systemParams.Add(new AvOption("VSMONI", "Display", Send, new[]
        {
            ("AUTO", "Auto"), ("1", "1 — TV"), ("2", "2 — Projector"),
        }, query: "VSMONI ?"));

        // A token-hosszak ELTÉRNEK, és ez élőben mérve derült ki (2026-08-11):
        // LIM és PON KÉT jegyű („60", „40" – a „070"/„040" nem működik, a PON-nál
        // „04"-re csonkul), az MLV viszont HÁROM jegyű („040", „060").
        VolumeLimit = AddSystem(new AvOption("SSVCTZMALIM ", "Volume limit", Send, new[]
        {
            ("OFF", "None"), ("60", "60 (−20 dB)"), ("70", "70 (−10 dB)"), ("80", "80 (0 dB)"),
        }, query: "SSVCTZMA ?"));
        PowerOnLevel = AddSystem(new AvOption("SSVCTZMAPON ", "Power-on volume", Send, new[]
        {
            ("LAS", "Last"), ("MUT", "Muted"), ("40", "40"), ("50", "50"), ("60", "60"),
        }, query: "SSVCTZMA ?"));
        MuteLevel = AddSystem(new AvOption("SSVCTZMAMLV ", "Mute level", Send, new[]
        {
            ("MUT", "Full"), ("040", "−40 dB"), ("060", "−20 dB"),
        }, query: "SSVCTZMA ?"));
        EcoMode = AddSystem(new AvOption("ECO", "ECO mode", Send, new[]
        {
            ("ON", "On"), ("AUTO", "Auto"), ("OFF", "Off"),
        }, query: "ECO?"));

        // Csak kijelzés (a hero kártyán). Ismeretlen tokennél a nyers érték látszik.
        // A kódokat élőben mértük ki (2026-08-14, ugyanaz a film több jelúton):
        // 02 = PCM (sztereó ÉS többcsatornás LPCM is), 03 = Dolby Digital,
        // 06 = DTS mag, 09 = DTS-HD. A „01" és „18" jelentése ismeretlen –
        // azokat a HasKnownLabel elrejti, nem találgatunk.
        SignalFormat = _signalParams.Add(new AvOption("SSINFAISSIG ", "Incoming signal", Send, new[]
        {
            ("02", "PCM"), ("03", "Dolby Digital"), ("06", "DTS"), ("09", "DTS-HD"),
            ("18", "Network"),
        }));
        SampleRate = _signalParams.Add(new AvOption("SSINFAISFSV ", "Sample rate", Send, new[]
        {
            ("NON", ""), ("441", "44,1 kHz"), ("48K", "48 kHz"), ("48", "48 kHz"),
            ("882", "88,2 kHz"), ("96K", "96 kHz"), ("96", "96 kHz"),
            ("1764", "176,4 kHz"), ("192K", "192 kHz"), ("192", "192 kHz"),
        }));
        // A BEJÖVŐ csatorna-elrendezés: „3/2/.1" = 3 front + 2 surround + LFE.
        // Élőben igazolva, hogy NEM a kimenetet követi: STEREO hangzásmódban a
        // CV? már csak FL/FR/SW-t adott, ez viszont „3/2/.1" maradt.
        // A választéklista szándékosan üres: ez csak kijelzés, és a token alakja
        // („3/2/.1", „2/0/.0") túl sokféle ahhoz, hogy táblázatból keressük –
        // a feliratot a LayoutLabel számolja ki.
        SignalLayout = _signalParams.Add(new AvOption("SSINFAISFOR ", "Layout", Send,
            Array.Empty<(string, string)>()));

        // Az összefoglaló feliratokat és a függőségeket bármelyik érték változása frissíti.
        foreach (var item in _audioParams.Items.Concat(_systemParams.Items).Concat(_signalParams.Items))
            item.PropertyChanged += (_, _) => OnOptionChanged();

        AudysseyOptions.Add(MultEq);
        AudysseyOptions.Add(DynamicEq);
        AudysseyOptions.Add(ReferenceLevel);
        AudysseyOptions.Add(DynamicVolume);
        ToneOptions.Add(ToneControl);
        ToneOptions.Add(MDax);
        ToneLevels.Add(Bass);
        ToneLevels.Add(Treble);

        AvOption AddSurround(AvOption o) { _audioParams.Add(o); SurroundOptions.Add(o); return o; }
        AvLevel AddSurroundLevel(AvLevel l) { _audioParams.Add(l); SurroundLevels.Add(l); return l; }
        AvOption AddSpeaker(AvOption o) { _systemParams.Add(o); SpeakerOptions.Add(o); return o; }
        AvOption AddCrossover(AvOption o) { _systemParams.Add(o); CrossoverOptions.Add(o); return o; }
        AvOption AddSystem(AvOption o) { _systemParams.Add(o); SystemOptions.Add(o); return o; }
    }

    /// <summary>Bármelyik beállítás változásakor: függőségek + fejléc-összefoglaló.</summary>
    private void OnOptionChanged()
    {
        // Ref. szint eltolás csak Dynamic EQ mellett értelmes; a Dynamic Volume és
        // a Dynamic EQ pedig csak kalibrált (nem OFF) MultEQ mellett.
        bool multEqOn = MultEq.SelectedToken is not null and not "OFF";
        DynamicEq.IsEnabled = multEqOn;
        DynamicVolume.IsEnabled = multEqOn;
        ReferenceLevel.IsEnabled = multEqOn && DynamicEq.SelectedToken == "ON";

        // A hangszín-szabályzó csúszkái csak bekapcsolt Tone Control mellett élnek.
        bool tone = ToneControl.SelectedToken == "ON";
        Bass.IsEnabled = tone;
        Treble.IsEnabled = tone;

        // Egyedi keresztváltásnál a csoportonkénti értékek élnek, egységesnél az „ALL".
        bool individual = CrossoverMode.SelectedToken == "IDV";
        CrossoverAll.IsEnabled = !individual;
        CrossoverFront.IsEnabled = individual;
        CrossoverCenter.IsEnabled = individual;
        CrossoverSurround.IsEnabled = individual;

        OnPropertyChanged(nameof(AudioParamsShort));
        OnPropertyChanged(nameof(SignalInfo));
        OnPropertyChanged(nameof(HasSignalInfo));
    }

    /// <summary>3. sor: HANGKÉP + HANGSZÍN kártyák (alapból zárva).</summary>
    private bool _isAudioParamsExpanded;
    public bool IsAudioParamsExpanded
    {
        get => _isAudioParamsExpanded;
        set => Set(ref _isAudioParamsExpanded, value);
    }

    /// <summary>Rövid összefoglaló a „SOUND" kártya fülén (összecsukva is látszik).</summary>
    public string AudioParamsShort
    {
        get
        {
            var parts = new List<string>();
            if (MultEq.SelectedToken is { } m)
                parts.Add(m == "OFF" ? "MultEQ off" : MultEq.ValueLabel);
            if (DynamicEq.SelectedToken == "ON") parts.Add("DynEQ");
            if (DynamicVolume.SelectedToken is { } dv && dv != "OFF") parts.Add("DynVol " + DynamicVolume.ValueLabel);
            if (ToneControl.SelectedToken == "ON")
                parts.Add($"Bass {Bass.Text} / Treble {Treble.Text}");
            if (MDax.SelectedToken is { } md && md != "OFF") parts.Add("M-DAX");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Bejövő jel a hero kártyán, pl. „PCM · 48 kHz".</summary>
    public string SignalInfo
    {
        get
        {
            // A vevő jelformátum-kódja (SSINFAISSIG) csak részben ismert – az
            // ismeretlen kódot (pl. „18") inkább elhagyjuk, mint hogy zajt mutassunk.
            // Az elrendezésnél viszont a nyers érték is olvasható („3/2/.1"), ezért
            // ott a ValueLabel fallbackjét használjuk.
            var parts = new List<string>();
            if (SignalFormat.IsAvailable && SignalFormat.HasKnownLabel)
                parts.Add(SignalFormat.ValueLabel);
            if (SignalLayout.IsAvailable && LayoutLabel(SignalLayout.SelectedToken) is { } layout)
                parts.Add(layout);
            if (SampleRate.IsAvailable && SampleRate.HasKnownLabel && SampleRate.ValueLabel.Length > 0)
                parts.Add(SampleRate.ValueLabel);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// A vevő „front/surround/.lfe" alakú elrendezés-tokenjéből olvasható felirat:
    /// „3/2/.1" → „5.1", „2/0/.0" → „2.0". Ismeretlen alaknál a nyers tokent adja
    /// vissza – így sosem hazudik, legfeljebb nyers.
    /// </summary>
    private static string? LayoutLabel(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var p = token.Split('/');
        if (p.Length < 2 || !int.TryParse(p[0], out int front) || !int.TryParse(p[1], out int surround))
            return token;
        int lfe = p.Length > 2 && p[2].Contains('1') ? 1 : 0;
        return $"{front + surround}.{lfe}";
    }

    public bool HasSignalInfo => SignalInfo.Length > 0;

    // ---- A bejövő jel élő követése ----
    //
    // A vevő a jelformátumot NEM küldi magától (a `MS…`-t igen, azt a ParseLine
    // kapja el). Ahhoz, hogy hangsáv-váltáskor magától frissüljön a kijelzés,
    // rövid ciklusban le kell kérdezni. A lekérdezés „csendes": nem nullázza az
    // auto-standby órát, és a válaszait a OnLineReceived sem számolja aktivitásnak.

    private System.Windows.Threading.DispatcherTimer? _signalTimer;
    private bool _signalQueryRunning;

    private void StartSignalPolling()
    {
        if (_signalTimer is not null)
            return;
        _signalTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _signalTimer.Tick += (_, _) => _ = PollSignalInfoAsync();
        _signalTimer.Start();
    }

    private async Task PollSignalInfoAsync()
    {
        // Egyszerre csak egy kör fusson, és csak ha van értelme.
        if (_signalQueryRunning || !IsConnected || !IsPoweredOn)
            return;
        _signalQueryRunning = true;
        try
        {
            _signalParams.MarkAllPending();
            foreach (var q in _signalParams.Queries)
            {
                await SendQuietAsync(q);
                await Task.Delay(60);
            }
            // Ami nem válaszol (pl. nincs bejövő jel), az eltűnik a kijelzésből.
            await Task.Delay(900);
            _signalParams.MarkPendingUnavailable();
        }
        finally
        {
            _signalQueryRunning = false;
        }
    }

    // ---- Lekérdezés és frissítés ----

    private int _audioParamGeneration;

    /// <summary>
    /// A forrásonként tárolt (PS…) beállítások újraolvasása. Forrás- vagy
    /// hangzásmód-váltás után kötelező: a vevő ezeket bemenetenként külön tárolja.
    /// Két körben kérdez: lejátszás közben az adatözönben elveszhet egy válasz,
    /// és csak a második néma kör után jelöljük „nem elérhető"-nek.
    /// </summary>
    private async Task RefreshAudioParamsAsync()
    {
        int generation = ++_audioParamGeneration;
        _audioParams.MarkAllPending();

        foreach (var q in _audioParams.Queries)
        {
            if (generation != _audioParamGeneration) return;
            await SendRawAsync(q);
            await Task.Delay(60);
        }

        await Task.Delay(1200);
        if (generation != _audioParamGeneration) return;

        // Második kör csak a némán maradtakra.
        foreach (var q in _audioParams.PendingQueries.ToList())
        {
            if (generation != _audioParamGeneration) return;
            await SendRawAsync(q);
            await Task.Delay(60);
        }

        await Task.Delay(1200);
        if (generation != _audioParamGeneration) return;
        _audioParams.MarkPendingUnavailable();
    }

    /// <summary>A globális (SS…, ECO) beállítások olvasása – csatlakozáskor és preset-váltáskor.</summary>
    private async Task RefreshSystemParamsAsync()
    {
        _systemParams.MarkAllPending();
        foreach (var q in _systemParams.Queries)
        {
            await SendRawAsync(q);
            await Task.Delay(70);
        }
        await Task.Delay(1500);
        foreach (var q in _systemParams.PendingQueries.ToList())
        {
            await SendRawAsync(q);
            await Task.Delay(70);
        }
        await Task.Delay(1500);
        _systemParams.MarkPendingUnavailable();
    }

    // Forrás/hangzásmód-váltás után nem azonnal kérdezünk: a vevőnek kell pár
    // száz ms, és váltás közben több visszajelzés is jön egymás után.
    private CancellationTokenSource? _audioParamDebounce;

    private void ScheduleAudioParamRefresh()
    {
        _audioParamDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _audioParamDebounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                if (!cts.IsCancellationRequested)
                    await RefreshAudioParamsAsync();
            }
            catch (TaskCanceledException) { /* újabb váltás jött – rendben */ }
        });
    }

    /// <summary>A Beállítások ablak megnyitása (nem modális, ugyanaz a DataContext).</summary>
    public ICommand OpenSettingsCommand => _openSettings ??= new RelayCommand(OpenSettings);
    private RelayCommand? _openSettings;

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        try
        {
            _settingsWindow = new SettingsWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
                DataContext = this,
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        catch (Exception ex)
        {
            // Ne vigye magával az egész appot, ha az ablak XAML-je hibás.
            _settingsWindow = null;
            StatusMessage = "Settings window error: " + FirstLine(ex.Message);
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        // Nyitáskor friss adat – a felhasználó közben állíthatott a telefonról.
        if (IsConnected)
            _ = RefreshSystemParamsAsync();
    }
}
