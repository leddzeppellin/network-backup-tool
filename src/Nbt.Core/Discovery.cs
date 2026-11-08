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
        IgnoreConnReset(udp.Client);
        var payload = Encoding.UTF8.GetBytes(Request);

        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var addr in LocalIPv4())
        {
            var ip = addr.Address.GetAddressBytes();
            var mask = addr.IPv4Mask.GetAddressBytes();
            // Máscara /32 (Tailscale, VPNs): o "broadcast" seria o próprio IP; não há vizinhos ali.
            if (mask.All(b => b == 255)) continue;
            var bc = new byte[4];
            for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
            targets.Add(new IPAddress(bc));
        }

        // Repete o pedido: Wi-Fi às vezes perde broadcast.
        for (int round = 0; round < 3; round++)
        {
            foreach (var t in targets)
            {
                try { await udp.SendAsync(payload, new IPEndPoint(t, Protocol.DiscoveryPort), ct); } catch (SocketException) { }
            }
            if (round < 2) await Task.Delay(150, ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                UdpReceiveResult res;
                try { res = await udp.ReceiveAsync(timeout.Token); }
                catch (SocketException) { continue; } // erro de um destino não pode encerrar a busca
                var text = Encoding.UTF8.GetString(res.Buffer);
                if (!text.StartsWith(Reply)) continue;
                var parts = text[Reply.Length..].Split('|');
                if (parts.Length < 2 || !int.TryParse(parts[1], out var port)) continue;
                var address = res.RemoteEndPoint.Address.ToString();
                found[address] = new DiscoveredReceiver(parts[0], address, port);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        // O mesmo computador pode responder por várias placas (virtuais inclusive): fica um por nome,
        // de preferência o endereço que está na mesma rede de uma placa real deste PC.
        var real = LocalIPv4().Where(a => !IsVirtual(InterfaceOf(a))).ToList();
        bool SameLan(string address) => IPAddress.TryParse(address, out var ip) && real.Any(a => SameSubnet(ip, a));
        return found.Values
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => SameLan(r.Address)).First())
            .OrderBy(r => r.Name)
            .ToList();
    }

    private static readonly string[] VirtualNames =
        ["vEthernet", "WSL", "Hyper-V", "docker", "br-", "veth", "virbr", "vmnet", "VirtualBox", "Loopback"];

    /// <summary>Adaptadores virtuais (Hyper-V, WSL, Docker, VMs) que não levam à rede local de verdade.</summary>
    public static bool IsVirtual(string interfaceName) =>
        VirtualNames.Any(v => interfaceName.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static string InterfaceOf(UnicastIPAddressInformation addr) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(addr.Address)))?.Name ?? "";

    private static bool SameSubnet(IPAddress ip, UnicastIPAddressInformation local)
    {
        var a = ip.GetAddressBytes();
        var b = local.Address.GetAddressBytes();
        var m = local.IPv4Mask.GetAddressBytes();
        if (a.Length != 4 || b.Length != 4) return false;
        for (int i = 0; i < 4; i++)
            if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        return true;
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
                .Select(a => (n.Name, a.Address.ToString(), SpeedOf(n))));

    // No macOS a velocidade da placa não é informada (lança PlatformNotSupportedException).
    private static long SpeedOf(NetworkInterface n)
    {
        try { return n.Speed; } catch { return -1; }
    }

    /// <summary>
    /// No Windows, um "porta inalcançável" (ICMP) recebido por causa de um envio anterior faz o
    /// próximo Receive do socket UDP falhar com ConnectionReset. Isso derrubava a busca inteira
    /// quando um dos destinos (ex.: o próprio PC) não estava ouvindo.
    /// </summary>
    internal static void IgnoreConnReset(Socket s)
    {
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try { s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
    }
}
