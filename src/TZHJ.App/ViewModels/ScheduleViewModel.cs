using System.Collections.ObjectModel;
using TZHJ.App.Services;
using TZHJ.Core.Models;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 采集计划（定时拉取规则，只读展示）。两流程各一套：核价 2 窗、挑图 3 窗。触发 = 窗口关闭后（登录态）。
/// </summary>
public sealed class ScheduleViewModel : ViewModelBase
{
    public ScheduleViewModel(ISession session)
    {
        Title = "采集计划（定时拉取规则）";
        PricingRows = Build(session.Config.PricingWindows);
        DrawingRows = Build(session.Config.DrawingSelectionWindows);
    }

    public ObservableCollection<ScheduleRowVM> PricingRows { get; }
    public ObservableCollection<ScheduleRowVM> DrawingRows { get; }

    private static ObservableCollection<ScheduleRowVM> Build(IReadOnlyList<CollectionWindow> windows)
    {
        var rows = new ObservableCollection<ScheduleRowVM>();
        foreach (var w in windows)
            rows.Add(new ScheduleRowVM(w));
        return rows;
    }
}

public sealed class ScheduleRowVM
{
    public ScheduleRowVM(CollectionWindow w)
    {
        Name = w.Name;
        RangeText = w.StartDayOffset == 0
            ? $"今天 {w.StartTime:HH\\:mm} ~ {w.EndTime:HH\\:mm}"
            : $"昨天 {w.StartTime:HH\\:mm} ~ 今天 {w.EndTime:HH\\:mm}";
        TriggerText = $"登录补拉 > 1min; 在线/手动 > 30min";
        EnabledText = w.Enabled ? "启用" : "停用";
    }

    public string Name { get; }
    public string RangeText { get; }
    public string TriggerText { get; }
    public string EnabledText { get; }
}
