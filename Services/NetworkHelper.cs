using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NPlayerServer.Services;

public static class NetworkHelper
{
    private static readonly string[] VirtualHints =
    {
        "vmware", "virtualbox", "hyper-v", "vethernet", "loopback", "bluetooth",
        "tap-", "tap0", "wsl", "docker", "vpn", "zerotier", "tailscale",
    };

    public static List<IPAddress> GetIPv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var name = ni.Name.ToLowerInvariant();
                var desc = ni.Description.ToLowerInvariant();
                if (VirtualHints.Any(h => name.Contains(h) || desc.Contains(h))) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.") || s.StartsWith("127.")) continue;
                    result.Add(ua.Address);
                }
            }
        }
        catch
        {
            // 枚举失败时返回空列表，界面会显示提示
        }

        return result;
    }

    public static string PrimaryAddress()
    {
        var addresses = GetIPv4Addresses();
        var preferred = addresses.FirstOrDefault(a => IsPrivate(a)) ?? addresses.FirstOrDefault();
        return preferred?.ToString() ?? "127.0.0.1";
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] switch
        {
            10 => true,
            172 => b[1] is >= 16 and <= 31,
            192 => b[1] == 168,
            _ => false,
        };
    }

    public static string BuildUrl(string address, int port) => $"http://{address}:{port}";
}
