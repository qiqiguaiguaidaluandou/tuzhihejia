namespace TZHJ.Infrastructure.Options;

/// <summary>
/// 网关选项（来自 appsettings.json 的 "Http" 节）。客户端唯一链路：连后端 TZHJ.Gateway。
/// </summary>
public sealed class HttpOptions
{
    /// <summary>后端无状态网关根地址（引导用：先有它才能调 /config，见开发文档 D3）。</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>单次请求超时（秒）。图纸逐张下载用单请求，故按单文件而非整批计。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>本地数据根目录的兜底值（登录后由 /config 下发的 ClientConfig.LocalRoot 覆盖）。</summary>
    public string LocalRoot { get; set; } = "TZHJ_Data";
}
