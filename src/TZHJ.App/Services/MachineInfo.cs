using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TZHJ.App.Services;

/// <summary>本机信息：取操作电脑的局域网 IPv4（写入操作日志的"操作电脑IP"）。</summary>
public static class MachineInfo
{
    /// <summary>
    /// 取本机局域网 IPv4。优先选"已启用、非回环、非虚拟"网卡上的单播地址；
    /// 取不到时回退到机器名解析。取不到返回 null（不影响日志记录，IP 留空）。
    /// </summary>
    public static string? LocalIPv4()
    {
        try
        {
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(ua => ua.Address)
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork
                                      && !IPAddress.IsLoopback(ip));
            if (candidate is not null) return candidate.ToString();

            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
