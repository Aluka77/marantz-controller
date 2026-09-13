using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MarantzController;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly MarantzConnection _connection = new();
    private readonly HeosConnection _heos = new();

    public MainViewModel()
    {
        _ipAddress = _settings.IpAddress;
        if (_ipAddress.Length == 0)
            _statusMessage = "Enter the receiver's IP address, then Connect.";

        _connection.LineReceived += OnLineReceived;
        _connection.ConnectionStateChanged += OnConnectionStateChanged;
        _heos.NowPlayingChanged += OnNowPlayingChanged;
        _heos.PlayStateChanged += OnPlayStateChanged;
        _heos.ProgressChanged += OnProgressChanged;

        ConnectCommand = new RelayCommand(ToggleConnectionAsync);
        PowerOnCommand = new RelayCommand(() => SendAsync("PWON"));
        PowerOffCommand = new RelayCommand(() => SendAsync("PWSTANDBY"));
        MuteToggleCommand = new RelayCommand(() => SendAsync(IsMuted ? "MUOFF" : "MUON"));
        VolumeUpCommand = new RelayCommand(VolumeStepUpAsync);
        VolumeDownCommand = new RelayCommand(VolumeStepDownAsync);
        SelectSourceCommand = new RelayCommand<string>(SelectSourceAsync);
        // Marantznál „Smart Select" → a parancs MSSMART (nem MSQUICK, az a Denoné).
        QuickSelectCommand = new RelayCommand<string>(RecallQuickSelect);
        SelectSoundModeCommand = new RelayCommand<string>(mode => SendAsync("MS" + mode));

        // HEOS/hálózati lejátszásvezérlés (Spotify Connect stb.). A telnet NS9x NEM
        // hat a HEOS forrásra – a HEOS CLI-n (1255) megy. Lásd [[marantz-protocol-findings]].
        // Egy gomb indít/megállít: a HEOS-tól kapott állapot dönti el, melyik jön.
        PlayPauseCommand = new RelayCommand(() => _ = IsPlaying ? _heos.PauseAsync() : _heos.PlayAsync());
        StopCommand = new RelayCommand(() => _ = _heos.StopAsync());
        NextTrackCommand = new RelayCommand(() => _ = _heos.NextAsync());
        PrevTrackCommand = new RelayCommand(() => _ = _heos.PreviousAsync());

        // A gombok alaplistája a 6. pont szerinti tokenekkel és alapértelmezett
        // feliratokkal. Csatlakozáskor az SSFUN ? válasza felülírja a Name-eket.
        Sources = new ObservableCollection<SourceItem>
        {
            new("TV",      "TV Audio"),
            new("SAT/CBL", "CBL/SAT"),
            new("BD",      "Blu-ray"),
            new("GAME",    "Game"),
            new("MPLAY",   "Media Player"),
            new("NET",     "Online Music"),
            new("TUNER",   "Tuner"),
            new("CD",      "CD"),
        };

        QuickSelects = new ObservableCollection<QuickSelectItem>
        {
            new("1", "QS 1", RecallQuickSelect),
            new("2", "QS 2", RecallQuickSelect),
            new("3", "QS 3", RecallQuickSelect),
            new("4", "QS 4", RecallQuickSelect),
        };

        // Audyssey / hangszín / mélyláda / surround / hangszóró / rendszer beállítások.
        BuildOptionCatalog();
        OnOptionChanged();

        // HEOS keverés / ismétlés / lejátszási sor.
        HookHeosExtras();
    }

    /// <summary>Forrásgombok (token + élőben frissülő felirat). Csatlakozáskor
    /// az SSFUN ? válasza újraépíti a tényleges bemenetlistával.</summary>
    public ObservableCollection<SourceItem> Sources { get; }

    /// <summary>Csatornaszintek (SSLEV…, a presetben tárolt kalibrált szintek – NEM a
    /// forrásonkénti CV-trim). A vevő SSLEV ? válaszából épül fel, a tényleg jelen lévő
    /// hangszórókkal. 50 = 0 dB, tartomány jellemzően 38..62 (±12 dB).</summary>
    public ObservableCollection<ChannelLevel> ChannelLevels { get; } = new();

    /// <summary>Quick Select gombok élő felirattal. A nevek a SSQSNZMA ? válaszából
    /// frissülnek (ha a felhasználó átnevezte őket az erősítőn).</summary>
    /// <summary>Quick Select gombok élő felirattal. A gomb parancsát MAGA az elem
    /// hordozza – a stílusban lévő `RelativeSource AncestorType=Window` kötés
    /// összecsukott kártyában létrejövő gombnál némán üresen maradt, és a gomb
    /// egyszerűen nem csinált semmit.</summary>
    public ObservableCollection<QuickSelectItem> QuickSelects { get; }

    private void RecallQuickSelect(string? number)
    {
        if (!string.IsNullOrEmpty(number))
            _ = SendAsync("MSSMART" + number);
    }

    // true, amíg az SSFUN ? első válaszsorára várunk, hogy a régi listát lecseréljük.
    private bool _sourceListPending;

    // ---- Napló (nyers TX/RX forgalom diagnosztikához) ----

    private const int MaxLogLines = 400;

    /// <summary>Legutóbbi sor elöl. „» " = küldött, „« " = fogadott.</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    public ICommand ClearLogCommand => _clearLogCommand ??= new RelayCommand(() => LogLines.Clear());
    private RelayCommand? _clearLogCommand;

    private void AddLog(string entry)
    {
        // A napló UI-t eltávolítottuk. A korábbi, UI-szálon futó Insert(0) egy
        // 400 elemű listába minden beérkező sornál (lejátszáskor adatözön!)
        // érezhetően lassította a hangerő-csúszka húzását, ezért már nem gyűjtünk.
    }

    // ---- Kapcsolódási állapot ----

    private readonly AppSettings _settings = AppSettings.Load();

    // Az utoljára sikeresen használt cím (%APPDATA%\MarantzController\settings.json);
    // a konstruktor tölti be.
    private string _ipAddress = "";
    public string IpAddress
    {
        get => _ipAddress;
        set => Set(ref _ipAddress, value);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ConnectButtonText));
                OnPropertyChanged(nameof(IsStandbyCountdownVisible));
                ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
                _findReceivers?.RaiseCanExecuteChanged();
            }
        }
    }

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";

    // ---- Ablak-állapotok (mindig felül, kompakt nézet) ----

    private bool _isAlwaysOnTop;
    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set => Set(ref _isAlwaysOnTop, value);
    }

    public ICommand ToggleAlwaysOnTopCommand =>
        _toggleAlwaysOnTopCommand ??= new RelayCommand(() => IsAlwaysOnTop = !IsAlwaysOnTop);
    private RelayCommand? _toggleAlwaysOnTopCommand;

    private bool _isCompact;
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (Set(ref _isCompact, value))
            {
                OnPropertyChanged(nameof(CompactButtonText));
                OnPropertyChanged(nameof(CompactButtonShort));
            }
        }
    }

    public string CompactButtonText => IsCompact ? "Full view" : "Mini view";

    /// <summary>Rövid felirat a mini nézet beépített kapcsolójához.</summary>
    public string CompactButtonShort => IsCompact ? "Full" : "Mini";

    public ICommand ToggleCompactCommand =>
        _toggleCompactCommand ??= new RelayCommand(() => IsCompact = !IsCompact);
    private RelayCommand? _toggleCompactCommand;

    // ---- Összecsukható szekciók (teljes nézet) ----
    // A fejlécek ToggleButtonok, kétirányban ezekre kötve – nem kell külön parancs.
    // Soronként EGY property: a két egymás melletti kártya együtt nyílik és záródik,
    // így a sor magassága nem lóg ki egyik oldalon sem.

    /// <summary>1. sor: SOUND MODES + INPUT SOURCES (alapból nyitva – ezeket
    /// használjuk naponta).</summary>
    private bool _isSoundAndInputExpanded = true;
    public bool IsSoundAndInputExpanded
    {
        get => _isSoundAndInputExpanded;
        set => Set(ref _isSoundAndInputExpanded, value);
    }

    /// <summary>2. sor: POWER &amp; QUICK SELECT + TIMERS (alapból zárva).</summary>
    private bool _isPowerAndTimersExpanded;
    public bool IsPowerAndTimersExpanded
    {
        get => _isPowerAndTimersExpanded;
        set => Set(ref _isPowerAndTimersExpanded, value);
    }

    private bool _isChannelLevelsExpanded;
    public bool IsChannelLevelsExpanded
    {
        get => _isChannelLevelsExpanded;
        set => Set(ref _isChannelLevelsExpanded, value);
    }

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await _heos.DisconnectAsync();
            await _connection.DisconnectAsync();
        }
        else
            await ConnectAsync();
    }

    private string _statusMessage = "Not connected";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string StatusText => IsConnected ? "Connected" : StatusMessage;

    // ---- Erősítő állapot (UI bindingek) ----

    private bool _isPoweredOn;
    public bool IsPoweredOn
    {
        get => _isPoweredOn;
        private set
        {
            if (Set(ref _isPoweredOn, value))
            {
                OnPropertyChanged(nameof(PowerMuteText));
                OnPropertyChanged(nameof(IsStandbyCountdownVisible));
                ResetStandbyCountdown();
            }
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        private set { if (Set(ref _isMuted, value)) OnPropertyChanged(nameof(PowerMuteText)); }
    }

    private double? _volumeRaw;   // numerikus MV érték (pl. 56.5)
    public double? VolumeRaw
    {
        get => _volumeRaw;
        private set
        {
            if (Set(ref _volumeRaw, value))
            {
                OnPropertyChanged(nameof(VolumeDbText));
                // A slidert a vevő által visszaigazolt értékre állítjuk, de úgy, hogy
                // az ne küldjön újabb parancsot (visszacsatolás-védelem). Amíg a
                // felhasználó épp mozgatja a csúszkát (vagy az utolsó mozdulat óta még
                // nem telt el ~0.6 mp), NEM nyúlunk hozzá – így a vevő 0.5-ös
                // visszaigazolásai nem rángatják, és sima marad a húzás.
                bool recentlyUserDriven =
                    Environment.TickCount64 - _lastUserVolumeTick < VolumeSyncSuppressMs;
                if (!_isVolumeDragging && !recentlyUserDriven)
                {
                    // A vevő az igazság: elengedjük a felhasználó ideiglenes célértékét.
                    _userVolumeTarget = null;
                    _applyingIncomingVolume = true;
                    OnPropertyChanged(nameof(VolumeSliderValue));
                    _applyingIncomingVolume = false;
                }
                else if (_userVolumeTarget is { } t && value is { } v && Math.Abs(t - v) < 0.25)
                {
                    // A vevő visszaigazolta, amit kértünk – nincs többé szükség a célértékre.
                    _userVolumeTarget = null;
                }
            }
        }
    }

    private double? _volumeMax;   // MVMAX numerikus érték (külön tárolva, nem rontja a kijelzést)
    public double? VolumeMax
    {
        get => _volumeMax;
        private set { if (Set(ref _volumeMax, value)) OnPropertyChanged(nameof(VolumeSliderMax)); }
    }

    // ---- Hangerő csúszka ----
    // A csúszka 0..MVMAX tartományban, 0.5-ös lépésekben mozog. Húzáskor a
    // beérkező MV-visszaigazolásokat elnyomjuk (_applyingIncomingVolume), a
    // kifelé menő parancsokat pedig egy ~60 ms-os időzítő fésüli össze, hogy
    // gyors húzásnál ne floodoljuk a vevőt.

    private bool _applyingIncomingVolume;
    private bool _isVolumeDragging;
    private long _lastUserVolumeTick;        // mikor nyúlt a felhasználó utoljára a csúszkához
    private const long VolumeSyncSuppressMs = 600;
    private double? _pendingVolumeTarget;   // a legutóbb beállított cél, amit még el kell küldeni
    private double? _userVolumeTarget;      // amit a felhasználó beállított, még visszaigazolás előtt
    private double? _lastSentVolume;        // a legutóbb ténylegesen elküldött érték
    private System.Windows.Threading.DispatcherTimer? _volumeThrottle;

    /// <summary>A nézet jelzi, amikor a felhasználó elkezdi húzni a hangerő-csúszkát.</summary>
    public void BeginVolumeDrag() => _isVolumeDragging = true;

    /// <summary>A húzás vége: elküldjük a végső értéket. A csúszka NEM ugrik vissza –
    /// a beérkező értékekhez az idő-ablak (~0.6 mp) lejárta után simán igazodik.</summary>
    public void EndVolumeDrag()
    {
        _isVolumeDragging = false;
        _lastUserVolumeTick = Environment.TickCount64; // az elnyomó ablak még tartson a felengedés után is
        FlushPendingVolume();
    }

    public double VolumeSliderMax => VolumeMax ?? 98;

    /// <summary>
    /// A csúszka/knob értéke. FONTOS: a getter a felhasználó által épp beállított
    /// értéket adja vissza, amíg a vevő vissza nem igazolja (<see cref="_userVolumeTarget"/>).
    ///
    /// Korábban a getter mindig a vevő <see cref="VolumeRaw"/> értékét adta, a setter
    /// viszont csak elküldte a parancsot – így ha a binding a visszaigazolás előtt
    /// újraolvasta a property-t, a csúszka VISSZAUGROTT az előző értékre (mérve:
    /// vevő 9.5, csúszka 25.5), és csak a következő kattintásra „érte utol" magát.
    /// </summary>
    public double VolumeSliderValue
    {
        get => _userVolumeTarget ?? VolumeRaw ?? 0;
        set
        {
            // A vevő által kiváltott frissítés nem küld vissza parancsot.
            if (_applyingIncomingVolume)
                return;

            // Felhasználói mozgatás → jelöljük az időt (ez nyomja el a beérkező
            // visszaigazolások csúszkára hatását, amíg aktívan mozgat).
            _lastUserVolumeTick = Environment.TickCount64;

            double target = Math.Clamp(Math.Round(value * 2) / 2, 0, VolumeSliderMax);

            // A felhasználó szándéka azonnal érvényes a kijelzésre, még a
            // visszaigazolás előtt – így a fogantyú ott marad, ahová kattintott.
            _userVolumeTarget = target;

            // Ha gyakorlatilag ugyanott vagyunk, nincs mit küldeni.
            if (VolumeRaw is { } cur && Math.Abs(cur - target) < 0.25)
                return;

            _pendingVolumeTarget = target;
            EnsureVolumeThrottle();
        }
    }

    private void EnsureVolumeThrottle()
    {
        if (_volumeThrottle is null)
        {
            _volumeThrottle = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _volumeThrottle.Tick += (_, _) => FlushPendingVolume();
        }
        if (!_volumeThrottle.IsEnabled)
        {
            _volumeThrottle.Start();
            FlushPendingVolume(); // az első lépést azonnal küldjük, hogy reszponzív legyen
        }
    }

    private async void FlushPendingVolume()
    {
        if (_pendingVolumeTarget is not { } target)
        {
            _volumeThrottle?.Stop();
            return;
        }
        if (_lastSentVolume is { } last && Math.Abs(last - target) < 0.25)
        {
            _pendingVolumeTarget = null;
            return;
        }

        _lastSentVolume = target;
        _pendingVolumeTarget = null;
        await SendAsync("MV" + EncodeHalfStep(target));
    }

    /// <summary>Hangerő/szint numerikus érték → MV/CV token (37.0→"37", 37.5→"375").</summary>
    private static string EncodeHalfStep(double v)
    {
        int whole = (int)Math.Floor(v + 0.001);
        bool half = (v - whole) >= 0.25;
        return half ? $"{whole:00}5" : $"{whole:00}";
    }

    private string _currentSource = "—";
    public string CurrentSource
    {
        get => _currentSource;
        private set => Set(ref _currentSource, value);
    }

    private string _soundMode = "";   // MS – az aktuális hangzásmód nyers tokenje
    public string SoundMode
    {
        get => _soundMode;
        private set
        {
            if (Set(ref _soundMode, value))
            {
                OnPropertyChanged(nameof(SoundModeDisplay));
                RefreshSoundModeHighlight();
            }
        }
    }

    public string SoundModeDisplay =>
        string.IsNullOrEmpty(SoundMode) ? "Sound mode: —" : "Sound mode: " + SoundMode;

    // ---- Elalváskapcsoló (Sleep) ----
    // A vevő SLP? -> "SLPOFF" vagy "SLP060" (= hátralévő PERC). Másodperces
    // visszaszámlálást a vevő nem küld, ezért helyben, DispatcherTimer-rel
    // számolunk le, és minden beérkező SLP üzenetre újraszinkronizálunk.

    private int _sleepSecondsRemaining;   // 0 = kikapcsolt
    private System.Windows.Threading.DispatcherTimer? _sleepTimer;

    public bool IsSleepActive => _sleepSecondsRemaining > 0;

    public string SleepDisplay
    {
        get
        {
            if (_sleepSecondsRemaining <= 0)
                return "Sleep: off";
            int m = _sleepSecondsRemaining / 60;
            int s = _sleepSecondsRemaining % 60;
            return $"Sleep: {m}:{s:00}";
        }
    }

    private void ApplySleepFromReceiver(int minutes)
    {
        // A vevő percre kerekítve adja meg a hátralévő időt. A helyi
        // visszaszámlálót erre állítjuk (a percen belüli pozíciót nem ismerjük,
        // ezért a teljes percről indulunk – ez a vevő kijelzőjével egyezik).
        _sleepSecondsRemaining = Math.Max(0, minutes) * 60;
        OnPropertyChanged(nameof(SleepDisplay));
        OnPropertyChanged(nameof(TimersShort));
        OnPropertyChanged(nameof(IsSleepActive));

        if (_sleepSecondsRemaining > 0)
        {
            _sleepTimer ??= CreateSleepTimer();
            if (!_sleepTimer.IsEnabled) _sleepTimer.Start();
        }
        else
        {
            _sleepTimer?.Stop();
        }
    }

    private System.Windows.Threading.DispatcherTimer CreateSleepTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (_sleepSecondsRemaining > 0)
                _sleepSecondsRemaining--;
            OnPropertyChanged(nameof(SleepDisplay));
            OnPropertyChanged(nameof(TimersShort));
            OnPropertyChanged(nameof(IsSleepActive));
            if (_sleepSecondsRemaining <= 0)
                t.Stop();
        };
        return t;
    }

    /// <summary>Elalváskapcsoló beállítása. 0 = ki, különben a megadott perc.</summary>
    public ICommand SetSleepCommand =>
        _setSleepCommand ??= new RelayCommand<string>(async arg =>
        {
            if (!int.TryParse(arg, out int min)) return;
            await SendAsync(min <= 0 ? "SLPOFF" : "SLP" + min.ToString("000"));
            await SendRawAsync("SLP?"); // azonnali visszaszinkronizálás
        });
    private RelayCommand<string>? _setSleepCommand;

    // ---- Automatikus készenlét (Auto Standby, Eco menü) ----
    // STBY? -> "STBYOFF" / "STBY15M" / "STBY30M" / "STBY60M". A vevő ennyi
    // INAKTIVITÁS után magától készenlétbe megy – ettől „kapcsol ki" magától.

    private string _autoStandby = "";
    public string AutoStandby
    {
        get => _autoStandby;
        private set
        {
            if (Set(ref _autoStandby, value))
            {
                OnPropertyChanged(nameof(AutoStandbyDisplay));
                OnPropertyChanged(nameof(TimersShort));
                OnPropertyChanged(nameof(IsStandbyCountdownVisible));
                ResetStandbyCountdown();
            }
        }
    }

    public string AutoStandbyDisplay =>
        _autoStandby switch
        {
            "OFF" => "Auto standby: off",
            "" => "Auto standby: —",
            _ => "Auto standby: " + _autoStandby.TrimEnd('M') + " perc"
        };

    /// <summary>Rövid, egysoros időzítő-összegzés a kártyafülhöz (ott kevés a hely).</summary>
    public string TimersShort
    {
        get
        {
            string sleep = _sleepSecondsRemaining <= 0
                ? "off"
                : $"{_sleepSecondsRemaining / 60}:{_sleepSecondsRemaining % 60:00}";
            string standby = _autoStandby switch
            {
                "OFF" => "off",
                "" => "—",
                _ => _autoStandby.TrimEnd('M') + "m"
            };
            return $"Sleep {sleep} · Auto {standby}";
        }
    }

    // A vevő nem küldi a hátralévő inaktivitási időt, ezért helyben becsüljük:
    // a beállított perctől számolunk vissza, és MINDEN elküldött parancsra
    // (= felhasználói művelet) visszaállítjuk a teljes időre – ahogy a vevő is
    // nullázza az auto-standby időzítőjét bármilyen kezelésre.
    private int _standbyMinutes;            // beállított perc (0 = OFF)
    private int _standbySecondsRemaining;
    private System.Windows.Threading.DispatcherTimer? _standbyTimer;

    // Csak akkor mutatjuk, ha tényleg „van mit visszaszámolni": a vevő egy ideje
    // (>=20 mp) nem küldött semmit ÉS nem volt felhasználói művelet. Lejátszás
    // közben a vevő folyamatosan küld státuszt → folyton nullázódik → rejtve marad.
    private const int StandbyShowAfterIdleSeconds = 20;

    public bool IsStandbyCountdownVisible =>
        IsConnected && IsPoweredOn && _standbyMinutes > 0
        && _standbySecondsRemaining <= _standbyMinutes * 60 - StandbyShowAfterIdleSeconds;

    /// <summary>Bármilyen aktivitás (TX vagy RX) → az inaktivitási órát nullázzuk.</summary>
    private void NoteActivity()
    {
        if (_standbyMinutes <= 0)
            return;
        // Olcsó útvonal: ez minden beérkező sorra meghívódik (lejátszáskor sűrűn).
        // Reset után a visszaszámláló a teljes időre ugrik → rejtve marad; csak
        // akkor frissítünk kötést, ha eddig LÁTSZOTT (különben fölösleges UI-munka).
        bool wasVisible = IsStandbyCountdownVisible;
        _standbySecondsRemaining = _standbyMinutes * 60;
        if (wasVisible)
        {
            OnPropertyChanged(nameof(StandbyCountdownDisplay));
            OnPropertyChanged(nameof(IsStandbyCountdownVisible));
        }
    }

    public string StandbyCountdownDisplay
    {
        get
        {
            if (!IsStandbyCountdownVisible) return "";
            int m = _standbySecondsRemaining / 60;
            int s = _standbySecondsRemaining % 60;
            return $"standby in ~ {m}:{s:00}";
        }
    }

    private void ResetStandbyCountdown()
    {
        _standbyMinutes = _autoStandby switch
        {
            "15M" => 15,
            "30M" => 30,
            "60M" => 60,
            _ => 0
        };
        _standbySecondsRemaining = _standbyMinutes * 60;

        if (_standbyMinutes > 0 && IsPoweredOn && IsConnected)
        {
            _standbyTimer ??= CreateStandbyTimer();
            if (!_standbyTimer.IsEnabled) _standbyTimer.Start();
        }
        else
        {
            _standbyTimer?.Stop();
        }
        OnPropertyChanged(nameof(IsStandbyCountdownVisible));
        OnPropertyChanged(nameof(StandbyCountdownDisplay));
    }

    private System.Windows.Threading.DispatcherTimer CreateStandbyTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (_standbySecondsRemaining > 0)
                _standbySecondsRemaining--;
            OnPropertyChanged(nameof(StandbyCountdownDisplay));
            if (_standbySecondsRemaining <= 0)
                t.Stop();
        };
        return t;
    }

    /// <summary>Auto-standby beállítása. "OFF" vagy perc ("15"/"30"/"60").</summary>
    public ICommand SetStandbyCommand =>
        _setStandbyCommand ??= new RelayCommand<string>(async arg =>
        {
            if (string.IsNullOrEmpty(arg)) return;
            await SendAsync(arg == "OFF" ? "STBYOFF" : "STBY" + arg + "M");
            await SendRawAsync("STBY?");
        });
    private RelayCommand<string>? _setStandbyCommand;

    // Hangerő-kijelzés módja. true = abszolút dB (érték − 80, pl. −29.0 dB),
    // false = relatív skála, ahogy az erősítő kijelzője mutatja (pl. 51.0).
    // Alapértelmezés: a vevő relatív skálája.
    private bool _showAbsoluteDb = false;
    public bool ShowAbsoluteDb
    {
        get => _showAbsoluteDb;
        set
        {
            if (Set(ref _showAbsoluteDb, value))
            {
                OnPropertyChanged(nameof(VolumeDbText));
                OnPropertyChanged(nameof(VolumeScaleLabel));
            }
        }
    }

    /// <summary>A hangerő-kijelző felirata: dB-ben vagy relatív skálán.</summary>
    public string VolumeScaleLabel => ShowAbsoluteDb ? "dB" : "scale";

    public ICommand ToggleVolumeScaleCommand =>
        _toggleVolumeScaleCommand ??= new RelayCommand(() => ShowAbsoluteDb = !ShowAbsoluteDb);
    private RelayCommand? _toggleVolumeScaleCommand;

    public string VolumeDbText
    {
        get
        {
            if (VolumeRaw is not { } v)
                return ShowAbsoluteDb ? "— dB" : "—";
            return ShowAbsoluteDb
                ? (v - 80).ToString("0.0", CultureInfo.InvariantCulture) + " dB"
                : v.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    public string PowerMuteText =>
        $"Power: {(IsPoweredOn ? "ON" : "STANDBY")} · Mute: {(IsMuted ? "on" : "off")}";

    private string _currentSourceToken = "";
    private string _currentQuickSelect = "";

    /// <summary>Az aktív Smart/Quick Select sorszáma („1".."4"), a vevő MSSMART
    /// visszajelzéséből. Megjegyzés: a vevő a LEGUTÓBB felidézett Smart Selectet
    /// jelenti – ha utána kézzel forrást váltasz, ez akkor is az marad.</summary>
    public string CurrentQuickSelect
    {
        get => _currentQuickSelect;
        private set
        {
            if (!Set(ref _currentQuickSelect, value)) return;
            // Az aktív kiemelést az elem maga hordozza (nem ablak-ős kötés).
            foreach (var qs in QuickSelects)
                qs.IsActive = string.Equals(qs.Number, value, StringComparison.Ordinal);
        }
    }

    public string CurrentSourceToken
    {
        get => _currentSourceToken;
        private set
        {
            if (Set(ref _currentSourceToken, value))
            {
                // A HEOS player akkor is jelenti a most szólót, ha a vevő bemenete
                // közben másra váltott – ezért a now-playing/transport/pozíció
                // láthatóságát a FORRÁSHOZ kötjük.
                OnPropertyChanged(nameof(IsHeosSource));
                OnPropertyChanged(nameof(IsNowPlayingVisible));
                OnPropertyChanged(nameof(HasAlbumArt));
                OnPropertyChanged(nameof(HasProgress));
            }
        }
    }

    /// <summary>true, ha a vevő aktuális bemenete a hálózati/HEOS forrás (NET).
    /// Csak ilyenkor van értelme a now-playing infónak és a transport gomboknak.</summary>
    public bool IsHeosSource => string.Equals(_currentSourceToken, "NET", StringComparison.Ordinal);

    // ---- Parancsok ----

    public ICommand ConnectCommand { get; }
    public ICommand PowerOnCommand { get; }
    public ICommand PowerOffCommand { get; }
    public ICommand MuteToggleCommand { get; }
    public ICommand VolumeUpCommand { get; }
    public ICommand VolumeDownCommand { get; }
    public ICommand SelectSourceCommand { get; }
    public ICommand QuickSelectCommand { get; }
    public ICommand SelectSoundModeCommand { get; }

    // HEOS/hálózati transport (Play-Pause összevonva/Stop/Köv/Előző).
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NextTrackCommand { get; }
    public ICommand PrevTrackCommand { get; }

    /// <summary>Kategória-gyorsgombok: mindegyik az adott kategóriához LEGUTÓBB
    /// használt surround módot idézi fel (az erősítő ezt jegyzi meg) – ezért nem
    /// jelenik meg egyszerre az összes almód.</summary>
    public IReadOnlyList<SoundModeItem> SoundModeCategories { get; } = new[]
    {
        new SoundModeItem("MOVIE", "Movie"),
        new SoundModeItem("MUSIC", "Music"),
        new SoundModeItem("GAME", "Game"),
        new SoundModeItem("PURE DIRECT", "Pure Direct", matchExact: "PURE DIRECT"),
    };

    /// <summary>Közvetlen surround módok – a forrás/jel függvényében nem mind
    /// választható, de amelyik nem, arra a vevő egyszerűen nem vált. Az aktívat
    /// az MS? válaszhoz illesztve emeljük ki.</summary>
    public IReadOnlyList<SoundModeItem> SoundModeDirect { get; } = new[]
    {
        new SoundModeItem("STEREO", "Stereo", matchExact: "STEREO"),
        new SoundModeItem("DOLBY AUDIO-DSUR", "Dolby Surround", matchContains: "DSUR"),
        new SoundModeItem("DTS NEURAL:X", "DTS Neural:X", matchContains: "NEURAL"),
        new SoundModeItem("DTS VIRTUAL:X", "DTS Virtual:X", matchExact: "VIRTUAL:X"),
        new SoundModeItem("MCH STEREO", "Multi Ch", matchExact: "MCH STEREO"),
        new SoundModeItem("VIRTUAL", "Virtual", matchExact: "VIRTUAL"),
        new SoundModeItem("AUTO", "Auto", matchExact: "AUTO"),
        new SoundModeItem("DIRECT", "Direct", matchExact: "DIRECT"),
    };

    /// <summary>Az MS? token alapján beállítja, melyik közvetlen mód aktív.</summary>
    private void RefreshSoundModeHighlight()
    {
        foreach (var m in SoundModeDirect)
            m.IsActive = m.Matches(SoundMode);
        foreach (var m in SoundModeCategories)
            m.IsActive = m.Matches(SoundMode);
    }

    // A Vol −/+ gombok páros relatív értékre lépnek (+2/−2). Az első nyomás a
    // következő páros számra igazít, utána már ±2-vel lépked – így hasznosabbak,
    // mint a 0.5-ös finomlépés (arra ott a csúszka).
    private async Task VolumeStepUpAsync()
    {
        if (VolumeRaw is not { } v) { await SendAsync("MVUP"); return; }
        double target = Math.Clamp(2 * Math.Floor(v / 2) + 2, 0, VolumeSliderMax);
        await SetVolumeAbsoluteAsync(target);
    }

    private async Task VolumeStepDownAsync()
    {
        if (VolumeRaw is not { } v) { await SendAsync("MVDOWN"); return; }
        double target = Math.Clamp(2 * Math.Ceiling(v / 2) - 2, 0, VolumeSliderMax);
        await SetVolumeAbsoluteAsync(target);
    }

    private async Task SetVolumeAbsoluteAsync(double v)
    {
        _lastSentVolume = v; // a csúszka throttle ne küldje el még egyszer
        await SendAsync("MV" + EncodeHalfStep(v));
    }

    private async void SelectSourceAsync(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        await SendAsync("SI" + token);
        // A választható hangzásmódok forrásfüggők → újra lekérjük az aktuálisat.
        await Task.Delay(250);
        await SendRawAsync("MS?");
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(IpAddress))
        {
            StatusMessage = "Enter the receiver's IP address first.";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        StatusMessage = "Connecting…";
        try
        {
            await _connection.ConnectAsync(IpAddress.Trim());

            // Csak a működő címet jegyezzük meg, az elgépeltet nem.
            if (_settings.IpAddress != IpAddress.Trim())
            {
                _settings.IpAddress = IpAddress.Trim();
                _settings.Save();
            }

            await QueryInitialStateAsync();

            // HEOS CLI (1255) – best-effort: ha nem megy, a többi vezérlés ettől még él.
            try { await _heos.ConnectAsync(IpAddress.Trim()); }
            catch { /* HEOS opcionális; a transport/now-playing marad üres */ }
        }
        catch (Exception ex)
        {
            // Rövid, emberi üzenet – a nyers kivétel szövege több soros és kitakarta
            // a címsort. A teljes hibaüzenet a státusz tooltipjében marad meg.
            StatusMessage = ex is System.Net.Sockets.SocketException or TimeoutException
                ? "No response — the receiver is probably in standby."
                : $"Could not connect: {FirstLine(ex.Message)}";
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>A kivétel-üzenet első mondata/sora (a .NET socket-hibák több sorosak).</summary>
    private static string FirstLine(string message)
    {
        int cut = message.IndexOfAny(new[] { '\r', '\n' });
        var s = (cut > 0 ? message.Substring(0, cut) : message).Trim();
        return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
    }

    /// <summary>
    /// Indítási szekvencia: a teljes alapállapot egyszeri lekérdezése. A válaszok
    /// aszinkron, a LineReceived-en jönnek vissza és a normál parse-úton frissítik a UI-t.
    /// </summary>
    private async Task QueryInitialStateAsync()
    {
        // Az SSFUN ? válaszából újraépítjük a forrásgomb-listát (a tényleg
        // engedélyezett bemenetek, a felhasználó saját neveivel).
        _sourceListPending = true;

        // Friss olvasás minden csatlakozáskor (ne maradjon korábbi preset csatornája).
        ChannelLevels.Clear();
        _speakerGroupPresent.Clear();
        _receivedLevels.Clear();

        // SSSPC ? legelöl, hogy a hangszóró-jelenlét ismert legyen, mire az SSLEV jön.
        foreach (var query in new[] { "SPPR ?", "SSSPC ?", "SSFUN ?", "SSQSNZMA ?", "PW?", "MV?", "MU?", "SI?", "MS?", "MSSMART ?", "SLP?", "STBY?", "SSLEV ?" })
        {
            await SendRawAsync(query);
            await Task.Delay(80); // ~50–100 ms, hogy az erősítő ne hagyjon ki választ
        }

        // Biztonsági újra-lekérdezés: lejátszás közbeni adatözönben az első SSLEV ?
        // válasza elveszhet, ezért a hangszóró-szinteket kicsit később még egyszer kérjük.
        await Task.Delay(400);
        await SendRawAsync("SSLEV ?");

        // A beállítás-katalógus feltöltése. Előbb a globális (SS…, ECO), utána a
        // forrásfüggő (PS…) blokk, hogy a kettő ne fusson egymásba.
        await RefreshSystemParamsAsync();
        await RefreshAudioParamsAsync();

        // A bejövő jel formátumát innentől folyamatosan követjük, hogy
        // hangsáv-váltáskor magától frissüljön a kijelzés.
        StartSignalPolling();
    }

    private async Task SendAsync(string command)
    {
        if (!IsConnected)
            return;
        await SendRawAsync(command);
    }

    /// <summary>Küldés az auto-standby óra nullázása NÉLKÜL – a háttérben futó
    /// jelinfó-pollozáshoz. Felhasználói művelethez a SendRawAsync való.</summary>
    private async Task SendQuietAsync(string command)
    {
        if (!IsConnected)
            return;
        await _connection.SendCommandAsync(command);
    }

    /// <summary>Naplózva küld egy parancsot a service-en keresztül.</summary>
    private async Task SendRawAsync(string command)
    {
        AddLog("» " + command);
        // Bármilyen elküldött parancs = felhasználói művelet → nullázzuk az órát.
        NoteActivity();
        await _connection.SendCommandAsync(command);
    }

    // ---- Bejövő üzenetek parse-olása (háttérszálról, UI szálra marshalolva) ----

    private void OnLineReceived(string line)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            AddLog("« " + line);
            ParseLine(line);
            // A jelinfó-pollozás VÁLASZAI nem felhasználói aktivitás. Ha ezek is
            // nullázzák az órát, az auto-standby visszaszámláló sosem jelenik meg.
            if (line.StartsWith("SSINFAIS", StringComparison.Ordinal))
                return;
            // A vevő bármilyen küldése = aktivitás → az auto-standby óra nullázódik
            // (lejátszáskor folyamatos a forgalom, ezért a visszaszámláló rejtve marad).
            NoteActivity();
        });
    }

    private void ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Quick Select nevek: "SSQSNZMAQS1 Smart Select 1", vége "SSQSNZMA END".
        if (line.StartsWith("SSQSNZMA", StringComparison.Ordinal))
        {
            var rest = line.Substring("SSQSNZMA".Length).TrimStart();
            if (rest.Equals("END", StringComparison.Ordinal))
                return;
            int sp = rest.IndexOf(' ');
            if (sp > 2 && rest.StartsWith("QS", StringComparison.Ordinal))
            {
                var num = rest.Substring(2, sp - 2);
                var name = rest.Substring(sp + 1).Trim();
                if (name.Length > 0)
                    ApplyQuickSelectName(num, name);
            }
            return;
        }

        // SSFUN: az aktív bemenetlista + nevek. Formátum: "SSFUN<TOKEN> <név>".
        // A lista végét a "SSFUN END" sor jelzi.
        if (line.StartsWith("SSFUN", StringComparison.Ordinal))
        {
            var rest = line.Substring("SSFUN".Length).TrimStart();
            if (rest.Equals("END", StringComparison.Ordinal))
            {
                _sourceListPending = false; // a lista teljes; a kezdő-clear már megtörtént
                EnsureNetworkSource();
                return;
            }
            int sp = rest.IndexOf(' ');
            if (sp > 0)
            {
                var token = rest.Substring(0, sp);
                var name = rest.Substring(sp + 1).Trim();
                if (name.Length > 0)
                    ApplySourceEntry(token, name);
            }
            return;
        }

        // Elalváskapcsoló: "SLPOFF" vagy "SLP060" (hátralévő perc).
        if (line.StartsWith("SLP", StringComparison.Ordinal))
        {
            var rest = line.Substring(3).Trim();
            if (rest.Equals("OFF", StringComparison.Ordinal))
                ApplySleepFromReceiver(0);
            else if (int.TryParse(rest, out int min))
                ApplySleepFromReceiver(min);
            return;
        }

        // Auto-standby: "STBYOFF" / "STBY15M" / "STBY30M" / "STBY60M".
        if (line.StartsWith("STBY", StringComparison.Ordinal))
        {
            AutoStandby = line.Substring(4).Trim();
            return;
        }

        // Hangszóró-preset: "SPPR 1" / "SPPR 2".
        if (line.StartsWith("SPPR", StringComparison.Ordinal))
        {
            if (int.TryParse(line.Substring(4).Trim(), out int p))
                SpeakerPreset = p;
            return;
        }

        // Hangszóró-konfiguráció: "SSSPCFRO LAR" / "SSSPCSWF YES" / "...NON".
        // Ez mondja meg, mely hangszórók vannak ténylegesen jelen.
        if (line.StartsWith("SSSPC", StringComparison.Ordinal))
        {
            // Ez az ág korábban visszatér, mint a katalógus-diszpécser, ezért a
            // Large/Small vezérlőket itt kell etetni (a hangszóró-jelenlét mellett).
            _systemParams.TryDispatch(line);
            var rest = line.Substring("SSSPC".Length).TrimStart();
            int sp = rest.IndexOf(' ');
            if (sp > 0)
                ApplySpeakerGroup(rest.Substring(0, sp), rest.Substring(sp + 1).Trim());
            return;
        }

        // Hangszóró-szintek (kalibrált, a telefon „Speakers/Levels" oldalával egyező):
        // "SSLEVSW 62", "SSLEVC 50", a lista végén "SSLEV END". 50 = 0 dB.
        if (line.StartsWith("SSLEV", StringComparison.Ordinal))
        {
            var rest = line.Substring("SSLEV".Length).TrimStart();
            if (rest.Equals("END", StringComparison.Ordinal))
                return;
            int sp = rest.IndexOf(' ');
            if (sp > 0)
            {
                var key = rest.Substring(0, sp);
                var valTok = rest.Substring(sp + 1).Trim();
                if (TryParseLevel(valTok, out double lvl))
                    ApplySpeakerLevel(key, lvl);
            }
            return;
        }

        // MVMAX a hangerő MAXIMUMA, NEM az aktuális hangerő – ezt elsőként szűrjük ki.
        if (line.StartsWith("MVMAX", StringComparison.Ordinal))
        {
            var maxPart = line.Substring("MVMAX".Length).Trim();
            if (TryParseVolume(maxPart, out double max))
                VolumeMax = max;
            return;
        }

        if (line.StartsWith("MV", StringComparison.Ordinal))
        {
            var digits = line.Substring(2).Trim();
            if (TryParseVolume(digits, out double vol))
                VolumeRaw = vol;
            return;
        }

        if (line.StartsWith("PW", StringComparison.Ordinal))
        {
            if (line.Equals("PWON", StringComparison.Ordinal))
                IsPoweredOn = true;
            else if (line.StartsWith("PWSTANDBY", StringComparison.Ordinal))
                IsPoweredOn = false;
            return;
        }

        if (line.StartsWith("MU", StringComparison.Ordinal))
        {
            if (line.Equals("MUON", StringComparison.Ordinal))
                IsMuted = true;
            else if (line.Equals("MUOFF", StringComparison.Ordinal))
                IsMuted = false;
            return;
        }

        if (line.StartsWith("SI", StringComparison.Ordinal))
        {
            CurrentSourceToken = line.Substring(2).Trim();
            CurrentSource = GetSourceLabel(CurrentSourceToken);
            // A PS… beállításokat a vevő FORRÁSONKÉNT külön tárolja → újraolvasás.
            ScheduleAudioParamRefresh();
            return;
        }

        // FONTOS: a Smart/Quick Select visszajelzése („MSSMART4") is „MS"-sel kezdődik,
        // ezért a hangzásmód-ág ELŐTT kell elkapni – különben „SMART4" lenne a hangzásmód.
        if (line.StartsWith("MSSMART", StringComparison.Ordinal))
        {
            CurrentQuickSelect = line.Substring("MSSMART".Length).Trim();
            return;
        }

        if (line.StartsWith("MS", StringComparison.Ordinal))
        {
            SoundMode = line.Substring(2).Trim();
            // A hangzásmód eldönti, mely surround-paraméterek élnek egyáltalán.
            ScheduleAudioParamRefresh();
            return;
        }

        // Beállítás-katalógus (PS…, SS…, ECO). SZÁNDÉKOSAN a lánc legvégén:
        // így egyetlen meglévő, sorrendfüggő ág elé sem kerül (MSSMART-csapda).
        if (_audioParams.TryDispatch(line) || _systemParams.TryDispatch(line)
            || _signalParams.TryDispatch(line))
            return;

        // Ismeretlen prefix: csendben eldobjuk – nem hiba.
    }

    /// <summary>
    /// Hangerő token → numerikus érték. 2 számjegy = egész (MV56→56),
    /// 3 számjegy = a harmadik fél lépés (MV565→56.5).
    /// </summary>
    private static bool TryParseVolume(string token, out double value)
    {
        value = 0;
        if (token.Length is < 2 or > 3)
            return false;
        foreach (char c in token)
            if (!char.IsDigit(c))
                return false;

        if (token.Length == 2)
        {
            value = int.Parse(token, CultureInfo.InvariantCulture);
        }
        else
        {
            value = int.Parse(token.Substring(0, 2), CultureInfo.InvariantCulture)
                    + (token[2] == '5' ? 0.5 : 0.0);
        }
        return true;
    }

    private void ApplyQuickSelectName(string number, string name)
    {
        foreach (var qs in QuickSelects)
        {
            if (string.Equals(qs.Number, number, StringComparison.Ordinal))
            {
                qs.Name = name;
                return;
            }
        }
    }

    private static bool TryParseLevel(string token, out double value) =>
        TryParseVolume(token, out value);

    // ---- Hangszóró-preset (SPPR 1/2) ----
    // A vevő két hangszóró-presetet tárol, mindegyikben külön szintekkel/konfiggal.
    // A SSLEV mindig az AKTÍV preset értékeit adja/állítja.

    private int _speakerPreset = 1;
    public int SpeakerPreset
    {
        get => _speakerPreset;
        private set
        {
            if (Set(ref _speakerPreset, value))
            {
                OnPropertyChanged(nameof(IsPreset1Active));
                OnPropertyChanged(nameof(IsPreset2Active));
            }
        }
    }

    public bool IsPreset1Active => _speakerPreset == 1;
    public bool IsPreset2Active => _speakerPreset == 2;

    public ICommand SelectPresetCommand =>
        _selectPresetCommand ??= new RelayCommand<string>(SelectPresetAsync);
    private RelayCommand<string>? _selectPresetCommand;

    private async Task SelectPresetAsync(string? n)
    {
        if (!IsConnected || !int.TryParse(n, out int p))
            return;
        await SendAsync("SPPR " + p);
        // A preset váltás után a hangszóró-konfig és a szintek is mások lehetnek →
        // tiszta lapról újraolvassuk őket.
        await Task.Delay(300);
        ChannelLevels.Clear();
        _speakerGroupPresent.Clear();
        _receivedLevels.Clear();
        await SendRawAsync("SSSPC ?");
        await Task.Delay(250);
        await SendRawAsync("SSLEV ?");

        // Biztonsági újra-lekérdezés – ugyanaz a logika, mint csatlakozáskor:
        // lejátszás közbeni adatözönben egy-egy válasz elveszhet. A pufferelés
        // miatt ez már csak elvesztett VÁLASZ ellen kell, sorrend ellen nem.
        await Task.Delay(600);
        await SendRawAsync("SSSPC ?");
        await Task.Delay(150);
        await SendRawAsync("SSLEV ?");

        // A preset a keresztváltást, a mélyláda-módot és az LFE-szűrőt is tárolja
        // (élőben mérve: a két preset ezekben tér el igazán, nem a szintgörbében).
        await RefreshSystemParamsAsync();
    }

    // A ténylegesen jelen lévő hangszóró-csoportok (SSSPC alapján). Csak ezek
    // csatornáit mutatjuk a panelben (a nem létezőket – pl. surround back – nem).
    private readonly Dictionary<string, bool> _speakerGroupPresent = new();

    /// <summary>
    /// MINDEN beérkezett SSLEV érték, függetlenül attól, hogy a hozzá tartozó
    /// hangszóró jelenléte (SSSPC) már ismert-e.
    ///
    /// Ez a pufferelés kritikus: a két lekérdezés válaszai versenyeznek egymással,
    /// és ha az SSLEV előbb érkezik, mint az SSSPC, akkor a szinteket korábban
    /// EGYSZERŰEN ELDOBTUK – ezért jelent meg preset-váltás után néha csak egy
    /// csúszka, és ezért „javította meg" egy újabb preset-váltás. Most a látható
    /// listát ebből a pufferből építjük újra, akármilyen sorrendben jön az adat.
    /// </summary>
    private readonly Dictionary<string, double> _receivedLevels = new();

    /// <summary>
    /// A csatornák MEGJELENÍTÉSI sorrendje. A vevő válaszainak sorrendje nem
    /// determinisztikus (ezért látszottak a csúszkák preset 1-ben és 2-ben más
    /// rendben), ezért fix, fizikai elrendezést követő sorrendet kényszerítünk.
    /// </summary>
    private static readonly string[] ChannelOrder =
    {
        "FL", "FR", "C",
        "SL", "SR",
        "SBL", "SB", "SBR",
        "FHL", "FHR", "TFL", "TFR", "TML", "TMR",
        "FDL", "FDR", "SDL", "SDR",
        "SW", "SW2",
    };

    private static int ChannelRank(string key)
    {
        int i = Array.IndexOf(ChannelOrder, key);
        return i < 0 ? int.MaxValue : i;
    }

    private void ApplySpeakerGroup(string group, string value)
    {
        if (value.Equals("NON", StringComparison.Ordinal))
            _speakerGroupPresent[group] = false;            // a NON mindig győz
        else if (!_speakerGroupPresent.ContainsKey(group))
            _speakerGroupPresent[group] = true;

        // Most már többet tudunk a hangszórókról → a puffer alapján újraépítjük
        // a listát (így a korábban beérkezett szintek sem esnek ki).
        RebuildChannelLevels();
    }

    /// <summary>SSLEV csatorna kulcsa → hangszóró-csoport (SSSPC). null = nem mutatjuk.
    /// Az SR5015 SSSPC ? válasza pontosan ezeket a csoportokat sorolja fel (FRO CEN SUA
    /// SBK FRH TFR TPM FRD SUD SWF). Nagyobb modellek további csatornáit (TRL/RHL/BDL/
    /// SHL/TS …) szándékosan nem találgatjuk: csoportkódjuk itt nem ellenőrizhető.</summary>
    private static string? SpeakerGroupForChannel(string key) => key switch
    {
        "FL" or "FR" => "FRO",
        "C" => "CEN",
        "SL" or "SR" => "SUA",
        "SBL" or "SBR" or "SB" => "SBK",
        "FHL" or "FHR" => "FRH",
        "TFL" or "TFR" => "TFR",
        "TML" or "TMR" => "TPM",
        "FDL" or "FDR" => "FRD",
        "SDL" or "SDR" => "SUD",
        "SW" or "SW2" => "SWF",
        _ => null
    };

    /// <summary>Egy hangszóró-szint eltárolása az SSLEV-válaszból. Nem szűrünk
    /// itt: a szűrés és a sorrendezés a <see cref="RebuildChannelLevels"/>-ben van.</summary>
    private void ApplySpeakerLevel(string key, double level)
    {
        if (SpeakerGroupForChannel(key) is null)
            return;                                  // ismeretlen csatorna – nem érdekel

        _receivedLevels[key] = level;
        RebuildChannelLevels();
    }

    /// <summary>
    /// A látható csúszkák újraépítése a pufferből: csak a tényleg jelen lévő
    /// hangszórók, fix sorrendben. Ha a halmaz és a sorrend már stimmel, csak az
    /// értékeket frissíti (nem cseréli az objektumokat, így nem villog a UI).
    /// </summary>
    private void RebuildChannelLevels()
    {
        var wanted = _receivedLevels.Keys
            .Where(k => SpeakerGroupForChannel(k) is { } g && _speakerGroupPresent.GetValueOrDefault(g))
            .OrderBy(ChannelRank)
            .ToList();

        bool sameOrder = wanted.Count == ChannelLevels.Count;
        if (sameOrder)
        {
            for (int i = 0; i < wanted.Count; i++)
            {
                if (!string.Equals(ChannelLevels[i].Key, wanted[i], StringComparison.Ordinal))
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        if (sameOrder)
        {
            foreach (var ch in ChannelLevels)
                ch.SetFromReceiver(_receivedLevels[ch.Key]);
            return;
        }

        ChannelLevels.Clear();
        foreach (var key in wanted)
            ChannelLevels.Add(new ChannelLevel(key, ChannelDisplayName(key), _receivedLevels[key], SendChannelLevel));
    }

    private async void SendChannelLevel(string key, double level)
    {
        if (!IsConnected) return;
        await SendAsync($"SSLEV{key} {EncodeHalfStep(level)}");
    }

    // A csatornaszintek nullázó parancsa SZÁNDÉKOSAN nincs többé — sem gomb, sem
    // kód. A szintek Audyssey-beméréssel születnek, és egyetlen félrekattintás
    // elvinné az egész munkát. Ha kell, a Beállítások ablakból presetet lehet
    // váltani, vagy új mérést indítani.

    private static string ChannelDisplayName(string key) => key switch
    {
        "FL" => "Front L",
        "FR" => "Front R",
        "C" => "Center",
        "SW" => "Subwoofer",
        "SW2" => "Subwoofer 2",
        "SL" => "Surround L",
        "SR" => "Surround R",
        "SBL" => "S. Back L",
        "SBR" => "S. Back R",
        "SB" => "S. Back",
        "FHL" => "F. Height L",
        "FHR" => "F. Height R",
        "TFL" => "Top Front L",
        "TFR" => "Top Front R",
        "TML" => "Top Middle L",
        "TMR" => "Top Middle R",
        "FDL" => "F. Dolby L",
        "FDR" => "F. Dolby R",
        "SDL" => "S. Dolby L",
        "SDR" => "S. Dolby R",
        _ => key
    };

    /// <summary>
    /// Egy SSFUN-bejegyzés feldolgozása. A friss lista első sora kiüríti a régit,
    /// utána soronként hozzáadja a forrást (meglévő tokent frissít).
    /// </summary>
    private void ApplySourceEntry(string token, string name)
    {
        if (_sourceListPending)
        {
            Sources.Clear();
            _sourceListPending = false;
        }

        foreach (var s in Sources)
        {
            if (string.Equals(s.Token, token, StringComparison.Ordinal))
            {
                s.Name = name;
                if (string.Equals(CurrentSourceToken, token, StringComparison.Ordinal))
                    CurrentSource = name;
                return;
            }
        }

        Sources.Add(new SourceItem(token, name));
        if (string.Equals(CurrentSourceToken, token, StringComparison.Ordinal))
            CurrentSource = name;
    }

    /// <summary>
    /// A HEOS/Online Music (NET) forrás pótlása a lista végén.
    ///
    /// Az SSFUN ? élőben CSAK a fizikai, átnevezhető bemeneteket sorolja fel
    /// (DVD/BD/TV/SAT-CBL/MPLAY/GAME/AUX1/CD/8K/PHONO) – a NET nincs köztük, pedig
    /// teljesen érvényes forrás: a vevő `SI?`-re `SINET`-et válaszol, és `SINET`-tel
    /// választható. Nélküle a HEOS nem lenne elérhető a forrásgombokról, és az aktív
    /// forrás kiemelése sem működne HEOS közben. Lásd [[marantz-protocol-findings]].
    /// </summary>
    private void EnsureNetworkSource()
    {
        foreach (var s in Sources)
            if (string.Equals(s.Token, "NET", StringComparison.Ordinal))
                return;

        Sources.Add(new SourceItem("NET", SourceDisplayName("NET")));
        if (string.Equals(CurrentSourceToken, "NET", StringComparison.Ordinal))
            CurrentSource = SourceDisplayName("NET");
    }

    /// <summary>A token felirata: előbb az átnevezett gombnévből, különben a beépített térképből.</summary>
    private string GetSourceLabel(string token)
    {
        foreach (var s in Sources)
            if (string.Equals(s.Token, token, StringComparison.Ordinal))
                return s.Name;
        return SourceDisplayName(token);
    }

    private static string SourceDisplayName(string token) => token switch
    {
        "" => "—",
        "TV" => "TV Audio",
        "SAT/CBL" => "CBL/SAT",
        "BD" => "Blu-ray",
        "GAME" => "Game",
        "MPLAY" => "Media Player",
        "NET" => "Online Music",
        "TUNER" => "Tuner",
        "CD" => "CD",
        "DVD" => "DVD",
        "PHONO" => "Phono",
        "BT" => "Bluetooth",
        "USB" => "USB",
        "USB/IPOD" => "USB/iPod",
        "IRADIO" => "Internet Radio",
        "SERVER" => "Media Server",
        "FAVORITES" => "Favorites",
        _ => token
    };

    private void OnConnectionStateChanged(bool connected)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsConnected = connected;
            if (!connected)
            {
                StatusMessage = "Nincs kapcsolat";
                ApplyNowPlaying(null);
                IsPlaying = false;
                ResetProgress();
            }
        });
    }

    // ---- HEOS now-playing (1255-ös CLI-ből) ----

    private string _nowPlayingTitle = "";
    private string _nowPlayingArtist = "";
    private string _nowPlayingImageUrl = "";

    /// <summary>Most szóló szám címe (HEOS); üres, ha nincs vagy nem HEOS forrás szól.</summary>
    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            if (Set(ref _nowPlayingTitle, value))
            {
                OnPropertyChanged(nameof(NowPlayingDisplay));
                OnPropertyChanged(nameof(IsNowPlayingVisible));
            }
        }
    }

    public string NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set { if (Set(ref _nowPlayingArtist, value)) OnPropertyChanged(nameof(NowPlayingDisplay)); }
    }

    /// <summary>Borító URL-je (album art); üres, ha nincs.</summary>
    public string NowPlayingImageUrl
    {
        get => _nowPlayingImageUrl;
        private set
        {
            if (!Set(ref _nowPlayingImageUrl, value))
                return;
            // Egyszer töltjük le számonként, és több helyen (háttér + bélyegkép)
            // ugyanazt a példányt használjuk.
            NowPlayingImage = LoadImage(value);
            OnPropertyChanged(nameof(IsNowPlayingVisible));
        }
    }

    private ImageSource? _nowPlayingImage;

    /// <summary>A betöltött borító (háttérhez és bélyegképhez is ez megy).</summary>
    public ImageSource? NowPlayingImage
    {
        get => _nowPlayingImage;
        private set { if (Set(ref _nowPlayingImage, value)) OnPropertyChanged(nameof(HasAlbumArt)); }
    }

    /// <summary>true, ha van betölthető borító – ettől jelenik meg a háttérkép.</summary>
    public bool HasAlbumArt => IsHeosSource && _nowPlayingImage is not null;

    private static ImageSource? LoadImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Egysoros kijelzés: „Cím — Előadó".</summary>
    public string NowPlayingDisplay =>
        string.IsNullOrEmpty(NowPlayingArtist) ? NowPlayingTitle : $"{NowPlayingTitle} — {NowPlayingArtist}";

    public bool IsNowPlayingVisible => IsHeosSource && !string.IsNullOrEmpty(NowPlayingTitle);

    private void OnNowPlayingChanged(NowPlaying? np)
    {
        Application.Current?.Dispatcher.Invoke(() => ApplyNowPlaying(np));
    }

    private void ApplyNowPlaying(NowPlaying? np)
    {
        NowPlayingTitle = np?.Song ?? "";
        NowPlayingArtist = np?.Artist ?? "";
        NowPlayingImageUrl = np?.ImageUrl ?? "";
    }

    private bool _isPlaying;

    /// <summary>true, ha a HEOS szerint épp szól – ettől függ a Play/Pause gomb ikonja.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayPauseGlyph)); }
    }

    /// <summary>A közös Play/Pause gomb ikonja: ha szól → szünet, különben → lejátszás.</summary>
    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";

    private void OnPlayStateChanged(string state)
    {
        Application.Current?.Dispatcher.Invoke(() =>
            IsPlaying = string.Equals(state, "play", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Lejátszási pozíció (HEOS event/player_now_playing_progress, ~5 mp-enként) ----
    // Az érkező értékeket mutatjuk, timerrel NEM interpolálunk – így nem „hazudik" a sáv.

    private int _positionMs;
    private int _durationMs;

    /// <summary>Pozíció 0..1 arányban (a sáv kitöltéséhez).</summary>
    public double ProgressFraction => _durationMs > 0 ? Math.Clamp((double)_positionMs / _durationMs, 0, 1) : 0;

    public string PositionText => FormatTime(_positionMs);
    public string DurationText => FormatTime(_durationMs);

    /// <summary>true, ha HEOS forrás szól és van értelmes hossz – ettől látszik a pozíció-sáv.</summary>
    public bool HasProgress => IsHeosSource && _durationMs > 0;

    private static string FormatTime(int ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void OnProgressChanged(int posMs, int durMs)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _positionMs = posMs;
            _durationMs = durMs;
            OnPropertyChanged(nameof(ProgressFraction));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(HasProgress));
        });
    }

    private void ResetProgress()
    {
        _positionMs = 0;
        _durationMs = 0;
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasProgress));
    }

    // ---- INotifyPropertyChanged ----

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Egy forrásgomb: állandó protokoll-token és élőben frissülő felirat.</summary>
public sealed class SourceItem : INotifyPropertyChanged
{
    public SourceItem(string token, string name)
    {
        Token = token;
        _name = name;
    }

