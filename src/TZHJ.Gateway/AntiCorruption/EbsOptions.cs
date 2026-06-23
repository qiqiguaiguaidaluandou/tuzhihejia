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

    /// <summary>
    /// 核价的固定期望组别（完整名，作为文件夹/权限键）。无论某组当天有无数据，都会为每个期望组建批次文件夹+表格，
    /// 空组只是表里没数据行——便于区分"采过但没数据"与"没采到"。
    /// 数据行按"组N"前缀匹配到这里的完整名；匹配不到的（意外组）按其原始组名照常落盘，不丢数据。
    /// </summary>
    public string[] PricingGroups { get; set; } =
    {
        "组1（模组、先进激光、芯片电阻、合能、奥超、奥杰）",
        "组2（绿能）",
    };
}
