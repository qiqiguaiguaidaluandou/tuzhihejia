using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Tests.Sync;

/// <summary>只实现 BatchSyncService 用得到的成员；其余抛异常（不该被调到）。</summary>
internal sealed class FakeLocalBatchStore : ILocalBatchStore
{
    private readonly HashSet<string> _existing = new();
    public List<FetchResult> Written { get; } = new();

    public void Seed(FlowType flow, string emp, DateTime ws, DateTime we) => _existing.Add(Key(flow, emp, ws, we));
    private static string Key(FlowType f, string e, DateTime ws, DateTime we) => $"{f}|{e}|{ws:O}|{we:O}";

    public bool BatchExists(FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd) =>
        _existing.Contains(Key(flow, employeeId, windowStart, windowEnd));

    public Task<Batch> WriteFetchedBatchAsync(FetchResult fetched, CancellationToken ct = default)
    {
        Written.Add(fetched);
        _existing.Add(Key(fetched.Flow, fetched.EmployeeId, fetched.WindowStart, fetched.WindowEnd));
        return Task.FromResult(new Batch
        {
            Flow = fetched.Flow,
            EmployeeId = fetched.EmployeeId,
            WindowStart = fetched.WindowStart,
            WindowEnd = fetched.WindowEnd,
            FolderName = LocalPaths.BatchFolderName(fetched.WindowStart, fetched.WindowEnd),
            FolderPath = "(fake)",
            Location = BatchLocation.Todo,
        });
    }

    public Task<IReadOnlyList<Batch>> ListBatchesAsync(FlowType flow, string employeeId, BatchLocation location, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Batch?> GetBatchAsync(FlowType flow, string employeeId, BatchLocation location, string folderName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveBatchAsync(Batch batch, CancellationToken ct = default) => throw new NotSupportedException();
    public Task MoveToDoneAsync(Batch batch, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AddExceptionsAsync(FlowType flow, string employeeId, IEnumerable<ExceptionItem> items, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ExceptionItem>> ListExceptionsAsync(FlowType flow, string employeeId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveExceptionAsync(FlowType flow, string employeeId, string sourceBatch, string rowKey, CancellationToken ct = default) => throw new NotSupportedException();

    public string Root => "(fake-root)";

    public Task EnsureBatchFolderAsync(FlowType flow, string groupName, string batchId, BatchLocation location, CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteSyncFileAsync(FlowType flow, string groupName, string batchId, BatchLocation location, string fileName, byte[] content, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddRowDrawingsAsync(FlowType flow, string groupName, string batchId, BatchLocation location, string rowKey, IEnumerable<string> fileNames, CancellationToken ct = default) => Task.CompletedTask;
    public Task OverwriteExceptionsAsync(FlowType flow, string groupName, IEnumerable<ExceptionItem> items, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeDataGateway : IDataGateway
{
    public List<FetchRequest> Requests { get; } = new();
    public List<BatchCatalogItem> Catalog { get; } = new();
    /// <summary>自定义每个请求的应答；返回 null 表示默认成功空批。抛异常表示取数失败。</summary>
    public Func<FetchRequest, FetchResult>? Responder { get; set; }

    public Task<FetchResult> FetchBatchAsync(FetchRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var r = Responder?.Invoke(request) ?? new FetchResult
        {
            Success = true,
            Flow = request.Flow,
            EmployeeId = request.EmployeeId,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
        };
        return Task.FromResult(r);
    }

    public Task<List<BatchCatalogItem>> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult(Catalog);
    public Task<byte[]> DownloadFileAsync(FlowType flow, string groupName, string batchId, string fileName, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task UpdateRowAsync(UpdateRowRequest request, CancellationToken ct = default) => Task.CompletedTask;
    public Task SuspendExceptionAsync(SuspendExceptionRequest request, CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<ExceptionItem>> GetExceptionsAsync(CancellationToken ct = default) => Task.FromResult(new List<ExceptionItem>());
    public Task ResolveExceptionAsync(FlowType flow, string groupName, string batchId, string rowKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task<RefetchDrawingResult> RefetchDrawingAsync(RefetchDrawingRequest request, CancellationToken ct = default) => Task.FromResult(new RefetchDrawingResult { Found = false });
}

internal sealed class FakeAuditGateway : IAuditGateway
{
    public int Calls { get; private set; }
    public Func<FlowType, DateTime, DateTime, bool> Hit { get; set; } = (_, _, _) => false;

    public Task<AuditExistsResponse> ExistsAsync(FlowType flow, DateTime windowStart, DateTime windowEnd, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new AuditExistsResponse { Exists = Hit(flow, windowStart, windowEnd) });
    }
}