    public string Token { get; }

    private string _name;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Egy hangzásmód-gomb: a vevőnek küldött token (MS után), a felirat,
/// és a kiemeléshez használt illesztési szabály az MS? válaszhoz.</summary>
public sealed class SoundModeItem : INotifyPropertyChanged
{
    private readonly string? _matchExact;
    private readonly string? _matchContains;

    public SoundModeItem(string token, string label, string? matchExact = null, string? matchContains = null)
    {
        Token = token;
        Label = label;
        _matchExact = matchExact;
        _matchContains = matchContains;
    }

    public string Token { get; }
    public string Label { get; }

    /// <summary>Aktív-e ez a mód az adott (MS?-ből jövő) token mellett.</summary>
    public bool Matches(string current)
    {
        if (string.IsNullOrEmpty(current)) return false;
        if (_matchExact is not null && current.Equals(_matchExact, StringComparison.Ordinal)) return true;
        if (_matchContains is not null && current.Contains(_matchContains, StringComparison.Ordinal)) return true;
        return false;
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Egy Quick Select gomb: protokoll-szám (1..4) és élő felirat.</summary>
public sealed class QuickSelectItem : INotifyPropertyChanged
{
    private readonly Action<string>? _recall;

    public QuickSelectItem(string number, string name, Action<string>? recall = null)
    {
        Number = number;
        _name = name;
        _recall = recall;
    }

    public string Number { get; }

    /// <summary>A gomb ezt hívja. Önálló parancs: nem kell ablak-ős kötés.</summary>
    /// <summary>A gomb ezt hívja. Önálló parancs: nem kell ablak-ős kötés.</summary>
    public ICommand SelectCommand => _select ??= new RelayCommand(() => _recall?.Invoke(Number));

    private RelayCommand? _select;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    private string _name;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Egy csatornaszint-csúszka modellje. A nyers érték 50 = 0 dB.</summary>
public sealed class ChannelLevel : INotifyPropertyChanged
{
    private readonly Action<string, double> _send;
    private bool _applyingIncoming;

    public ChannelLevel(string key, string name, double value, Action<string, double> send)
    {
        Key = key;
        Name = name;
        _value = value;
        _send = send;
    }

    public string Key { get; }
    public string Name { get; }

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) < 0.001)
                return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DbText)));
            if (!_applyingIncoming)
                _send(Key, Math.Round(value * 2) / 2);
        }
    }

    /// <summary>dB-kijelzés (50 = 0 dB), előjellel.</summary>
    public string DbText
    {
        get
        {
            double db = _value - 50;
            string sign = db > 0 ? "+" : "";
            return sign + db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }
    }

    /// <summary>A vevő által küldött érték alkalmazása – nem küld vissza parancsot.</summary>
    public void SetFromReceiver(double v)
    {
        _applyingIncoming = true;
        Value = v;
        _applyingIncoming = false;
    }

    // ---- Finomhangoló gombok a csúszka két végén (0,5 dB lépés) ----

    public ICommand DownCommand => _downCommand ??= new RelayCommand(() => Nudge(-0.5));
    private RelayCommand? _downCommand;

    public ICommand UpCommand => _upCommand ??= new RelayCommand(() => Nudge(+0.5));
    private RelayCommand? _upCommand;

    /// <summary>Lépés fél dB-t, a vevő tartományára (38..62 = ±12 dB) vágva.</summary>
    private void Nudge(double delta) =>
        Value = Math.Clamp(Math.Round((_value + delta) * 2) / 2, 38, 62);

    public event PropertyChangedEventHandler? PropertyChanged;
}
