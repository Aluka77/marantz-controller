using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MarantzController;

// ---------------------------------------------------------------------------
//  Finding Denon/Marantz receivers on the local network.
//
//  1. SSDP M-SEARCH, sent separately from EVERY local IPv4 address. With a VPN
//     adapter present (Tailscale, WireGuard) Windows routes the multicast packet
//     out of the VPN, and a single unbound socket gets no answer at all.
//  2. The UPnP description (LOCATION) tells the manufacturer and model; only
//     Denon/Marantz devices are kept.
//  3. TCP 23 must be open – HEOS speakers answer SSDP too, but have no telnet.
//
//  Approach inspired by OxygenLack/Denon-Marantz-AVR-Dashboard (MIT).
//  Only call this while NOT connected: the receiver accepts a single telnet client,
//  and the port probe would compete with the live connection.
// ---------------------------------------------------------------------------

public sealed record DiscoveredReceiver(string IpAddress, string Model)
{
    public string Display => $"{Model}  ·  {IpAddress}";
}

public static class ReceiverDiscovery
{
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);

    // ACT-Denon is present on every HEOS-era Denon/Marantz unit; MediaRenderer is a
    // fallback for older network models.
    private static readonly string[] SearchTargets =
    {
        "urn:schemas-denon-com:device:ACT-Denon:1",
        "urn:schemas-upnp-org:device:MediaRenderer:1",
    };

    public static async Task<IReadOnlyList<DiscoveredReceiver>> FindAsync(CancellationToken ct = default)
    {
        var locations = await SearchAsync(TimeSpan.FromSeconds(3), ct);

        var checks = locations.Select(kv => IdentifyAsync(kv.Key, kv.Value, ct));
        var found = await Task.WhenAll(checks);

        return found.OfType<DiscoveredReceiver>()
                    .OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>SSDP search on all interfaces. Result: device IP → description URL.</summary>
    private static async Task<Dictionary<string, string>> SearchAsync(TimeSpan listen, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var sockets = new List<UdpClient>();

        foreach (var local in LocalIPv4Addresses())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                sockets.Add(udp);

                foreach (var st in SearchTargets)
                {
                    var msg = Encoding.ASCII.GetBytes(
                        "M-SEARCH * HTTP/1.1\r\n" +
                        "HOST: 239.255.255.250:1900\r\n" +
                        "MAN: \"ssdp:discover\"\r\n" +
                        "MX: 2\r\n" +
                        $"ST: {st}\r\n\r\n");
                    await udp.SendAsync(msg, msg.Length, SsdpEndpoint);
                }
            }
            catch (SocketException)
            {
                // Adapter that cannot send multicast (e.g. a tunnel) – skip it.
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(listen);

        await Task.WhenAll(sockets.Select(async udp =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(timeout.Token);
                    var text = Encoding.ASCII.GetString(packet.Buffer);
                    var loc = Regex.Match(text, @"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (!loc.Success)
                        continue;
                    lock (result)
                        result.TryAdd(packet.RemoteEndPoint.Address.ToString(), loc.Groups[1].Value);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            finally
            {
                udp.Dispose();
            }
        }));

        return result;
    }

    /// <summary>Denon/Marantz with an open telnet port → receiver; anything else → null.</summary>
    private static async Task<DiscoveredReceiver?> IdentifyAsync(string ip, string location, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                // Some models serve the description over HTTPS with a self-signed certificate.
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            var xml = XDocument.Parse(await http.GetStringAsync(location, ct));

            string? Field(string name) =>
                xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

            var manufacturer = Field("manufacturer") ?? "";
            if (!manufacturer.Contains("Denon", StringComparison.OrdinalIgnoreCase) &&
                !manufacturer.Contains("Marantz", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!await IsPortOpenAsync(ip, 23, TimeSpan.FromSeconds(1.5), ct))
                return null;

            var model = Field("modelName") ?? Field("friendlyName") ?? manufacturer;
            return new DiscoveredReceiver(ip, model);
        }
        catch
        {
            return null;   // unreachable, not XML, timeout …
        }
    }

    private static async Task<bool> IsPortOpenAsync(string ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await tcp.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork &&
                        !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct();
}
