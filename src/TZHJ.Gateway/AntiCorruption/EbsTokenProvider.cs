using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TZHJ.Gateway.AntiCorruption;

/// <summary>
/// 复刻对方 JWTUtil.CreateJWTToken()：HS256 签名的 JWT，放进 Authorization 头。
/// 这里手写 base64url + HMACSHA256（不走 Microsoft.IdentityModel），原因：
///   1) 与对方使用的 jwt-dotnet 输出格式完全一致；
///   2) 不受 Microsoft 库对对称密钥长度的强制校验影响（对方 secret 可能较短）。
/// claims 与对方一致：iss / exp(+15m) / sub("") / aud("") / nbf(-10m)，时间戳为 10 位秒级。
/// nbf 提前 10 分钟、exp 延后 15 分钟的宽容窗用于容忍两端时钟偏差。
/// </summary>
public sealed class EbsTokenProvider
{
    private readonly EbsOptions _opt;

    public EbsTokenProvider(EbsOptions opt) => _opt = opt;

    public string Create()
    {
        var now = DateTimeOffset.UtcNow;

        var header = new Dictionary<string, object> { ["typ"] = "JWT", ["alg"] = "HS256" };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = _opt.JwtIssuer,
            ["exp"] = now.AddMinutes(15).ToUnixTimeSeconds(),
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
