using System.Collections.ObjectModel;
using System.Windows.Input;

namespace MarantzController;

// ---------------------------------------------------------------------------
//  Vevőkeresés a hálózaton (SSDP) – csak lekapcsolt állapotban, mert a vevő
//  egyszerre egy telnet-klienst szolgál ki. Lásd ReceiverDiscovery.cs.
// ---------------------------------------------------------------------------

public sealed partial class MainViewModel
{
    public ObservableCollection<DiscoveredReceiver> DiscoveredReceivers { get; } = new();

    private bool _isDiscovering;
    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set
        {
            if (Set(ref _isDiscovering, value))
                _findReceivers?.RaiseCanExecuteChanged();
        }
    }

    private bool _isDiscoveryOpen;
    public bool IsDiscoveryOpen
    {
        get => _isDiscoveryOpen;
        set => Set(ref _isDiscoveryOpen, value);
    }

    public ICommand FindReceiversCommand => _findReceivers ??=
        new RelayCommand(FindReceiversAsync, () => !IsConnected && !IsDiscovering);
    private RelayCommand? _findReceivers;

    public ICommand UseReceiverCommand => _useReceiver ??= new RelayCommand<DiscoveredReceiver>(r =>
    {
        if (r is null) return;
        IpAddress = r.IpAddress;
        IsDiscoveryOpen = false;
        StatusMessage = $"{r.Model} selected — press Connect.";
        OnPropertyChanged(nameof(StatusText));
    });
    private RelayCommand<DiscoveredReceiver>? _useReceiver;

    private async Task FindReceiversAsync()
    {
        IsDiscovering = true;
        IsDiscoveryOpen = false;
        StatusMessage = "Searching the network…";
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var found = await ReceiverDiscovery.FindAsync();

            DiscoveredReceivers.Clear();
            foreach (var r in found)
                DiscoveredReceivers.Add(r);

            if (found.Count == 0)
            {
                StatusMessage = "No receiver found. Check that it is on the same network, or type its IP address.";
            }
            else if (found.Count == 1)
            {
                // Egyetlen találat: nincs mit választani, rögtön beírjuk.
                IpAddress = found[0].IpAddress;
                StatusMessage = $"Found {found[0].Model} — press Connect.";
            }
            else
            {
                StatusMessage = $"Found {found.Count} receivers — pick one.";
                IsDiscoveryOpen = true;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {FirstLine(ex.Message)}";
        }
        finally
        {
            IsDiscovering = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }
}
