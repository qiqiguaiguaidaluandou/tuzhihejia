using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TZHJ.Gateway.AntiCorruption;

/// <summary>
/// 复刻对方 JWTUtil.CreateJWTToken()：HS256 签名的 JWT，放进 Authorization 头。
/// 这里手写 base64url + HMACSHA256（不走 Microsoft.IdentityModel），原因：
///   1) 与对方使用的 jwt-dotnet 输出格式完全一致；
///   2) 不受 Microsoft 库对对称密钥长度的强制校验影响（对方 secret 可能较短）。
/// claims 与对方一致：iss / exp(+24h) / sub("") / aud("") / nbf(-10m)，时间戳为 10 位秒级。
/// nbf 提前 10 分钟容忍两端时钟偏差；exp 延后 24 小时，token 在有效期内复用。
///
/// 本类注册为单例，被 EBS 取数 / PLM 富化 / SRM 回传共用。Create() 会缓存上一次签发的
/// token 并复用，直到距 exp 不足 <see cref="RefreshSkew"/> 时才重签，避免每次调接口都做一次
/// HMACSHA256 计算。提前 RefreshSkew 重签，确保返回的 token 在后续请求飞行途中不会过期。
/// </summary>
public sealed class EbsTokenProvider
{
    /// <summary>距 exp 不足此时长即视为需要重签的安全余量。</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly EbsOptions _opt;
    private readonly object _gate = new();

    private string? _cached;
    private DateTimeOffset _expiresAt;

    public EbsTokenProvider(EbsOptions opt) => _opt = opt;

    /// <summary>返回有效的鉴权 token；未过期则复用缓存，否则重新签发。</summary>
    public string Create()
    {
        // 临界区极小（命中缓存仅做一次比较），单锁即可，无需双检快路径。
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cached is { } cached && now < _expiresAt - RefreshSkew)
                return cached;

            var expiresAt = now.AddHours(24);
            var token = Sign(now, expiresAt);
            _cached = token;
            _expiresAt = expiresAt;
            return token;
        }
    }

    private string Sign(DateTimeOffset now, DateTimeOffset expiresAt)
    {
        var header = new Dictionary<string, object> { ["typ"] = "JWT", ["alg"] = "HS256" };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = _opt.JwtIssuer,
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["sub"] = "",
            ["aud"] = "",
            ["nbf"] = now.AddMinutes(-10).ToUnixTimeSeconds(),
        };

        var headerB64 = B64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadB64 = B64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{headerB64}.{payloadB64}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_opt.JwtSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));

        return $"{signingInput}.{B64Url(signature)}";
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
