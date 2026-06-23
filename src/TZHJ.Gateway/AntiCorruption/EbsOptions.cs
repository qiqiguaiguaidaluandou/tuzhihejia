namespace TZHJ.Gateway.AntiCorruption;

/// <summary>
/// EBS 取数接口配置（来自 appsettings 的 "Ebs" 段）。
/// 真实地址/密钥放 appsettings.local.json（不进 git）。Enabled=false 时仍用 FakeDataSource。
/// </summary>
public sealed class EbsOptions
{
    /// <summary>是否启用真实 EBS 取数。false（默认）→ 继续用 FakeDataSource 造数。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>图纸核价接口 URL（POST）。两个接口地址不同，分别配置。</summary>
    public string PricingUrl { get; set; } = "";

    /// <summary>机加中心挑图接口 URL（POST）。</summary>
    public string DrawingUrl { get; set; } = "";

    /// <summary>生成鉴权 JWT 的密钥（对应对方的 iPaaS_JWT_secret）。</summary>
    public string JwtSecret { get; set; } = "";

    /// <summary>鉴权 JWT 的发行人 iss（对应对方的 iPaaS_JWT_iss）。</summary>
    public string JwtIssuer { get; set; } = "";

    /// <summary>Authorization 头前缀。对方要求带前缀 → "Bearer"。</summary>
    public string AuthScheme { get; set; } = "Bearer";

    /// <summary>图纸核价接口码（固定值）。</summary>
    public string PricingIfaceCode { get; set; } = "CUX_AI_DRW_COST";

    /// <summary>机加中心挑图接口码（固定值）。</summary>
    public string DrawingIfaceCode { get; set; } = "CUX_AI_MACH_DRW";
}
