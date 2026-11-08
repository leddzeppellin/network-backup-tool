using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Nbt.Core;

public sealed record DiscoveredReceiver(string Name, string Address, int Port)
{
    public override string ToString() => $"{Name}  ({Address})";
}

/// <summary>Broadcast UDP na rede local para achar receptores sem digitar IP.</summary>
public static class Discovery
{
    public const string Request = "NBT?";
    public const string Reply = "NBT!";

    public static async Task<List<DiscoveredReceiver>> FindAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredReceiver>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var payload = Encoding.UTF8.GetBytes(Request);

        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var addr in LocalIPv4())
        {
            var ip = addr.Address.GetAddressBytes();
            var mask = addr.IPv4Mask.GetAddressBytes();
            var bc = new byte[4];
            for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
            targets.Add(new IPAddress(bc));
        }

        foreach (var t in targets)
        {
            try { await udp.SendAsync(payload, new IPEndPoint(t, Protocol.DiscoveryPort), ct); } catch (SocketException) { }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var res = await udp.ReceiveAsync(timeout.Token);
                var text = Encoding.UTF8.GetString(res.Buffer);
                if (!text.StartsWith(Reply)) continue;
                var parts = text[Reply.Length..].Split('|');
                if (parts.Length < 2 || !int.TryParse(parts[1], out var port)) continue;
                var address = res.RemoteEndPoint.Address.ToString();
                found[address] = new DiscoveredReceiver(parts[0], address, port);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }

        return found.Values.OrderBy(r => r.Name).ToList();
    }

    public static IEnumerable<UnicastIPAddressInformation> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

    public static IEnumerable<(string Interface, string Address, long SpeedBps)> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => (n.Name, a.Address.ToString(), n.Speed)));
}
