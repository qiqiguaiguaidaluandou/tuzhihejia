using System.Text.Json;
using System.Text.Json.Serialization;
using TZHJ.Core.Contracts;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;
using TZHJ.Infrastructure.Options;

namespace TZHJ.Infrastructure.Storage;

/// <summary>
/// 本地批次存储实现。文件夹即真相源：批次列表/状态映射 {待处理|已处理} 目录，
/// 行级状态在 manifest，待处理→已处理 仅由回传成功驱动（MoveToDoneAsync）。
/// </summary>
public sealed class LocalBatchStore : ILocalBatchStore
{
    private const string MaterialCodeKey = "materialCode";
    private static readonly string[] NameKeys = { "name", "materialDesc" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IFieldProvider _fields;
    private readonly LocalStorageOptions _storage;

    public LocalBatchStore(IFieldProvider fields, LocalStorageOptions storage)
    {
        _fields = fields;
        _storage = storage;
    }

    public string Root => _storage.Root;

    // ---------- 列表 ----------


    public async Task<IReadOnlyList<Batch>> ListBatchesAsync(
        FlowType flow, string employeeId, BatchLocation location, CancellationToken ct = default)
    {
        var rootDir = Path.Combine(Root, LocalFolders.FlowFolder(flow), LocalFolders.LocationFolder(location));
        var batches = new List<Batch>();

        if (Directory.Exists(rootDir))
        {
            if (flow == FlowType.DrawingSelection)
            {
                // 一层结构：Flow / Location / BatchId
                foreach (var sub in Directory.GetDirectories(rootDir))
                {
                    await ProcessBatchDir(sub, "Center", batches, flow, employeeId, location, ct);
                }
            }
            else
            {
                // 两层结构：Flow / Location / Group / BatchId
                foreach (var groupDir in Directory.GetDirectories(rootDir))
                {
                    var groupName = Path.GetFileName(groupDir);
                    foreach (var sub in Directory.GetDirectories(groupDir))
                    {
                        await ProcessBatchDir(sub, groupName, batches, flow, employeeId, location, ct);
                    }
                }
            }
        }

        return batches.OrderByDescending(b => b.WindowStart).ToList();
    }

    private async Task ProcessBatchDir(string sub, string groupName, List<Batch> batches, FlowType flow, string employeeId, BatchLocation location, CancellationToken ct)
    {
        var folderName = Path.GetFileName(sub);
        if (!LocalPaths.TryParseFolderName(folderName, out var start, out var end))
            return;

        var manifest = await BatchManifest.LoadAsync(Path.Combine(sub, LocalFolders.Manifest), ct);
        int totalCount = manifest?.TotalRows ?? 0;
        var rows = new List<MaterialRow>();

        if (manifest is not null && manifest.Rows.Count > 0)
        {
            rows = manifest.Rows.Select(m => new MaterialRow
            {
                RowKey = m.RowKey,
                GroupName = groupName,
                Status = m.Status,
                ExceptionReason = m.ExceptionReason,
            }).ToList();
            if (totalCount == 0) totalCount = rows.Count;
        }
        else
        {
            rows = LoadRowsFromXlsx(sub, folderName, flow);
            foreach (var row in rows) row.GroupName = groupName;
            if (totalCount == 0) totalCount = rows.Count;
        }

        batches.Add(new Batch
        {
            Flow = flow,
            EmployeeId = employeeId,
            GroupName = groupName,
            WindowStart = start,
            WindowEnd = end,
            FolderName = folderName,
            FolderPath = sub,
            Location = location,
            Rows = rows,
            TotalRowsFromMeta = totalCount,
            FetchedAt = manifest?.FetchedAt ?? Directory.GetCreationTime(sub),
            SubmittedAt = manifest?.SubmittedAt,
        });
    }

    // ---------- 读取完整批次 ----------

    public async Task<Batch?> GetBatchAsync(
        FlowType flow, string employeeId, BatchLocation location, string folderName, CancellationToken ct = default)
    {
        var rootDir = Path.Combine(Root, LocalFolders.FlowFolder(flow), LocalFolders.LocationFolder(location));
        string? dir = null;
        string groupName = "Center";

        if (Directory.Exists(rootDir))
        {
            if (flow == FlowType.DrawingSelection)
            {
                var candidate = Path.Combine(rootDir, folderName);
                if (Directory.Exists(candidate)) dir = candidate;
            }
            else
            {
                foreach (var groupDir in Directory.GetDirectories(rootDir))
                {
                    var candidate = Path.Combine(groupDir, folderName);
                    if (Directory.Exists(candidate))
                    {
                        dir = candidate;
                        groupName = Path.GetFileName(groupDir);
                        break;
                    }
                }
            }
        }
        
        if (dir == null) return null;
        if (!LocalPaths.TryParseFolderName(folderName, out var start, out var end)) return null;

        // 最终兜底：如果 groupName 还是 Default，尝试从物理路径再取一次
        if (groupName == "Default")
        {
            groupName = Path.GetFileName(Path.GetDirectoryName(dir)!) ?? "Default";
        }

        var fields = _fields.FieldsFor(flow);
        var xlsxRows = ExcelGridIO.Read(Path.Combine(dir, LocalFolders.GridWorkbookName(folderName)), fields);
        var manifest = await BatchManifest.LoadAsync(Path.Combine(dir, LocalFolders.Manifest), ct);
        var manifestByKey = manifest?.Rows.ToDictionary(m => m.RowKey) ?? new();

        var rows = new List<MaterialRow>();
        foreach (var (rowKey, values) in xlsxRows)
        {
            var row = new MaterialRow { RowKey = rowKey, Values = values, GroupName = groupName };
            var materialCode = values.GetValueOrDefault(MaterialCodeKey) ?? rowKey;

            if (manifestByKey.TryGetValue(rowKey, out var mr))
            {
                row.Status = mr.Status;
                row.ExceptionReason = mr.ExceptionReason;
                // 完整性校验：manifest 期望图纸 vs 磁盘实际
                foreach (var fileName in mr.Drawings)
                    row.Drawings.Add(MakeDrawingRef(dir, fileName, materialCode));
            }
            else
            {
                // 无 manifest 记录：扫盘按物料编码前缀找图
                foreach (var file in ScanDrawings(dir, materialCode))
                    row.Drawings.Add(MakeDrawingRef(dir, Path.GetFileName(file), materialCode));
            }
            rows.Add(row);
        }

        return new Batch
        {
            Flow = flow,
            EmployeeId = employeeId,
            GroupName = groupName,
            WindowStart = start,
            WindowEnd = end,
            FolderName = folderName,
            FolderPath = dir,
            Location = location,
            Rows = rows,
            FetchedAt = manifest?.FetchedAt ?? Directory.GetCreationTime(dir),
            SubmittedAt = manifest?.SubmittedAt,
        };
    }

    public async Task<Batch> WriteFetchedBatchAsync(FetchResult fetched, CancellationToken ct = default)
    {
        var fields = _fields.FieldsFor(fetched.Flow);
        var folderName = LocalPaths.BatchFolderName(fetched.WindowStart, fetched.WindowEnd);
        
        // --- 组织架构优化: 使用 GroupName ---
        var groupName = fetched.GroupName ?? "Default";
        var dir = LocalPaths.LocalBatchDir(Root, fetched.Flow, BatchLocation.Todo, groupName, folderName);
        Directory.CreateDirectory(dir);

        var rows = new List<MaterialRow>();
        var manifestRows = new List<ManifestRow>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fr in fetched.Rows)
        {
            var row = new MaterialRow
            {
                RowKey = fr.RowKey,
                Values = new Dictionary<string, string?>(fr.Values),
                Status = RowStatus.Pending,
            };
            var materialCode = fr.Values.GetValueOrDefault(MaterialCodeKey) ?? fr.RowKey;

            var drawingNames = new List<string>();
            foreach (var d in fr.Drawings)
            {
                var fileName = EnsureUnique(d.FileName, usedNames);
                await File.WriteAllBytesAsync(Path.Combine(dir, fileName), d.Content, ct);
                drawingNames.Add(fileName);
                row.Drawings.Add(MakeDrawingRef(dir, fileName, materialCode));
            }

            rows.Add(row);
            manifestRows.Add(new ManifestRow
            {
                RowKey = fr.RowKey,
                MaterialCode = materialCode,
                DisplayName = DisplayNameOf(fr.Values),
                Status = RowStatus.Pending,
                Drawings = drawingNames,
            });
        }

        ExcelGridIO.Write(Path.Combine(dir, LocalFolders.GridWorkbookName(folderName)), fields, rows);

        var manifest = new BatchManifest
        {
            Flow = fetched.Flow,
            EmployeeId = fetched.EmployeeId,
            WindowStart = fetched.WindowStart,
            WindowEnd = fetched.WindowEnd,
            FetchedAt = DateTime.Now,
            Rows = manifestRows,
        };
        await BatchManifest.SaveAsync(Path.Combine(dir, LocalFolders.Manifest), manifest, ct);

        return new Batch
        {
            Flow = fetched.Flow,
            EmployeeId = fetched.EmployeeId,
            GroupName = groupName,
            WindowStart = fetched.WindowStart,
            WindowEnd = fetched.WindowEnd,
            FolderName = folderName,
            FolderPath = dir,
            Location = BatchLocation.Todo,
            Rows = rows,
            FetchedAt = manifest.FetchedAt,
        };
    }

