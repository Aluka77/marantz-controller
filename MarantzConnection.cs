using System.Net.Sockets;
using System.Text;

namespace MarantzController;

/// <summary>
/// A TCP kapcsolat tulajdonosa az SR5015 felé (telnet, 23-as port).
/// Egyetlen, folyamatosan nyitva tartott kapcsolatot kezel. A bejövő streamet
/// CR (\r) mentén sorokra bontja, és minden komplett sorra a <see cref="LineReceived"/>
/// eseményt váltja ki. A parancsok ASCII szövegek, CR-rel lezárva, LF nélkül.
/// </summary>
public sealed class MarantzConnection
{
    private const char Cr = '\r';

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    // Egy időben egy parancsküldés a streamre.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Egy komplett (CR-ig olvasott) sor érkezett. Háttérszálon hívódik.</summary>
    public event Action<string>? LineReceived;

    /// <summary>true = csatlakozva, false = lecsatlakozva / megszakadt.</summary>
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _client?.Connected == true;

    /// <summary>
    /// Csatlakozik a megadott IP-re. Sikertelen csatlakozáskor kivételt dob,
    /// amit a hívó (ViewModel) kezel és a státuszban jelez.
    /// </summary>
    public async Task ConnectAsync(string ip, int port = 23)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var client = new TcpClient();
        await client.ConnectAsync(ip, port).ConfigureAwait(false);

        _client = client;
        _stream = client.GetStream();
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

        ConnectionStateChanged?.Invoke(true);
    }

    /// <summary>
    /// Elküld egy parancsot a streamre, hozzáfűzve a lezáró CR-t. ASCII kódolás.
    /// </summary>
    public async Task SendCommandAsync(string command)
    {
        var stream = _stream;
        if (stream is null || !IsConnected)
            return;

        var payload = Encoding.ASCII.GetBytes(command + Cr);

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // A stream megszakadt küldés közben – kezelje az olvasó loop a leesést.
            HandleDisconnect();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        var cts = _cts;
        cts?.Cancel();

        // FONTOS: a socketet ELŐBB zárjuk be. Egy már elindult NetworkStream.ReadAsync
        // Windowson nem feltétlenül szakad meg a CancellationTokenre – a stream lezárása
        // viszont azonnal kivételt dob benne, így az olvasóhurok kilép és nem fagyunk be.
        CleanupSocket();

        var loop = _readLoop;
        if (loop is not null)
        {
            // Időkorláttal várunk, hogy semmiképp ne ragadjon be a hívó (UI) szál.
            try { await Task.WhenAny(loop, Task.Delay(1500)).ConfigureAwait(false); }
            catch { /* leállás közbeni hibák elnyelve */ }
        }

        _cts?.Dispose();
        _cts = null;
        _readLoop = null;

        // Biztosan jelezzük a lecsatlakozást (a hurok a token miatt nem hívja meg).
        ConnectionStateChanged?.Invoke(false);
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var stream = _stream!;
        var buffer = new byte[4096];
        var line = new StringBuilder();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0)
                {
                    // A távoli vég lezárta a kapcsolatot.
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    char c = (char)buffer[i];
                    if (c == Cr)
                    {
                        if (line.Length > 0)
                        {
                            LineReceived?.Invoke(line.ToString());
                            line.Clear();
                        }
                    }
                    else if (c != '\n')
                    {
                        line.Append(c);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Szándékos leállás – nem hiba.
            return;
        }
        catch
        {
            // Stream megszakadt – lentebb jelezzük.
        }

        if (!token.IsCancellationRequested)
        {
            HandleDisconnect();
        }
    }

    private void HandleDisconnect()
    {
        CleanupSocket();
        ConnectionStateChanged?.Invoke(false);
    }

    private void CleanupSocket()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }
}
