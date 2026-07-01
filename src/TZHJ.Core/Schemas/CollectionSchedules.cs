using System.Collections.Generic;
using System.Linq;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Core.Schemas;

/// <summary>
/// 客户端采集时间窗（相对规则），投影自唯一权威的 <see cref="IngestionSchedules"/>。
/// 供 /config 下发、客户端登录补拉（BatchSyncService.ClosedWindows）与批次键使用；
/// 与采集服务的排程始终一致，不再单独维护一套。
/// </summary>
public static class CollectionSchedules
{
    public static IReadOnlyList<CollectionWindow> Pricing { get; } =
        IngestionSchedules.For(FlowType.Pricing).Select(s => s.ToWindow()).ToList();

    public static IReadOnlyList<CollectionWindow> DrawingSelection { get; } =
        IngestionSchedules.For(FlowType.DrawingSelection).Select(s => s.ToWindow()).ToList();

    public static IReadOnlyList<CollectionWindow> For(FlowType flow) =>
        flow == FlowType.Pricing ? Pricing : DrawingSelection;
}