    // ---------- 暂存（写回 xlsx + manifest） ----------

    public async Task SaveBatchAsync(Batch batch, CancellationToken ct = default)
    {
        var fields = _fields.FieldsFor(batch.Flow);
        ExcelGridIO.Write(Path.Combine(batch.FolderPath, LocalFolders.GridWorkbookName(batch.FolderName)), fields, batch.Rows);

        var manifestPath = Path.Combine(batch.FolderPath, LocalFolders.Manifest);
        var manifest = await BatchManifest.LoadAsync(manifestPath, ct) ?? new BatchManifest
        {
            Flow = batch.Flow,
            EmployeeId = batch.EmployeeId,
            WindowStart = batch.WindowStart,
            WindowEnd = batch.WindowEnd,
            FetchedAt = batch.FetchedAt,
        };

        manifest.Rows = batch.Rows.Select(r => new ManifestRow
        {
            RowKey = r.RowKey,
            MaterialCode = r.Get(MaterialCodeKey) ?? r.RowKey,
            DisplayName = DisplayNameOf(r.Values),
            Status = r.Status,
            ExceptionReason = r.ExceptionReason,
            Drawings = r.Drawings.Select(d => d.FileName).ToList(),
        }).ToList();

        await BatchManifest.SaveAsync(manifestPath, manifest, ct);
    }

