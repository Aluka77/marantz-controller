using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MarantzController;

// ---------------------------------------------------------------------------
//  Általános vevő-beállítás modell.
//
//  A vevő több tucat beállítást ismer (PS…, SS…, ECO), amelyek mind ugyanarra
//  a két mintára illeszkednek: „válassz egy tokent" vagy „állíts egy számot".
//  Ezért nem 25 egyedi property + 25 parse-ág készül, hanem két általános
//  osztály (AvOption / AvLevel) és egy katalógus (AvOptionRegistry).
//
//  A ParseLine láncába EGYETLEN új ág kerül, a legvégére – így a meglévő,
//  sorrendfüggő ágakat (MSSMART-csapda!) nem érinti.
// ---------------------------------------------------------------------------

/// <summary>Közös ős: INotifyPropertyChanged + a diszpécser által használt felület.</summary>
public abstract class AvItem : INotifyPropertyChanged
{
    protected AvItem(string prefix, string label, string? query = null)
    {
        Prefix = prefix;
        Label = label;
        // Alapértelmezett lekérdezés: „PSDYNVOL " → „PSDYNVOL ?".
        Query = query ?? prefix.TrimEnd() + " ?";
    }

    /// <summary>A parancs/válasz eleje, a záró szóközzel együtt (pl. „PSDYNVOL ").
    /// A „PSMULTEQ:" és a „PSCINEMA EQ." szóköz nélküli, ezért van külön mezőben.</summary>
    public string Prefix { get; }

    /// <summary>Magyar felirat a UI-on.</summary>
    public string Label { get; }

    /// <summary>A lekérdező parancs. Több elem is oszthat egy lekérdezést
    /// (pl. az „SSCFR ?" egyszerre tölti az összes keresztváltás-elemet).</summary>
    public string Query { get; }

    private bool _isAvailable = true;
    /// <summary>Hamis, ha a vevő az aktuális forrás/hangzásmód mellett nem válaszol
    /// erre a beállításra (pl. Cinema EQ csak film-módban él).</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set { if (Set(ref _isAvailable, value)) OnPropertyChanged(nameof(IsRowVisible)); }
    }

    private bool _isEnabled = true;
    /// <summary>Hamis, ha logikailag függ egy másiktól (pl. Ref. Level csak Dynamic EQ mellett).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (Set(ref _isEnabled, value)) OnPropertyChanged(nameof(IsRowVisible)); }
    }

    /// <summary>A sor csak akkor látszik, ha a vevő ismeri ÉS az adott helyzetben
    /// van értelme. Így nem kell 40 kiszürkült gombot mutatni – ez tartja a
    /// beállítás-listákat rövidnek.</summary>
    public bool IsRowVisible => _isAvailable && _isEnabled;

    /// <summary>Igaz, amíg a frissítés óta nem jött válasz erre az elemre.</summary>
    internal bool Pending { get; set; }

    /// <summary>A vevőtől érkezett érték (a prefix utáni rész) alkalmazása.</summary>
    internal abstract void ApplyRaw(string rest);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Egy választható érték az AvOption-ön belül (egy pill-gomb).</summary>
public sealed class AvChoice : INotifyPropertyChanged
{
    internal AvChoice(AvOption owner, string token, string label)
    {
        _owner = owner;
        Token = token;
        Label = label;
    }

    private readonly AvOption _owner;

    /// <summary>A vevőnek küldött/általa visszaadott token (pl. „BYP.LR").</summary>
    public string Token { get; }
    public string Label { get; }

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

    public ICommand SelectCommand => _select ??= new RelayCommand(() => _owner.Select(Token));
    private RelayCommand? _select;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Diszkrét választás (pl. MultEQ, Dynamic Volume, keresztváltás-frekvencia).</summary>
public sealed class AvOption : AvItem
{
    private readonly Action<string> _send;

    public AvOption(string prefix, string label, Action<string> send,
                    (string Token, string Label)[] choices, string? query = null)
        : base(prefix, label, query)
    {
        _send = send;
        foreach (var (token, text) in choices)
            Choices.Add(new AvChoice(this, token, text));
    }

