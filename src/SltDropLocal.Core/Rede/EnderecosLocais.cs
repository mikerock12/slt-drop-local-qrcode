using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SltDropLocal.Core.Rede;

public sealed record EnderecoLocal(IPAddress Ip, string NomePlaca, string Descricao, bool Recomendado)
{
    public override string ToString() => $"{Ip}  —  {NomePlaca}";
}

/// <summary>
/// Descobre o IP do PC na rede Wi-Fi/LAN. Um PC costuma ter várias placas (Wi-Fi,
/// cabo, VPN, Hyper-V, WSL, VirtualBox...); o celular só alcança a da rede real,
/// que é a que tem gateway e não é virtual. Essa vem primeiro.
/// </summary>
public static class EnderecosLocais
{
    private static readonly string[] SinaisDeVirtual =
    [
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "docker",
        "tap-", "tap ", "tun", "vpn", "loopback", "bluetooth", "npcap", "pseudo",
        "zerotier", "hamachi", "tailscale", "wireguard", "openvpn",
    ];

    public static IReadOnlyList<EnderecoLocal> Listar()
    {
        var lista = new List<(EnderecoLocal endereco, int nota)>();
        NetworkInterface[] placas;
        try { placas = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }

        foreach (var placa in placas)
        {
            if (placa.OperationalStatus != OperationalStatus.Up) continue;
            if (placa.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try { props = placa.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            var temGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var texto = (placa.Name + " " + placa.Description).ToLowerInvariant();
            var virtualizada = SinaisDeVirtual.Any(texto.Contains);

            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                if (EhLinkLocal(ip)) continue;

                var nota = 0;
                if (temGateway) nota += 100;
                if (!virtualizada) nota += 50;
                if (placa.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) nota += 10;
                if (placa.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet) nota += 8;
                if (EhPrivado(ip)) nota += 5;

                lista.Add((new EnderecoLocal(ip, placa.Name, placa.Description, false), nota));
            }
        }

        var ordenada = lista.OrderByDescending(x => x.nota).ThenBy(x => x.endereco.Ip.ToString()).ToList();
        return [.. ordenada.Select((x, i) => i == 0 && x.nota >= 150 ? x.endereco with { Recomendado = true } : x.endereco)];
    }

    /// <summary>169.254.x.x: o Windows se atribui quando não pegou IP do roteador; o celular não alcança.</summary>
    internal static bool EhLinkLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    internal static bool EhPrivado(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
