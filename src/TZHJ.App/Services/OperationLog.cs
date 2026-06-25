using System.Diagnostics;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Infrastructure.Net;

namespace TZHJ.App.Services;

/// <summary>
/// 操作日志记录助手：统一拼装记录（操作按钮/电脑IP/时间/表单名称）并 fire-and-forget 上报。
/// 记录失败只吞掉并写调试输出——绝不打断或拖慢正在进行的业务操作（回传/补回传）。
/// </summary>
public static class OperationLog
{
    public static void Record(
        IOperationLogGateway gateway, string operation, string formName, FlowType flow, string employeeId)
    {
        var entry = new OperationLogEntry
        {
            Operation = operation,
            ClientIp = MachineInfo.LocalIPv4(),
            FormName = formName,
            OperatedAt = DateTime.Now,
            Flow = flow,
            EmployeeId = employeeId, // 真链路下后端会以令牌盖章覆盖；离线 Mock 用此值。
        };

        _ = Task.Run(async () =>
        {
            try { await gateway.RecordAsync(entry); }
            catch (Exception ex) { Debug.WriteLine($"[操作日志] 上报失败（已忽略）：{ex.Message}"); }
        });
    }
}
