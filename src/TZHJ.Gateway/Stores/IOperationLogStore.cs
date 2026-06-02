using TZHJ.Core.Contracts.Http;

namespace TZHJ.Gateway.Stores;

/// <summary>
/// 用户操作日志存储。与回传审计（<see cref="IAuditStore"/>）分开：审计只记成功回传供补拉判据，
/// 这里记"谁在哪台电脑、什么时间、点了哪个按钮、操作了哪张表单"，供管理员追溯、操作员自查。
/// 落文件（JSONL 按行追加），管理员可直接在服务器侧打开查看；上线可改落 PostgreSQL。
/// </summary>
public interface IOperationLogStore
{
    /// <summary>追加一条操作日志。</summary>
    void Append(OperationLogEntry entry);

    /// <summary>查某工号的全部操作记录（新到旧）。</summary>
    IReadOnlyList<OperationLogEntry> ListByEmployee(string employeeId);
}
