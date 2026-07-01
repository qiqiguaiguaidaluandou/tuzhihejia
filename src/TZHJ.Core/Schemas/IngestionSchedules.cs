using System.Collections.Generic;
using System.Linq;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Core.Schemas;

/// <summary>
/// 一条采集排程：到达「触发时刻」时，取 [窗口起, 窗口止] 的数据。窗口止固定在触发当天。
/// </summary>
public sealed record IngestionSchedule
{
    public required FlowType Flow { get; init; }

    public required string Name { get; init; }

    /// <summary>采集触发时刻（每天该时刻触发一次；比窗口结束晚约 30 分钟，留数据落定缓冲）。</summary>
    public required TimeOnly TriggerTime { get; init; }

    /// <summary>窗口起：相对触发日的天偏移（0=当天，-1=昨天）。</summary>
    public int StartDayOffset { get; init; }

    public required TimeOnly StartTime { get; init; }

    /// <summary>窗口止：时刻（触发当天）。</summary>
    public required TimeOnly EndTime { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>投影成相对规则窗口（客户端补拉 / 批次键 用；不含采集触发时刻）。</summary>
    public CollectionWindow ToWindow() => new()
    {
        Name = Name,
        Flow = Flow,
        StartDayOffset = StartDayOffset,
        StartTime = StartTime,
        EndTime = EndTime,
        Enabled = Enabled,
    };
}

/// <summary>
/// 采集排程的唯一权威定义（见 docs/接口文档.md 与 docs/changes/013）。
/// 采集服务(DataIngestionService)、客户端补拉(经 CollectionSchedules)、后台「采集计划」展示 全部以此为准，
/// 避免多处各写一套造成窗口不一致。各流程窗口首尾相接、整天闭合。
/// </summary>
public static class IngestionSchedules
{
    public static IReadOnlyList<IngestionSchedule> All { get; } = new[]
    {
        // 图纸核价：10:00 取「昨天15:31 ~ 今天09:30」；16:00 取「今天09:31 ~ 15:30」。
        new IngestionSchedule { Flow = FlowType.Pricing, Name = "上午批", TriggerTime = new(10, 0), StartDayOffset = -1, StartTime = new(15, 31), EndTime = new(9, 30) },
        new IngestionSchedule { Flow = FlowType.Pricing, Name = "下午批", TriggerTime = new(16, 0), StartDayOffset = 0, StartTime = new(9, 31), EndTime = new(15, 30) },
        // 机加中心挑图：10:00 取「昨天18:01 ~ 今天09:30」；15:00 取「今天09:31 ~ 14:30」；18:30 取「今天14:31 ~ 18:00」。
        new IngestionSchedule { Flow = FlowType.DrawingSelection, Name = "夜间批", TriggerTime = new(10, 0), StartDayOffset = -1, StartTime = new(18, 1), EndTime = new(9, 30) },
        new IngestionSchedule { Flow = FlowType.DrawingSelection, Name = "上午批", TriggerTime = new(15, 0), StartDayOffset = 0, StartTime = new(9, 31), EndTime = new(14, 30) },
        new IngestionSchedule { Flow = FlowType.DrawingSelection, Name = "下午批", TriggerTime = new(18, 30), StartDayOffset = 0, StartTime = new(14, 31), EndTime = new(18, 0) },
    };

    public static IEnumerable<IngestionSchedule> For(FlowType flow) => All.Where(s => s.Flow == flow);
}
