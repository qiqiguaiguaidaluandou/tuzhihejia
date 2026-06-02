using TZHJ.Core.Contracts;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;
using TZHJ.Infrastructure.Fields;
using TZHJ.Infrastructure.Options;
using TZHJ.Infrastructure.Storage;

namespace TZHJ.Tests.Storage;

/// <summary>本地批次存储集成测试（临时目录）：落地 / 读取 / 暂存 / 待处理→已处理 / 异常池 / 防重复。</summary>
public class LocalBatchStoreTests : IDisposable
{
    private const string Emp = "10086";
    private static readonly DateTime Ws = new(2026, 5, 26, 15, 31, 0);
    private static readonly DateTime We = new(2026, 5, 27, 9, 30, 0);

    private readonly string _root = TempDir.Create();
    private readonly LocalBatchStore _store;

    public LocalBatchStoreTests() =>
        _store = new LocalBatchStore(new DefaultFieldProvider(), new LocalStorageOptions { Root = _root });

    public void Dispose() => TempDir.Delete(_root);

    private static FetchResult SampleFetch(int rows = 2)
    {
        var list = new List<FetchedRow>();
        for (var i = 1; i <= rows; i++)
        {
            var code = $"M-{i}";
            var fr = new FetchedRow
            {
                RowKey = code,
                Values = new()
                {
                    ["materialCode"] = code, ["model"] = $"GB-{i}", ["name"] = $"件{i}",
                    ["demandQty"] = i.ToString(), ["hasChange"] = "无变更", ["targetPrice"] = null,
                },
            };
            fr.Drawings.Add(new FetchedDrawing { FileName = $"{code}__件{i}.pdf", MaterialCode = code, Content = new byte[] { 1, 2, 3 } });
            list.Add(fr);
        }
        return new FetchResult { Success = true, Flow = FlowType.Pricing, EmployeeId = Emp, WindowStart = Ws, WindowEnd = We, Rows = list };
    }

    [Fact]
    public async Task Write_lands_batch_in_todo_with_xlsx_drawings_manifest()
    {
        var batch = await _store.WriteFetchedBatchAsync(SampleFetch());

        Assert.Equal(BatchLocation.Todo, batch.Location);
        Assert.True(File.Exists(Path.Combine(batch.FolderPath, LocalFolders.GridWorkbookName(batch.FolderName))));
        Assert.True(File.Exists(Path.Combine(batch.FolderPath, LocalFolders.Manifest)));
        Assert.True(File.Exists(Path.Combine(batch.FolderPath, "M-1__件1.pdf")));
        Assert.True(_store.BatchExists(FlowType.Pricing, Emp, Ws, We));
    }

    [Fact]
    public async Task Xlsx_filename_equals_batch_folder_name()
    {
        var batch = await _store.WriteFetchedBatchAsync(SampleFetch());

        var xlsxFiles = Directory.GetFiles(batch.FolderPath, "*.xlsx");
        Assert.Single(xlsxFiles);
        Assert.Equal(batch.FolderName + ".xlsx", Path.GetFileName(xlsxFiles[0]));
    }

    [Fact]
    public async Task Get_reads_back_values_status_and_drawings()
    {
        var written = await _store.WriteFetchedBatchAsync(SampleFetch());

        var batch = await _store.GetBatchAsync(FlowType.Pricing, Emp, BatchLocation.Todo, written.FolderName);

        Assert.NotNull(batch);
        Assert.Equal(2, batch!.Rows.Count);
        Assert.All(batch.Rows, r => Assert.Equal(RowStatus.Pending, r.Status));
        var r1 = batch.Rows.Single(r => r.RowKey == "M-1");
        Assert.Equal("GB-1", r1.Get("model"));
        Assert.Single(r1.Drawings);
        Assert.True(r1.Drawings[0].Exists);
    }

    [Fact]
    public async Task Save_persists_row_status_and_filled_value_to_manifest()
    {
        var written = await _store.WriteFetchedBatchAsync(SampleFetch());
        var batch = (await _store.GetBatchAsync(FlowType.Pricing, Emp, BatchLocation.Todo, written.FolderName))!;

        batch.Rows[0].Status = RowStatus.Done;
        batch.Rows[0].Set("targetPrice", "99");
        await _store.SaveBatchAsync(batch);

        var reread = (await _store.GetBatchAsync(FlowType.Pricing, Emp, BatchLocation.Todo, written.FolderName))!;
        var r0 = reread.Rows.Single(r => r.RowKey == batch.Rows[0].RowKey);
        Assert.Equal(RowStatus.Done, r0.Status);
        Assert.Equal("99", r0.Get("targetPrice"));
    }

    [Fact]
    public async Task MoveToDone_relocates_folder_and_stamps_submitted()
    {
        var written = await _store.WriteFetchedBatchAsync(SampleFetch());
        var batch = (await _store.GetBatchAsync(FlowType.Pricing, Emp, BatchLocation.Todo, written.FolderName))!;

        await _store.MoveToDoneAsync(batch);

        var todoDir = LocalPaths.BatchDir(_root, FlowType.Pricing, Emp, BatchLocation.Todo, written.FolderName);
        var doneDir = LocalPaths.BatchDir(_root, FlowType.Pricing, Emp, BatchLocation.Done, written.FolderName);
        Assert.False(Directory.Exists(todoDir));
        Assert.True(Directory.Exists(doneDir));

        var done = (await _store.GetBatchAsync(FlowType.Pricing, Emp, BatchLocation.Done, written.FolderName))!;
        Assert.NotNull(done.SubmittedAt);
    }

    [Fact]
    public async Task Exceptions_add_and_list_roundtrip()
    {
        await _store.AddExceptionsAsync(FlowType.Pricing, Emp, new[]
        {
            new ExceptionItem
            {
                Flow = FlowType.Pricing, RowKey = "M-1", MaterialCode = "M-1",
                SourceBatch = "20260526_1531-20260527_0930", Reason = "图纸缺失", SuspendedAt = DateTime.Now,
            },
        });

        var list = await _store.ListExceptionsAsync(FlowType.Pricing, Emp);
        Assert.Single(list);
        Assert.Equal("图纸缺失", list[0].Reason);
    }

    [Fact]
    public async Task BatchExists_is_window_and_flow_scoped()
    {
        await _store.WriteFetchedBatchAsync(SampleFetch());

        Assert.True(_store.BatchExists(FlowType.Pricing, Emp, Ws, We));
        Assert.False(_store.BatchExists(FlowType.Pricing, Emp, Ws.AddDays(-1), We.AddDays(-1))); // 另一窗口
        Assert.False(_store.BatchExists(FlowType.DrawingSelection, Emp, Ws, We));                // 另一流程
    }
}
