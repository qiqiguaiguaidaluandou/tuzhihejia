using TZHJ.Infrastructure.Net;

namespace TZHJ.Infrastructure.Gateways.Http;

/// <summary>
/// 给每个出站请求带上 X-Client-Ip = 本机局域网 IPv4，作为操作日志里的"操作电脑IP"。
/// 服务端连接 IP 在同机/反向代理/NAT 下都不等于操作员真实工位 IP，故由客户端自报本机 IP。
/// 取不到本机 IP 时不加头，由服务端回退到连接 IP。
/// </summary>
public sealed class ClientIpHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ip = MachineInfo.LocalIPv4();
        if (!string.IsNullOrEmpty(ip) && !request.Headers.Contains("X-Client-Ip"))
            request.Headers.Add("X-Client-Ip", ip);

        return base.SendAsync(request, cancellationToken);
    }
}
