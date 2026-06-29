using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Core.Contracts;

/// <summary>
/// 本地批次存储（客户端内部，非外部网关）。软件视图 = 本地文件夹视图：批次列表/状态直接映射
/// {待处理|已处理} 目录，文件夹即真相源。负责落本地、读 xlsx+manifest、暂存写回、待处理→已处理 移目录、
/// 异常池、完整性校验。
/// </summary>
public interface ILocalBatchStore
{
    /// <summary>本地存储根目录。</summary>
    string Root { get; }

    /// <summary>列出某流程/工号/位置下的所有批次（扫描文件夹 + manifest，不读全部行内容）。</summary>
    Task<IReadOnlyList<Batch>> ListBatchesAsync(FlowType flow, string employeeId, BatchLocation location, CancellationToken ct = default);

    /// <summary>读取一个批次的完整内容（清单表格.xlsx + manifest + 图纸有无校验）。</summary>
    Task<Batch?> GetBatchAsync(FlowType flow, string employeeId, BatchLocation location, string folderName, CancellationToken ct = default);

    /// <summary>把取数结果落本地：在「待处理」建批次目录，写 xlsx + 图纸 + manifest。返回落地后的批次。</summary>
    Task<Batch> WriteFetchedBatchAsync(FetchResult fetched, CancellationToken ct = default);

    /// <summary>暂存：把网格里的行值/行状态写回该批次的 xlsx + manifest。</summary>
    Task SaveBatchAsync(Batch batch, CancellationToken ct = default);

    /// <summary>整批回传成功后，把批次目录从「待处理」移入「已处理」。</summary>
    Task MoveToDoneAsync(Batch batch, CancellationToken ct = default);

    /// <summary>把挂起异常的行追加进「异常待跟进」池。</summary>
    Task AddExceptionsAsync(FlowType flow, string employeeId, IEnumerable<ExceptionItem> items, CancellationToken ct = default);

    /// <summary>列出异常待跟进池。</summary>
    Task<IReadOnlyList<ExceptionItem>> ListExceptionsAsync(FlowType flow, string employeeId, CancellationToken ct = default);

    /// <summary>从异常待跟进池移除一条（按来源批次 + 行键定位）。单行补回传成功后调用。</summary>
    Task RemoveExceptionAsync(FlowType flow, string employeeId, string sourceBatch, string rowKey, CancellationToken ct = default);

    /// <summary>判断某窗口本地是否已有批次（待处理或已处理），供登录补拉判断"漏数"用。</summary>
    bool BatchExists(FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd);

    // --- Remote-First 镜像同步支持 ---

    /// <summary>确保批次文件夹及其 manifest 存在。</summary>
    Task EnsureBatchFolderAsync(FlowType flow, string groupName, string batchId, BatchLocation location, CancellationToken ct = default);

    /// <summary>写入同步下来的文件字节。</summary>
    Task WriteSyncFileAsync(FlowType flow, string groupName, string batchId, BatchLocation location, string fileName, byte[] content, CancellationToken ct = default);

    /// <summary>把重新获取到的图纸文件名并入指定行的 manifest 图纸清单（去重，不动行状态/字段值）。补图后关联用。</summary>
    Task AddRowDrawingsAsync(FlowType flow, string groupName, string batchId, BatchLocation location, string rowKey, IEnumerable<string> fileNames, CancellationToken ct = default);

    /// <summary>全量覆盖异常池。</summary>
    Task OverwriteExceptionsAsync(FlowType flow, string groupName, IEnumerable<ExceptionItem> items, CancellationToken ct = default);
}