    // ---------- 待处理 → 已处理 ----------

    public async Task MoveToDoneAsync(Batch batch, CancellationToken ct = default)
    {
        var dest = LocalPaths.LocalBatchDir(Root, batch.Flow, BatchLocation.Done, batch.GroupName, batch.FolderName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        Directory.Move(batch.FolderPath, dest);

        var manifestPath = Path.Combine(dest, LocalFolders.Manifest);
        var manifest = await BatchManifest.LoadAsync(manifestPath, ct);
        if (manifest is not null)
        {
            manifest.SubmittedAt = DateTime.Now;
            await BatchManifest.SaveAsync(manifestPath, manifest, ct);
        }
    }

    // ---------- 异常待跟进池 ----------

    public async Task AddExceptionsAsync(
        FlowType flow, string employeeId, IEnumerable<ExceptionItem> items, CancellationToken ct = default)
    {
        var poolDir = LocalPaths.LocalExceptionPoolRoot(Root, flow);
        Directory.CreateDirectory(poolDir);
        var file = Path.Combine(poolDir, LocalFolders.ExceptionPoolFile);

        var list = (await ReadExceptionsAsync(file, ct)).ToList();
        list.AddRange(items);

        await using var fs = File.Create(file);
        await JsonSerializer.SerializeAsync(fs, list, JsonOpts, ct);
    }

    public async Task<IReadOnlyList<ExceptionItem>> ListExceptionsAsync(
        FlowType flow, string employeeId, CancellationToken ct = default)
    {
        var file = Path.Combine(LocalPaths.LocalExceptionPoolRoot(Root, flow), LocalFolders.ExceptionPoolFile);
        var list = await ReadExceptionsAsync(file, ct);
        return list.OrderByDescending(e => e.SuspendedAt).ToList();
    }

    public async Task RemoveExceptionAsync(
        FlowType flow, string employeeId, string sourceBatch, string rowKey, CancellationToken ct = default)
    {
        var file = Path.Combine(LocalPaths.LocalExceptionPoolRoot(Root, flow), LocalFolders.ExceptionPoolFile);
        if (!File.Exists(file)) return;

        var list = (await ReadExceptionsAsync(file, ct)).ToList();
        if (list.RemoveAll(e => e.SourceBatch == sourceBatch && e.RowKey == rowKey) == 0) return;

        await using var fs = File.Create(file);
        await JsonSerializer.SerializeAsync(fs, list, JsonOpts, ct);
    }

    public bool BatchExists(FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd)
    {
        var folderName = LocalPaths.BatchFolderName(windowStart, windowEnd);
        // 此处为了兼容，我们只能假设一个默认组进行检查，或者扫描所有组。
        // 由于 BatchExists 主要是为了同步，在新逻辑下它变得不那么重要，因为 Catalog 会驱动同步。
        // 暂时扫描所有可能的组。
        var todoBase = LocalPaths.LocalLocationRoot(Root, flow, BatchLocation.Todo);
        var doneBase = LocalPaths.LocalLocationRoot(Root, flow, BatchLocation.Done);

        return (Directory.Exists(todoBase) && Directory.GetDirectories(todoBase).Any(g => Directory.Exists(Path.Combine(g, folderName))))
            || (Directory.Exists(doneBase) && Directory.GetDirectories(doneBase).Any(g => Directory.Exists(Path.Combine(g, folderName))));
    }

    public async Task EnsureBatchFolderAsync(FlowType flow, string groupName, string batchId, BatchLocation location, CancellationToken ct = default)
    {
        var dir = LocalPaths.LocalBatchDir(Root, flow, location, groupName, batchId);
        Directory.CreateDirectory(dir);

        // 如果 manifest 不存在，创建一个占位的
        var manifestPath = Path.Combine(dir, LocalFolders.Manifest);
        if (!File.Exists(manifestPath))
        {
            LocalPaths.TryParseFolderName(batchId, out var start, out var end);
            var manifest = new BatchManifest
            {
                Flow = flow,
                EmployeeId = "(group)", // 新逻辑下工号不再参与物理路径，此处仅填充
                WindowStart = start,
                WindowEnd = end,
                FetchedAt = DateTime.Now
            };
            await BatchManifest.SaveAsync(manifestPath, manifest, ct);
        }

        // 检查物理位置是否正确（处理 Todo <-> Done 迁移）
        var otherLocation = location == BatchLocation.Todo ? BatchLocation.Done : BatchLocation.Todo;
        var otherDir = LocalPaths.LocalBatchDir(Root, flow, otherLocation, groupName, batchId);
        if (Directory.Exists(otherDir))
        {
            // 如果在另一个位置存在，说明状态发生了变化，移动它
            // 注意：Directory.Move 不能移动到已存在的目录
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(otherDir, dir);
        }
    }

    public async Task WriteSyncFileAsync(FlowType flow, string groupName, string batchId, BatchLocation location, string fileName, byte[] content, CancellationToken ct = default)
    {
        var dir = LocalPaths.LocalBatchDir(Root, flow, location, groupName, batchId);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), content, ct);
    }

    public async Task AddRowDrawingsAsync(FlowType flow, string groupName, string batchId, BatchLocation location, string rowKey, IEnumerable<string> fileNames, CancellationToken ct = default)
    {
        var dir = LocalPaths.LocalBatchDir(Root, flow, location, groupName, batchId);
        var manifestPath = Path.Combine(dir, LocalFolders.Manifest);
        var manifest = await BatchManifest.LoadAsync(manifestPath, ct);
        if (manifest is null) return; // 无 manifest：靠扫盘按物料编码前缀仍可识别，跳过即可。

        var mr = manifest.Rows.FirstOrDefault(r => r.RowKey == rowKey);
        if (mr is null)
        {
            mr = new ManifestRow { RowKey = rowKey };
            manifest.Rows.Add(mr);
        }

        var seen = new HashSet<string>(mr.Drawings, StringComparer.OrdinalIgnoreCase);
        foreach (var f in fileNames)
            if (seen.Add(f)) mr.Drawings.Add(f);

        await BatchManifest.SaveAsync(manifestPath, manifest, ct);
    }

    public async Task OverwriteExceptionsAsync(FlowType flow, string groupName, IEnumerable<ExceptionItem> items, CancellationToken ct = default)
    {
        var poolDir = LocalPaths.LocalExceptionPoolRoot(Root, flow);
        Directory.CreateDirectory(poolDir);
        var file = Path.Combine(poolDir, LocalFolders.ExceptionPoolFile);

        await using var fs = File.Create(file);
        await JsonSerializer.SerializeAsync(fs, items.ToList(), JsonOpts, ct);
    }

    // ---------- 私有辅助 ----------

    private static async Task<List<ExceptionItem>> ReadExceptionsAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return new();
        await using var fs = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<List<ExceptionItem>>(fs, JsonOpts, ct) ?? new();
    }

    private List<MaterialRow> LoadRowsFromXlsx(string dir, string folderName, FlowType flow)
    {
        var fields = _fields.FieldsFor(flow);
        return ExcelGridIO.Read(Path.Combine(dir, LocalFolders.GridWorkbookName(folderName)), fields)
            .Select(x => new MaterialRow { RowKey = x.RowKey, Values = x.Values, Status = RowStatus.Pending })
            .ToList();
    }

    private static DrawingRef MakeDrawingRef(string dir, string fileName, string materialCode) => new()
    {
        FileName = fileName,
        MaterialCode = materialCode,
        Kind = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(),
        Exists = File.Exists(Path.Combine(dir, fileName)),
    };

    private static IEnumerable<string> ScanDrawings(string dir, string materialCode)
    {
        var prefix = materialCode + "__";
        // 物料编码作前缀已能将 xlsx 排除（不以编码起头），无需再硬编码表格文件名。
        return Directory.EnumerateFiles(dir)
            .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? DisplayNameOf(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var k in NameKeys)
            if (values.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v))
                return v;
        return null;
    }

    private static string EnsureUnique(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
