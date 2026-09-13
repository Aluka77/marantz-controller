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

    public string RepeatToolTip => _repeatMode switch
    {
        "on_all" => "Repeat: all",
        "on_one" => "Repeat: one",
        _ => "Repeat: off",
    };

    public ICommand ToggleRepeatCommand => _toggleRepeat ??= new RelayCommand(() =>
    {
        // Körbe: ki → összes → egy → ki
        var next = _repeatMode switch { "off" => "on_all", "on_all" => "on_one", _ => "off" };
        _ = _heos.SetPlayModeAsync(next, _shuffleOn ? "on" : "off");
    });
    private RelayCommand? _toggleRepeat;

    public ICommand ToggleShuffleCommand => _toggleShuffle ??= new RelayCommand(() =>
        _ = _heos.SetPlayModeAsync(_repeatMode, _shuffleOn ? "off" : "on"));
    private RelayCommand? _toggleShuffle;

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
