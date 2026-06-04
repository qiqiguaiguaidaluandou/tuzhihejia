using TZHJ.Core.Enums;

namespace TZHJ.Core.Models;

/// <summary>
/// 采集时间窗定义（相对规则）。批次 = 一个采集时间窗，触发 = 窗口关闭后（登录态）。
/// 两流程各一套、可配置：
///   核价（2窗，日界 15:30/15:31）：昨天15:31~今天09:30、今天09:31~15:30；
///   挑图（3窗，日界 18:00/18:01）：昨天18:01~今天09:30、今天09:31~15:30、今天15:31~18:00。
/// </summary>
public sealed class CollectionWindow
{
    public required string Name { get; init; }

    public required FlowType Flow { get; init; }

    /// <summary>窗口起：相对锚定日的天偏移（0=当天，-1=前一天）。</summary>
    public int StartDayOffset { get; init; }

    /// <summary>窗口起：时刻。</summary>
    public required TimeOnly StartTime { get; init; }

    /// <summary>窗口止：时刻（锚定日当天）。</summary>
    public required TimeOnly EndTime { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>最小触发阈值（窗口关闭后一分钟，主要用于登录补拉）。在线自动获取则需 30 分钟。</summary>
    public TimeOnly TriggerTime => EndTime.AddMinutes(1);

    /// <summary>
    /// 把相对规则在某锚定日（窗口关闭日）解算成具体的 [起,止]。
    /// </summary>
    public (DateTime Start, DateTime End) Resolve(DateOnly anchorDate)
    {
        var start = anchorDate.AddDays(StartDayOffset).ToDateTime(StartTime);
        var end = anchorDate.ToDateTime(EndTime);
        return (start, end);
    }
}
