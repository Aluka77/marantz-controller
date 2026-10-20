using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace MarantzController;

// ---------------------------------------------------------------------------
//  HEOS-extrák: keverés, ismétlés, lejátszási sor.
//  A HEOS CLI eseményei háttérszálról jönnek → mindent a UI-szálra marshalolunk.
// ---------------------------------------------------------------------------

public sealed partial class MainViewModel
{
    private void HookHeosExtras()
    {
        _heos.PlayModeChanged += (repeat, shuffle) =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _repeatMode = string.IsNullOrEmpty(repeat) ? "off" : repeat;
                _shuffleOn = shuffle == "on";
                OnPropertyChanged(nameof(RepeatGlyph));
                OnPropertyChanged(nameof(IsRepeatActive));
                OnPropertyChanged(nameof(IsShuffleActive));
                OnPropertyChanged(nameof(RepeatToolTip));

                // Ez a VALÓDI állapot (a set_play_mode nyugtája nem az). Ha mást kértünk,
                // mint ami lett, a vevő elnyelte a parancsot – ezt meg kell mondani.
                if (_pendingPlayMode is { } want &&
                    (want.Repeat != _repeatMode || want.Shuffle != _shuffleOn))
                {
                    PlayModeNote = IsStationPlayback
                        ? "Repeat and shuffle belong to the app that is streaming, not the receiver."
                        : "The receiver did not accept the repeat/shuffle change.";
                }
                else if (_pendingPlayMode is not null)
                {
                    PlayModeNote = "";
                }
                _pendingPlayMode = null;
            });

        _heos.QueueReceived += items =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Queue.Clear();
                foreach (var i in items)
                    Queue.Add(i);
                OnPropertyChanged(nameof(HasQueue));
            });
    }

    // ---- Ismétlés / keverés ----

    private string _repeatMode = "off";     // off | on_all | on_one
    private bool _shuffleOn;

    public bool IsRepeatActive => _repeatMode != "off";
    public bool IsShuffleActive => _shuffleOn;

    /// <summary>Segoe MDL2 ikon: RepeatAll / RepeatOne (ismétlés ki állapotban is RepeatAll,
    /// csak nem arany).</summary>
    public string RepeatGlyph =>
        char.ConvertFromUtf32(_repeatMode == "on_one" ? 0xE8ED : 0xE8EE);

    public string RepeatToolTip => IsStationPlayback
        ? "Repeat — set it in the app that is streaming (Spotify Connect / radio)"
        : _repeatMode switch
        {
            "on_all" => "Repeat: all",
            "on_one" => "Repeat: one",
            _ => "Repeat: off",
        };

    public string ShuffleToolTip => IsStationPlayback
        ? "Shuffle — set it in the app that is streaming (Spotify Connect / radio)"
        : "Shuffle";

    /// <summary>A vevő a sorból (type="song") lejátszott zenénél fogadja csak el az
    /// ismétlést/keverést. Spotify Connect-nél és rádiónál a küldő app dönt – a vevő
    /// ilyenkor "success"-t hazudik, de nem csinál semmit (élőben mérve).</summary>
    public bool IsPlayModeAvailable => !IsStationPlayback;

    private string _playModeNote = "";

    /// <summary>Rövid magyarázat, ha a vevő elnyelte a kért módot; különben üres.</summary>
    public string PlayModeNote
    {
        get => _playModeNote;
        private set
        {
            if (Set(ref _playModeNote, value))
                OnPropertyChanged(nameof(HasPlayModeNote));
        }
    }

    public bool HasPlayModeNote => _playModeNote.Length > 0;

    /// <summary>Amit legutóbb KÉRTÜNK. A visszaolvasott állapottal összevetve derül ki,
    /// hogy a vevő tényleg végrehajtotta-e.</summary>
    private (string Repeat, bool Shuffle)? _pendingPlayMode;

    public ICommand ToggleRepeatCommand => _toggleRepeat ??= new RelayCommand(() =>
    {
        // Körbe: ki → összes → egy → ki
        var next = _repeatMode switch { "off" => "on_all", "on_all" => "on_one", _ => "off" };
        _pendingPlayMode = (next, _shuffleOn);
        _ = _heos.SetPlayModeAsync(next, _shuffleOn ? "on" : "off");
    });
    private RelayCommand? _toggleRepeat;

    public ICommand ToggleShuffleCommand => _toggleShuffle ??= new RelayCommand(() =>
    {
        _pendingPlayMode = (_repeatMode, !_shuffleOn);
        _ = _heos.SetPlayModeAsync(_repeatMode, _shuffleOn ? "off" : "on");
    });
    private RelayCommand? _toggleShuffle;

    // ---- Spotify Connect / rádió felismerése ----

    private bool _isStationPlayback;

    /// <summary>true, ha a HEOS "station" típust jelent: rádió vagy Spotify Connect.
    /// Ilyenkor a lejátszási sor, az ismétlés és a keverés NEM a vevőé – a vevő saját
    /// sora ilyenkor egy korábbi, független lista marad (élőben mérve).</summary>
    public bool IsStationPlayback
    {
        get => _isStationPlayback;
        private set
        {
            if (!Set(ref _isStationPlayback, value))
                return;
            OnPropertyChanged(nameof(QueueHint));
            OnPropertyChanged(nameof(IsPlayModeAvailable));
            OnPropertyChanged(nameof(RepeatToolTip));
            OnPropertyChanged(nameof(ShuffleToolTip));
            PlayModeNote = "";      // forrásváltáskor a régi magyarázat már nem érvényes
        }
    }

    public string QueueHint => IsStationPlayback
        ? "Streaming from an app — this is the receiver's own queue, not what is playing now."
        : "";

    // ---- Lejátszási sor ----

    public ObservableCollection<QueueItem> Queue { get; } = new();

    public bool HasQueue => Queue.Count > 0;

    private bool _isQueueOpen;
    public bool IsQueueOpen
    {
        get => _isQueueOpen;
        set
        {
            if (!Set(ref _isQueueOpen, value)) return;
            // A sort csak nyitáskor kérjük le – zárva nem terheljük a vevőt.
            if (value)
                _ = _heos.RefreshQueueAsync();
        }
    }

    public ICommand ToggleQueueCommand => _toggleQueue ??= new RelayCommand(() => IsQueueOpen = !IsQueueOpen);
    private RelayCommand? _toggleQueue;

    public ICommand PlayQueueItemCommand => _playQueueItem ??= new RelayCommand<QueueItem>(item =>
    {
        if (item is null) return;
        _ = _heos.PlayQueueItemAsync(item.Qid);
        IsQueueOpen = false;
    });
    private RelayCommand<QueueItem>? _playQueueItem;
}
