using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;
using TZHJ.Gateway.AntiCorruption;
using TZHJ.Gateway.Stores;
using Microsoft.EntityFrameworkCore;

namespace TZHJ.Gateway;

/// <summary>
/// 后端定时采集服务（模拟服务器主动抓取 EBS/PLM 数据）。
/// 运行于服务器后台，不依赖客户端。抓取后存入服务器路径并注册到数据库。
/// </summary>
public sealed class DataIngestionService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<DataIngestionService> _logger;
    private readonly IConfiguration _config;

    public DataIngestionService(IServiceProvider sp, ILogger<DataIngestionService> logger, IConfiguration config)
    {
        _sp = sp;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Data Ingestion Service started. 按排程采集（核价 10:00/16:00；挑图 10:00/15:00/18:30）。");

        // 启动先清一次过期批次；采集本身由下面的循环按排程触发（含重启后补采）。
        await CleanupOldBatchesAsync(stoppingToken);

        // 每 5 分钟检查一次排程：到点而未采的批次就采（已采的会跳过，不重复调接口）。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSchedulesAsync(stoppingToken);

                // 周期性清理（每小时执行一次，跟随时钟）
                if (DateTime.Now.Minute % 60 == 0)
                {
                    await CleanupOldBatchesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodical data ingestion/cleanup.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task CleanupOldBatchesAsync(CancellationToken ct)
    {
        var todoDays = _config.GetValue<int>("Config:RetentionDaysForTodo", 30);
        var doneDays = _config.GetValue<int>("Config:RetentionDaysForDone", 15);

        var todoThreshold = DateTime.UtcNow.AddDays(-todoDays);
        var doneThreshold = DateTime.UtcNow.AddDays(-doneDays);

        _logger.LogInformation("Batch cleanup started. Todo Threshold: {Todo:O} ({TodoDays}d), Done Threshold: {Done:O} ({DoneDays}d)", 
            todoThreshold, todoDays, doneThreshold, doneDays);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TzhjDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IServerBatchStore>();

        // 找出过期的批次
        var expired = await db.BatchRegistries
            .Where(b => (b.Status == BatchLocation.Todo && b.LastModified < todoThreshold)
                     || (b.Status == BatchLocation.Done && b.LastModified < doneThreshold))
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        foreach (var b in expired)
        {
            try
            {
                _logger.LogInformation("Deleting expired batch ({Status}): {Flow} - {Group} - {BatchId}", 
                    b.Status, b.Flow, b.GroupName, b.BatchId);

                // 1. 物理删除
                await store.DeleteBatchAsync(b.Flow, b.GroupName, b.BatchId, ct);

                // 2. 数据库删除
                db.BatchRegistries.Remove(b);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete expired batch {BatchId}", b.BatchId);
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Cleanup completed. Removed {Count} batches.", expired.Count);
    }

    /// <summary>一条采集排程：到达「触发时刻」时，取 [窗口起, 窗口止] 的数据。窗口止固定在触发当天。</summary>
    private sealed record Schedule(
        FlowType Flow,
        int TriggerHour, int TriggerMinute,
        int StartDayOffset, int StartHour, int StartMinute, // 窗口起点相对触发日的天偏移（0=当天，-1=昨天）
        int EndHour, int EndMinute);

    /// <summary>
    /// 项目规定的采集排程（见 docs/接口文档.md 与 docs/changes/013）。
    /// 触发时刻比窗口结束晚约 30 分钟，留数据落定缓冲。各流程窗口首尾相接、整天闭合。
    /// </summary>
    private static readonly Schedule[] Schedules =
    {
        // 图纸核价：10:00 取「昨天15:31 ~ 今天09:30」；16:00 取「今天09:31 ~ 15:30」。
        new(FlowType.Pricing,          10,  0,  -1, 15, 31,   9, 30),
        new(FlowType.Pricing,          16,  0,   0,  9, 31,  15, 30),
        // 机加中心挑图：10:00 取「昨天18:01 ~ 今天09:30」；15:00 取「今天09:31 ~ 14:30」；18:30 取「今天14:31 ~ 18:00」。
        new(FlowType.DrawingSelection, 10,  0,  -1, 18,  1,   9, 30),
        new(FlowType.DrawingSelection, 15,  0,   0,  9, 31,  14, 30),
        new(FlowType.DrawingSelection, 18, 30,   0, 14, 31,  18,  0),
    };

    /// <summary>
    /// 检查所有排程：触发时刻已过、且对应批次尚未采集的就采。
    /// 回看「昨天 + 今天」两天的触发点，以便服务重启后补采错过的批次（已存在的会跳过，不重复调接口）。
    /// </summary>
    private async Task CheckSchedulesAsync(CancellationToken ct)
    {
        var now = DateTime.Now;

        foreach (var triggerDate in new[] { now.Date.AddDays(-1), now.Date })
        {
            foreach (var s in Schedules)
            {
                if (ct.IsCancellationRequested) return;

                var triggerAt = triggerDate.AddHours(s.TriggerHour).AddMinutes(s.TriggerMinute);
                if (now < triggerAt) continue; // 还没到触发时刻 → 不采（避免取到未封口的窗口）

                var ws = triggerDate.AddDays(s.StartDayOffset).AddHours(s.StartHour).AddMinutes(s.StartMinute);
                var we = triggerDate.AddHours(s.EndHour).AddMinutes(s.EndMinute);

                try
                {
                    using var scope = _sp.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TzhjDbContext>();
                    var source = scope.ServiceProvider.GetRequiredService<IEbsPlmSource>();
                    var store = scope.ServiceProvider.GetRequiredService<IServerBatchStore>();
                    await IngestWindowAsync(db, source, store, s.Flow, ws, we, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "采集失败：{Flow} 窗口 {Start:yyyy-MM-dd HH:mm}~{End:yyyy-MM-dd HH:mm}", s.Flow, ws, we);
                }
            }
        }
    }

    /// <summary>
    /// 取一个窗口的全部行（真实 EBS：一次调用），再按"数据自带的组别"拆分落盘：
    /// 核价按响应里的 GROUP_NAME 分组；挑图不分组，整批归到 "Center"。
    /// 组别不再由本服务预设（旧的写死 组1/组2 已移除），完全跟随源数据。
    /// </summary>
    private async Task IngestWindowAsync(TzhjDbContext db, IEbsPlmSource source, IServerBatchStore store, FlowType flow, DateTime ws, DateTime we, CancellationToken ct)
    {
        // 批次级预检：该窗口已登记且磁盘完好 → 直接跳过，避免对真实 EBS 重复取数（同一批次一天会被轮询很多次）。
        // 若 DB 有记录但磁盘缺失，则放行重取（存储自愈）。
        var batchId = LocalPaths.BatchFolderName(ws, we);
        var existing = await db.BatchRegistries.Where(b => b.Flow == flow && b.BatchId == batchId).ToListAsync(ct);
        if (existing.Count > 0 &&
            existing.All(b => Directory.Exists(LocalPaths.ServerBatchDir(store.Root, flow, b.GroupName, batchId))))
            return;

        var rows = await source.FetchRowsAsync(flow, "SYSTEM", ws, we, ct);
        _logger.LogInformation("EBS 取数完成：{Flow} {Start:yyyy-MM-dd HH:mm}~{End:yyyy-MM-dd HH:mm} → {Count} 行",
            flow, ws, we, rows.Count);
        if (rows.Count == 0) return;

        var groups = flow == FlowType.Pricing
            ? rows.GroupBy(r => string.IsNullOrWhiteSpace(r.GroupName) ? "未分组" : r.GroupName!.Trim())
            : rows.GroupBy(_ => "Center");

        foreach (var g in groups)
            await PersistGroupAsync(db, store, flow, g.Key, g.ToList(), ws, we, ct);
    }

    private async Task PersistGroupAsync(TzhjDbContext db, IServerBatchStore store, FlowType flow, string groupName, IReadOnlyList<SourceRow> rows, DateTime ws, DateTime we, CancellationToken ct)
    {
        var batchId = LocalPaths.BatchFolderName(ws, we);

        // 1. 检查物理磁盘是否存在
        var serverBatchDir = LocalPaths.ServerBatchDir(store.Root, flow, groupName, batchId);
        bool diskExists = Directory.Exists(serverBatchDir);

        // 2. 检查数据库记录
        var registry = await db.BatchRegistries.FirstOrDefaultAsync(b => b.BatchId == batchId && b.GroupName == groupName && b.Flow == flow, ct);

        // 只有当【物理存在】且【数据库记录也存在】时，才真正跳过
        if (diskExists && registry != null)
        {
            return;
        }

        _logger.LogInformation("Ingesting/Healing batch: {Flow} - {GroupName} - {BatchId} (Disk:{Disk}, DB:{Db})",
            flow, groupName, batchId, diskExists, registry != null);

        if (rows.Count == 0) return;

        var resp = new FetchResponse
        {
            Success = true,
            Flow = flow,
            EmployeeId = "SYSTEM",
            WindowStart = ws,
            WindowEnd = we,
            GroupName = groupName,
            Rows = rows.Select(r => new FetchRowDto
            {
                RowKey = r.RowKey,
                Values = r.Values,
                Drawings = r.Drawings.Select(d => new DrawingMeta
                {
                    DrawingId = d.DrawingId,
                    FileName = d.FileName,
                    MaterialCode = d.MaterialCode,
                    Size = d.Content.LongLength,
                }).ToList(),
            }).ToList(),
        };

        // 4. 强制持久化到服务器磁盘 (补齐物理文件)
        var drawings = rows.SelectMany(r => r.Drawings).Select(d => (d.FileName, d.Content));
        await store.SaveBatchAsync(resp, groupName, drawings, ct);

        // 5. 注册或刷新数据库状态
        if (registry == null)
        {
            registry = new BatchRegistry
            {
                BatchId = batchId,
                GroupName = groupName,
                Flow = flow,
                Status = BatchLocation.Todo,
                TotalRows = rows.Count, // 记录总行数
                CreatedAt = DateTime.UtcNow,
                LastModified = DateTime.UtcNow
            };
            db.BatchRegistries.Add(registry);
        }
        else
        {
            registry.TotalRows = rows.Count;
            registry.LastModified = DateTime.UtcNow; 
        }

        // 6. 记录系统日志
        db.ActivityLogs.Add(new ActivityLog
        {
            Action = "Ingest",
            EmployeeId = "SYSTEM",
            Flow = flow,
            GroupName = groupName,
            BatchId = batchId,
            ImpactCount = rows.Count,
            Status = "Success",
            Timestamp = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
    }
}
