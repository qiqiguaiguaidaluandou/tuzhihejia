using System.Net;
using System.Net.Http;

namespace TZHJ.App.Services;

/// <summary>
/// 把网关抛出的技术异常翻成操作员看得懂、能据此行动的中文提示。
/// 断网/服务不可达、超时、令牌失效、服务器报错应区别对待，
/// 而不是把 HttpRequestException 的英文堆栈原样弹给操作员。
/// </summary>
public static class FriendlyError
{
    /// <summary>
    /// 生成「{动作}失败：{原因}。」。<paramref name="action"/> 给上下文（如「回传」「补拉」「登录」），
    /// 原因按异常类型归类——具体类别取不到时退回异常自带消息。
    /// </summary>
    public static string Describe(Exception ex, string action) => $"{action}失败：{Reason(ex)}。";

    private static string Reason(Exception ex) => ex switch
    {
        // HttpClient 超时抛 TaskCanceledException（OperationCanceledException 子类）；
        // 这些调用点不传用户取消令牌，故此处只可能是超时。
        OperationCanceledException => "请求超时，请检查网络后稍后重试",

        // StatusCode 为空 = 连接层失败（DNS/拒绝/断网），尚未拿到任何响应。
        HttpRequestException { StatusCode: null } => "无法连接服务器，请检查网络连接后重试",

        // 令牌失效/无权限：引导重新登录而非让操作员困惑于「401」。
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            => "登录状态已失效，请退出后重新登录",

        // 其余 HTTP 状态码：服务器侧问题，给出码值便于联系管理员定位。
        HttpRequestException { StatusCode: { } code } => $"服务器返回错误（{(int)code}），请稍后重试或联系管理员",

        _ => ex.Message,
    };
}