    public ObservableCollection<AvChoice> Choices { get; } = new();

    private string? _selectedToken;
    public string? SelectedToken
    {
        get => _selectedToken;
        private set
        {
            if (!Set(ref _selectedToken, value)) return;
            foreach (var c in Choices)
                c.IsActive = string.Equals(c.Token, value, StringComparison.OrdinalIgnoreCase);
            OnPropertyChanged(nameof(ValueLabel));
        }
    }

    /// <summary>A jelenlegi érték felirata (a fejléc-összefoglalókhoz és a csak-olvasó sorokhoz).
    /// Ismeretlen tokennél a nyers tokent adja vissza – így sosem hazudik.</summary>
    public string ValueLabel
    {
        get
        {
            if (_selectedToken is null) return "–";
            foreach (var c in Choices)
                if (string.Equals(c.Token, _selectedToken, StringComparison.OrdinalIgnoreCase))
                    return c.Label;
            return _selectedToken;
        }
    }

    /// <summary>Igaz, ha az aktuális token szerepel a választéklistában. Az ismeretlen
    /// tokent a ValueLabel nyersen adja vissza (nem hazudik) – de ahol ez csak zajt
    /// jelentene (pl. bejövő jelformátum kódja), ezzel el lehet rejteni.</summary>
    public bool HasKnownLabel
    {
        get
        {
            if (_selectedToken is null) return false;
            foreach (var c in Choices)
                if (string.Equals(c.Token, _selectedToken, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    internal void Select(string token)
    {
        if (!IsEnabled) return;
        _send(Prefix + token);
    }

    internal override void ApplyRaw(string rest) => SelectedToken = rest.Trim();
}

/// <summary>Az AvLevel token-kódolása. A vevő háromféle formát használ.</summary>
public enum AvLevelKind
{
    /// <summary>Denon dB-forma: 50 = 0 dB, a fél lépés 3. számjegyként (505 = 50,5).</summary>
    HalfStepDb,
    /// <summary>Fix szélességű egész (pl. PSDELAY 000…999, PSDIC 00…06).</summary>
    Integer,
    /// <summary>Előjeles egész, 0 helyett „00" (pl. PSLFE 00…-10).</summary>
    SignedInteger,
}

/// <summary>Numerikus beállítás −/+ gombokkal (pl. Bass, Treble, mélyláda-szint).</summary>
public sealed class AvLevel : AvItem
{
    private readonly Action<string> _send;
    private bool _applyingIncoming;

    public AvLevel(string prefix, string label, Action<string> send,
                   AvLevelKind kind, double min, double max, double step,
                   double zero = 0, int digits = 2, string unit = " dB", string? query = null)
        : base(prefix, label, query)
    {
        _send = send;
        Kind = kind;
        Min = min;
        Max = max;
        Step = step;
        Zero = zero;
        Digits = digits;
        Unit = unit;
        _value = zero;
    }

    public AvLevelKind Kind { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    /// <summary>A „nulla" nyers érték (dB-nél 50, egyébként 0).</summary>
    public double Zero { get; }
    public int Digits { get; }
    public string Unit { get; }

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            double v = Math.Clamp(value, Min, Max);
            if (Math.Abs(_value - v) < 0.001) return;
            _value = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Text));
            if (!_applyingIncoming)
                _send(Prefix + Encode(v));
        }
    }

    /// <summary>Kijelzett érték (dB-nél előjellel, egyébként mértékegységgel).</summary>
    public string Text
    {
        get
        {
            double shown = _value - Zero;
            if (Kind == AvLevelKind.HalfStepDb)
            {
                string sign = shown > 0 ? "+" : "";
                return sign + shown.ToString("0.0", CultureInfo.InvariantCulture) + Unit;
            }
            return shown.ToString("0", CultureInfo.InvariantCulture) + Unit;
        }
    }

    public ICommand UpCommand => _up ??= new RelayCommand(() => Value = _value + Step);
    private RelayCommand? _up;

    public ICommand DownCommand => _down ??= new RelayCommand(() => Value = _value - Step);
    private RelayCommand? _down;

    internal string Encode(double v) => Kind switch
    {
        AvLevelKind.HalfStepDb => EncodeHalf(v),
        AvLevelKind.SignedInteger => v == 0 ? "00" : ((int)v).ToString(CultureInfo.InvariantCulture),
        _ => ((int)Math.Round(v)).ToString(new string('0', Digits), CultureInfo.InvariantCulture),
    };

    private static string EncodeHalf(double v)
    {
        int whole = (int)Math.Floor(v + 0.001);
        bool half = Math.Abs(v - whole - 0.5) < 0.01;
        return whole.ToString("00", CultureInfo.InvariantCulture) + (half ? "5" : "");
    }

    internal override void ApplyRaw(string rest)
    {
        if (!TryDecode(rest.Trim(), out double v))
            return;
        _applyingIncoming = true;
        // A Clamp kihagyása itt szándékos lenne, de a vevő sosem küld tartományon
        // kívülit; ha mégis, a Value settere levágja – jobb, mint hibás kijelzés.
        Value = v;
        _applyingIncoming = false;
    }

    private bool TryDecode(string token, out double value)
    {
        value = 0;
        if (token.Length == 0) return false;

        if (Kind == AvLevelKind.HalfStepDb)
        {
            // „50" = 50,0 · „505" = 50,5. (A vevő itt sosem küld előjelet.)
            foreach (char c in token)
                if (!char.IsDigit(c)) return false;
            if (token.Length == 2 && int.TryParse(token, out int w)) { value = w; return true; }
            if (token.Length == 3 && token[2] == '5' && int.TryParse(token.Substring(0, 2), out int w2))
            {
                value = w2 + 0.5;
                return true;
            }
            return false;
        }

        return double.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// A beállítás-katalógus. Egyetlen helyen tartja az összes prefixet, és
/// LEGHOSSZABB-PREFIX illesztéssel osztja szét a bejövő sorokat.
/// A leghosszabb illesztés kötelező: „PSDEL" ⊂ „PSDELAY", „SSCFR " ⊂ „SSCFRALL ".
/// </summary>
public sealed class AvOptionRegistry
{
    private readonly List<AvItem> _items = new();
    private AvItem[] _sorted = Array.Empty<AvItem>();

    public IReadOnlyList<AvItem> Items => _items;

    public T Add<T>(T item) where T : AvItem
    {
        _items.Add(item);
        // Hosszabb prefix előre – így a „PSDELAY 000" nem a „PSDEL" elemre esik.
        _sorted = _items.OrderByDescending(i => i.Prefix.Length).ToArray();
        return item;
    }

    /// <summary>A lekérdezendő parancsok (duplikátumok nélkül, a felvétel sorrendjében).</summary>
    public IEnumerable<string> Queries => _items.Select(i => i.Query).Distinct(StringComparer.Ordinal);

    /// <summary>Igaz, ha a sor egy ismert beállításhoz tartozott és feldolgoztuk.</summary>
    public bool TryDispatch(string line)
    {
        foreach (var item in _sorted)
        {
            if (!line.StartsWith(item.Prefix, StringComparison.Ordinal))
                continue;

            var rest = line.Substring(item.Prefix.Length).Trim();
            // A több soros válaszok („SSCFR END", „SSSPC END") lezárása nem érték.
            if (rest.Length == 0 || rest.Equals("END", StringComparison.Ordinal))
                return true;

            item.ApplyRaw(rest);
            item.IsAvailable = true;
            item.Pending = false;
            return true;
        }
        return false;
    }

    /// <summary>Frissítés indítása: minden elem válaszra vár.</summary>
    public void MarkAllPending()
    {
        foreach (var i in _items)
            i.Pending = true;
    }

    /// <summary>A még mindig válasz nélküli elemek lekérdezései (ismétléshez).</summary>
    public IEnumerable<string> PendingQueries =>
        _items.Where(i => i.Pending).Select(i => i.Query).Distinct(StringComparer.Ordinal);

    /// <summary>A második kör után is néma elemek elrejtése.</summary>
    public void MarkPendingUnavailable()
    {
        foreach (var i in _items)
            if (i.Pending)
                i.IsAvailable = false;
    }
}
