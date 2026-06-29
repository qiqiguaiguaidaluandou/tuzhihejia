using System.Collections.Concurrent;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Gateway.Stores;

public sealed class FileServerBatchStore : IServerBatchStore
{
    private readonly ServerStorageOptions _options;
    private readonly IFieldProvider _fields;

    // 按批次键串行化磁盘写入，防止并发 read-modify-write 损坏 Excel（doc ⑦ §5）。
    // 进程内锁，单网关实例部署足够（方案设计 §10）；多实例需改分布式锁，见 changes/009 备注。
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    public FileServerBatchStore(ServerStorageOptions options, IFieldProvider fields)
    {
        _options = options;
        _fields = fields;
    }

    public string Root => _options.ServerRoot;

    private string GetBatchPath(FlowType flow, string groupName, string batchId)
        => LocalPaths.ServerBatchDir(Root, flow, groupName, batchId);

    private static SemaphoreSlim LockFor(FlowType flow, string groupName, string batchId)
        => _writeLocks.GetOrAdd($"{flow}/{groupName}/{batchId}", _ => new SemaphoreSlim(1, 1));

    public async Task SaveBatchAsync(FetchResponse fetched, string groupName, IEnumerable<(string FileName, byte[] Content)> drawings, CancellationToken ct = default)
    {
        var batchId = LocalPaths.BatchFolderName(fetched.WindowStart, fetched.WindowEnd);
        var sem = LockFor(fetched.Flow, groupName, batchId);
        await sem.WaitAsync(ct);
        try
        {
            var dir = GetBatchPath(fetched.Flow, groupName, batchId);
            Directory.CreateDirectory(dir);

            // 1. 写图纸
            foreach (var (fileName, content) in drawings)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, fileName), content, ct);
            }

            // 2. 写 Excel
            var excelPath = Path.Combine(dir, LocalFolders.GridWorkbookName(batchId));
            var fieldDefs = _fields.FieldsFor(fetched.Flow);

            using var workbook = new XSSFWorkbook();
            var sheet = workbook.CreateSheet("清单表格");

            // 表头
            var headerRow = sheet.CreateRow(0);
            var orderedFields = fieldDefs.OrderBy(f => f.Order).ToList();
            for (int i = 0; i < orderedFields.Count; i++)
            {
                headerRow.CreateCell(i).SetCellValue(orderedFields[i].DisplayName);
            }

            // 数据
            for (int i = 0; i < fetched.Rows.Count; i++)
            {
                var rowDto = fetched.Rows[i];
                var dataRow = sheet.CreateRow(i + 1);
                for (int j = 0; j < orderedFields.Count; j++)
                {
                    var val = rowDto.Values.GetValueOrDefault(orderedFields[j].Key);
                    dataRow.CreateCell(j).SetCellValue(val ?? "");
                }
            }

            using var fs = File.Create(excelPath);
            workbook.Write(fs);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task AppendDrawingsAsync(FlowType flow, string groupName, string batchId, IEnumerable<(string FileName, byte[] Content)> drawings, CancellationToken ct = default)
    {
        // 与 SaveBatchAsync 同一把批次锁，避免与同批次的 read-modify-write 撞车。
        var sem = LockFor(flow, groupName, batchId);
        await sem.WaitAsync(ct);
        try
        {
            var dir = GetBatchPath(flow, groupName, batchId);
            Directory.CreateDirectory(dir);
            foreach (var (fileName, content) in drawings)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, fileName), content, ct);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public Task<List<SyncFileMeta>> ListFilesAsync(FlowType flow, string groupName, string batchId, CancellationToken ct = default)
    {
        var dir = GetBatchPath(flow, groupName, batchId);
        if (!Directory.Exists(dir)) return Task.FromResult(new List<SyncFileMeta>());

        var files = Directory.GetFiles(dir)
            .Select(f => new SyncFileMeta
            {
                FileName = Path.GetFileName(f),
                Size = new FileInfo(f).Length,
                LastModified = File.GetLastWriteTime(f)
            })
            .ToList();

        return Task.FromResult(files);
    }

    public Stream? OpenFile(FlowType flow, string groupName, string batchId, string fileName)
    {
        var path = Path.Combine(GetBatchPath(flow, groupName, batchId), fileName);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public async Task UpdateExcelRowAsync(FlowType flow, string groupName, string batchId, string rowKey, Dictionary<string, string?> values, CancellationToken ct = default)
    {
        var dir = GetBatchPath(flow, groupName, batchId);
        var excelPath = Path.Combine(dir, LocalFolders.GridWorkbookName(batchId));
        if (!File.Exists(excelPath)) throw new FileNotFoundException("Excel file not found.", excelPath);

        var fieldDefs = _fields.FieldsFor(flow);
        var rowKeyField = fieldDefs.FirstOrDefault(f => f.IsRowKey)
                          ?? throw new InvalidOperationException("No RowKey field defined for this flow.");

        // 串行化同一批次的 read-modify-write，避免并发写坏 xlsx。
        var sem = LockFor(flow, groupName, batchId);
        await sem.WaitAsync(ct);
        try
        {
            IWorkbook workbook;
            using (var readFs = new FileStream(excelPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                workbook = WorkbookFactory.Create(readFs);
            }

            var sheet = workbook.GetSheetAt(0);

            // 1. 寻找列索引
            var headerRow = sheet.GetRow(0);
            var colMap = new Dictionary<string, int>(); // DisplayName -> ColIndex
            for (int i = 0; i < headerRow.LastCellNum; i++)
            {
                var cell = headerRow.GetCell(i);
                if (cell != null) colMap[cell.StringCellValue.Trim()] = i;
            }

            var keyToCol = fieldDefs
                .Where(f => colMap.ContainsKey(f.DisplayName))
                .ToDictionary(f => f.Key, f => colMap[f.DisplayName]);

            if (!keyToCol.TryGetValue(rowKeyField.Key, out var rowKeyCol))
                throw new InvalidOperationException($"RowKey column '{rowKeyField.DisplayName}' not found in Excel.");

            // 2. 寻找行并更新
            bool updated = false;
            for (int i = 1; i <= sheet.LastRowNum; i++)
            {
                var row = sheet.GetRow(i);
                if (row == null) continue;

                var currentKey = row.GetCell(rowKeyCol)?.ToString();
                if (currentKey == rowKey)
                {
                    foreach (var (key, val) in values)
                    {
                        if (keyToCol.TryGetValue(key, out var colIdx))
                        {
                            var cell = row.GetCell(colIdx) ?? row.CreateCell(colIdx);
                            cell.SetCellValue(val ?? "");
                        }
                    }
                    updated = true;
                    break;
                }
            }

            if (updated)
            {
                // 写回文件
                using var writeFs = new FileStream(excelPath, FileMode.Create, FileAccess.Write, FileShare.None);
                workbook.Write(writeFs);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public Task MoveToDoneAsync(FlowType flow, string groupName, string batchId, CancellationToken ct = default)
    {
        // 方案二：扁平化存储，不物理移动。
        return Task.CompletedTask;
    }

    public Task DeleteBatchAsync(FlowType flow, string groupName, string batchId, CancellationToken ct = default)
    {
        var dir = GetBatchPath(flow, groupName, batchId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        return Task.CompletedTask;
    }
}
