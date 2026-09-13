using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MarantzController;

/// <summary>
/// HEOS CLI kapcsolat (1255-ös port, JSON, \r\n-nel zárt parancsok).
///
/// FONTOS tanulság (élőben mérve): a telnet (23) `NS9x` parancsok NEM vezérlik a
/// HEOS/Spotify lejátszást – azok csak a régi on-device hálózati böngészőt hajtják.
/// A HEOS forrás (SINET) lejátszásvezérlése + now-playing kizárólag innen, a HEOS
/// CLI-ből megy. A vevőnek HEOS-fiókba kell lennie léptetve, hogy a player listázódjon.
/// Lásd [[marantz-protocol-findings]].
/// </summary>
public sealed class HeosConnection
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Az első (fő) player azonosítója; null, amíg meg nem érkezik a lista.</summary>
    private long? _pid;

    public bool IsConnected => _client?.Connected == true;

    /// <summary>Most szóló média változott (cím, előadó, album, borító-URL). UI-szálra kell marshalolni.</summary>
    public event Action<NowPlaying?>? NowPlayingChanged;

    /// <summary>Lejátszási állapot változott ("play" / "pause" / "stop").</summary>
    public event Action<string>? PlayStateChanged;

    /// <summary>Lejátszási pozíció (aktuális, teljes hossz) MILLISZEKUNDUMBAN.
    /// A vevő kb. 5 másodpercenként küldi (élőben mérve).</summary>
    public event Action<int, int>? ProgressChanged;

    /// <summary>Ismétlés ("off"/"on_all"/"on_one") és keverés ("on"/"off") változott.</summary>
    public event Action<string, string>? PlayModeChanged;

    /// <summary>Megérkezett a lejátszási sor egy szelete.</summary>
    public event Action<IReadOnlyList<QueueItem>>? QueueReceived;

    public async Task ConnectAsync(string ip, int port = 1255)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var client = new TcpClient();
        await client.ConnectAsync(ip, port).ConfigureAwait(false);

        _client = client;
        _stream = client.GetStream();
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

        // Felfedezzük a playert (pid), feliratkozunk a változásokra, és lekérjük a kezdeti állapotot.
        await SendRawAsync("heos://player/get_players").ConfigureAwait(false);
        await SendRawAsync("heos://system/register_for_change_events?enable=on").ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }

        var loop = _readLoop;
        if (loop is not null)
        {
            try { await Task.WhenAny(loop, Task.Delay(1000)).ConfigureAwait(false); }
            catch { }
        }

        _cts?.Dispose();
        _cts = null;
        _readLoop = null;
        _stream = null;
        _client = null;
        _pid = null;
    }

    // ---- Lejátszásvezérlés (a UI gombjai ezeket hívják) ----

    public Task PlayAsync() => SetPlayStateAsync("play");
    public Task PauseAsync() => SetPlayStateAsync("pause");
    public Task StopAsync() => SetPlayStateAsync("stop");

    public Task NextAsync() =>
        _pid is long pid ? SendRawAsync($"heos://player/play_next?pid={pid}") : Task.CompletedTask;

    public Task PreviousAsync() =>
        _pid is long pid ? SendRawAsync($"heos://player/play_previous?pid={pid}") : Task.CompletedTask;

    /// <summary>Ismétlés + keverés beállítása. A HEOS mindkettőt egy parancsban kéri.</summary>
    public Task SetPlayModeAsync(string repeat, string shuffle) =>
        _pid is long pid
            ? SendRawAsync($"heos://player/set_play_mode?pid={pid}&repeat={repeat}&shuffle={shuffle}")
            : Task.CompletedTask;

    public Task RefreshPlayModeAsync() =>
        _pid is long pid ? SendRawAsync($"heos://player/get_play_mode?pid={pid}") : Task.CompletedTask;

    /// <summary>A lejátszási sor első <paramref name="count"/> tétele.</summary>
    public Task RefreshQueueAsync(int count = 50) =>
        _pid is long pid
            ? SendRawAsync($"heos://player/get_queue?pid={pid}&range=0,{count - 1}")
            : Task.CompletedTask;

    /// <summary>Ugrás a sor egy tételére. (A `position` paramétert a vevő IGNORÁLJA –
    /// beletekerni nem lehet, csak tételre ugrani. Élőben mérve.)</summary>
    public Task PlayQueueItemAsync(int qid) =>
        _pid is long pid ? SendRawAsync($"heos://player/play_queue?pid={pid}&qid={qid}") : Task.CompletedTask;

    private Task SetPlayStateAsync(string state) =>
        _pid is long pid ? SendRawAsync($"heos://player/set_play_state?pid={pid}&state={state}") : Task.CompletedTask;

    private Task RefreshNowPlayingAsync() =>
        _pid is long pid ? SendRawAsync($"heos://player/get_now_playing_media?pid={pid}") : Task.CompletedTask;

    private async Task SendRawAsync(string command)
    {
        var stream = _stream;
        if (stream is null || !IsConnected)
            return;

        var payload = Encoding.UTF8.GetBytes(command + "\r\n");
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch { /* a read loop észleli a leesést */ }
        finally { _sendLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var stream = _stream!;
        var buffer = new byte[8192];

        // FONTOS: BÁJT-szinten gyűjtünk sorvégig, és csak a teljes sort dekódoljuk
        // UTF-8-cal. Bájtonkénti (char) cast Latin-1 lenne, ami szétvágná a többbájtos
        // UTF-8 karaktereket (ékezetek → "Ã¡"-szerű szemét). A soremelés (0x0A) és a
        // CR (0x0D) sosem fordul elő UTF-8 többbájtos sorozat belsejében (a folytató
        // bájtok mind >= 0x80), ezért bájtra hasítani biztonságos.
        var line = new List<byte>(1024);

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0)
                    break;

                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b == (byte)'\n')
                    {
                        if (line.Count > 0)
                        {
                            var text = Encoding.UTF8.GetString(line.ToArray()).Trim();
                            line.Clear();
                            if (text.Length > 0)
                                HandleMessage(text);
                        }
                    }
                    else if (b != (byte)'\r')
                    {
                        line.Add(b);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("heos", out var heos))
                return;

            string command = heos.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() ?? "" : "";
            string message = heos.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";

            switch (command)
            {
                case "player/get_players":
                    if (root.TryGetProperty("payload", out var players) &&
                        players.ValueKind == JsonValueKind.Array &&
                        players.GetArrayLength() > 0 &&
                        players[0].TryGetProperty("pid", out var pidEl))
                    {
                        _pid = pidEl.GetInt64();
                        // Most már van pid: kérjük a kezdeti now-playing + állapotot.
                        _ = RefreshNowPlayingAsync();
                        _ = SendRawAsync($"heos://player/get_play_state?pid={_pid}");
                        _ = RefreshPlayModeAsync();
                    }
                    break;

                case "player/get_play_mode":
                case "player/set_play_mode":
                    PlayModeChanged?.Invoke(ParseField(message, "repeat"), ParseField(message, "shuffle"));
                    break;

                case "player/get_queue":
                    if (root.TryGetProperty("payload", out var queue) && queue.ValueKind == JsonValueKind.Array)
                    {
                        var items = new List<QueueItem>(queue.GetArrayLength());
                        foreach (var el in queue.EnumerateArray())
                            items.Add(QueueItem.FromPayload(el));
                        QueueReceived?.Invoke(items);
                    }
                    break;

                case "player/get_now_playing_media":
                    if (root.TryGetProperty("payload", out var media) && media.ValueKind == JsonValueKind.Object)
                        NowPlayingChanged?.Invoke(NowPlaying.FromPayload(media));
                    else
                        NowPlayingChanged?.Invoke(null);
                    break;

                case "player/get_play_state":
                case "player/set_play_state":
                    var st = ParseField(message, "state");
                    if (!string.IsNullOrEmpty(st))
                        PlayStateChanged?.Invoke(st);
                    break;

                // Push-események: új szám / állapotváltás → frissítünk.
                case "event/player_now_playing_changed":
                    _ = RefreshNowPlayingAsync();
                    break;
                case "event/player_state_changed":
                    var evState = ParseField(message, "state");
                    if (!string.IsNullOrEmpty(evState))
                        PlayStateChanged?.Invoke(evState);
                    break;

                // Élőben mérve: "pid=…&cur_pos=77000&duration=167000" (ms), ~5 mp-enként.
                case "event/player_progress_changed":
                case "event/player_now_playing_progress":
                    if (int.TryParse(ParseField(message, "cur_pos"), out int pos) &&
                        int.TryParse(ParseField(message, "duration"), out int dur))
                        ProgressChanged?.Invoke(pos, dur);
                    break;
            }
        }
        catch
        {
            // Hibás/részleges JSON – egyszerűen eldobjuk.
        }
    }

    /// <summary>A HEOS "message" mezője URL-query formátumú: pl. "pid=-101&amp;state=play".</summary>
    private static string ParseField(string message, string key)
    {
        foreach (var part in message.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.AsSpan(0, eq).SequenceEqual(key))
                return part[(eq + 1)..];
        }
        return "";
    }
}

/// <summary>Now-playing média a HEOS-ból.</summary>
public sealed record NowPlaying(string Song, string Artist, string Album, string ImageUrl)
{
    public static NowPlaying FromPayload(JsonElement p)
    {
        string Get(string name) =>
            p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
        return new NowPlaying(Get("song"), Get("artist"), Get("album"), Get("image_url"));
    }
}

/// <summary>Egy tétel a lejátszási sorból.</summary>
public sealed class QueueItem
{
    public int Qid { get; init; }
    public string Song { get; init; } = "";
    public string Artist { get; init; } = "";

    /// <summary>„3. My way — Frank Sinatra" – egy soros felirat a listához.</summary>
    public string Display =>
        Artist.Length > 0 ? $"{Qid}. {Song} — {Artist}" : $"{Qid}. {Song}";

    public static QueueItem FromPayload(JsonElement p)
    {
        string Get(string name) =>
            p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
        int qid = p.TryGetProperty("qid", out var q) && q.TryGetInt32(out int v) ? v : 0;
        return new QueueItem { Qid = qid, Song = Get("song"), Artist = Get("artist") };
    }
}
