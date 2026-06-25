using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TZHJ.Infrastructure.Net;

/// <summary>本机信息：取操作电脑的局域网 IPv4（随请求上报为"操作电脑IP"，见 ClientIpHandler）。</summary>
public static class MachineInfo
{
    // 单台机器一个会话内 IP 基本不变，首个成功值缓存复用；为空（暂无网络）时下次再解析。
    private static string? _cached;

    /// <summary>
    /// 取本机局域网 IPv4。优先选"已启用、非回环、非隧道"网卡上的单播地址；
    /// 取不到时回退到机器名解析。取不到返回 null（不影响请求，IP 留空由服务端回退连接 IP）。
    /// </summary>
    public static string? LocalIPv4() => _cached ??= Resolve();

    private static string? Resolve()
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
