using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;

namespace TZHJ.Gateway.Stores;

public interface IServerBatchStore
{
    /// <summary>
    /// 获取服务器存储根路径。
    /// </summary>
    string Root { get; }

    /// <summary>
    /// 将获取到的数据落盘到服务器。
    /// </summary>
    Task SaveBatchAsync(FetchResponse fetched, string groupName, IEnumerable<(string FileName, byte[] Content)> drawings, CancellationToken ct = default);

    /// <summary>
    /// 向已存在的批次目录追加图纸文件（重新取图补图用，不重写 Excel）。
    /// </summary>
    Task AppendDrawingsAsync(FlowType flow, string groupName, string batchId, IEnumerable<(string FileName, byte[] Content)> drawings, CancellationToken ct = default);

    /// <summary>
    /// 获取批次目录下的文件清单。
    /// </summary>
    Task<List<SyncFileMeta>> ListFilesAsync(FlowType flow, string groupName, string batchId, CancellationToken ct = default);

    /// <summary>
    /// 打开文件流进行下载。
    /// </summary>
    Stream? OpenFile(FlowType flow, string groupName, string batchId, string fileName);

    /// <summary>
    /// 更新 Excel 中的一行数据。
    /// </summary>
    Task UpdateExcelRowAsync(FlowType flow, string groupName, string batchId, string rowKey, Dictionary<string, string?> values, CancellationToken ct = default);

    /// <summary>
    /// 移动批次到完成目录（方案二下通常为 no-op）。
    /// </summary>
    Task MoveToDoneAsync(FlowType flow, string groupName, string batchId, CancellationToken ct = default);

    /// <summary>
    /// 从服务器磁盘彻底删除批次物理文件夹。
    /// </summary>
    Task DeleteBatchAsync(FlowType flow, string groupName, string batchId, CancellationToken ct = default);
}
