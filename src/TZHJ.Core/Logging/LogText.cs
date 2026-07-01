using TZHJ.Core.Enums;

namespace TZHJ.Core.Logging;

/// <summary>
/// 日志显示词表：动作英文值 → 中文名、payload 片段美化。后台管理（全量审计）与
/// 操作员「我的操作」共用同一套词表，避免两处翻译走偏。
/// </summary>
public static class LogText
{
    /// <summary>动作值（写入 ActivityLogs 的英文）→ 中文显示名。既驱动后台筛选下拉，也用于列表渲染。</summary>
    public static readonly (string Value, string Label)[] Actions =
    {
        ("Login", "用户登录"),
        ("AdminLogin", "后台登录"),
        ("ChangePassword", "修改密码"),
        ("UpdateRow", "修改数据行"),
        ("Suspend", "挂起异常"),
        ("Resolve", "处理异常"),
        ("RefetchDrawing", "重新获取图纸"),
        ("Submit", "提交回传"),
        ("Ingest", "数据导入"),
        ("Behavior", "用户操作"),
        ("AdminCreateUser", "新建用户"),
        ("AdminResetPassword", "重置密码"),
        ("AdminSetActive", "启用/停用用户"),
        ("AdminSetUserRoles", "分配角色"),
        ("AdminCreateRole", "新建角色"),
        ("AdminUpdateRole", "编辑角色"),
        ("AdminDeleteRole", "删除角色"),
    };

    /// <summary>
    /// 操作员「我的操作」可见的动作：本人的业务操作 + 登录/改密。
    /// 刻意排除系统导入(Ingest)、客户端行为埋点(Behavior)、后台管理动作(Admin*)——那些属于全量审计，只在后台看。
    /// </summary>
    public static readonly string[] OperatorActions =
    {
        "Submit", "UpdateRow", "Suspend", "Resolve", "RefetchDrawing", "Login", "ChangePassword",
    };

    /// <summary>动作英文值 → 中文；未知动作原样返回，避免吞掉新增类型。</summary>
    public static string ActionLabel(string action)
    {
        foreach (var (val, label) in Actions)
            if (val == action) return label;
        return action;
    }

    /// <summary>流程中文名。</summary>
    public static string FlowLabel(FlowType flow) => flow == FlowType.Pricing ? "核价" : "挑图";

    /// <summary>回传目标：核价→SRM，挑图→EBS（与 /submit 端点一致）。</summary>
    public static string SubmitTarget(FlowType? flow) => (flow ?? FlowType.Pricing) == FlowType.Pricing ? "SRM" : "EBS";

    /// <summary>提交回传的动作显示名：区分「提交回传/重新回传」并带上回传目标（如「提交回传 → SRM」）。</summary>
    public static string SubmitLabel(FlowType? flow, bool isRetry) =>
        $"{(isRetry ? "重新回传" : "提交回传")} → {SubmitTarget(flow)}";

    /// <summary>该条 Submit 日志是否为「重新回传」（异常行重传）。以 payload 里的标记判定。</summary>
    public static bool IsResubmit(string? payload) => payload is not null && payload.Contains("重新回传");

    /// <summary>
    /// 把历史英文 payload 里的已知标签/键翻译成中文（后台「详情」列用）。
    /// 顺序有意义：先长键（empId=）后短键（id=）。命中不到的原样透传，不丢信息。
    /// </summary>
    public static string Prettify(string payload) => payload
        .Replace("empId=", "工号=")
        .Replace("Target:", "回传目标：")
        .Replace("Row:", "数据行：")
        .Replace("Reason:", "原因：")
        .Replace("Files:", "文件：")
        .Replace("admin=", "管理员=")
        .Replace("active=", "启用=")
        .Replace("roles=", "角色=")
        .Replace("ranges=", "数据范围数=")
        .Replace("name=", "名称=")
        .Replace("id=", "ID=")
        .Replace("=True", "=是")
        .Replace("=False", "=否")
        .Replace(", ", "；");
}
