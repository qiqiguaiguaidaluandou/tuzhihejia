using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Core.Contracts;

/// <summary>
/// 取数网关。负责与后端 Gateway 通信。
/// </summary>
public interface IDataGateway
{
    /// <summary>按时间窗取数（原始流程）。</summary>
    Task<FetchResult> FetchBatchAsync(FetchRequest request, CancellationToken ct = default);

    // --- Remote-First 新增 ---

    /// <summary>获取服务器同步清单。</summary>
    Task<List<BatchCatalogItem>> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>下载服务器上的特定文件字节。</summary>
    Task<byte[]> DownloadFileAsync(FlowType flow, string groupName, string batchId, string fileName, CancellationToken ct = default);

    /// <summary>更新服务器端的一行数据。</summary>
    Task UpdateRowAsync(UpdateRowRequest request, CancellationToken ct = default);

    /// <summary>同步挂起异常到服务器。</summary>
    Task SuspendExceptionAsync(SuspendExceptionRequest request, CancellationToken ct = default);

    /// <summary>获取服务器端异常池。</summary>
    Task<List<ExceptionItem>> GetExceptionsAsync(CancellationToken ct = default);

    /// <summary>从服务器端处理/撤销异常。</summary>
    Task ResolveExceptionAsync(FlowType flow, string groupName, string batchId, string rowKey, CancellationToken ct = default);

    /// <summary>按行重新从 PLM 拉取图纸（补图）。Found=false 表示 PLM 仍无该料号图纸。</summary>
    Task<RefetchDrawingResult> RefetchDrawingAsync(RefetchDrawingRequest request, CancellationToken ct = default);
}
