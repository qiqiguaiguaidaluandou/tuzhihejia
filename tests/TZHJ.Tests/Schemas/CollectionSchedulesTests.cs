using TZHJ.Core.Schemas;

namespace TZHJ.Tests.Schemas;

/// <summary>时间窗规则与日界（核价 15:30/15:31、挑图 18:00/18:01）。</summary>
public class CollectionSchedulesTests
{
    [Fact]
    public void Pricing_has_two_windows_with_1530_1531_boundary()
    {
        var ps = CollectionSchedules.Pricing;
        Assert.Equal(2, ps.Count);

        var am = ps.Single(w => w.Name == "上午批");
        var pm = ps.Single(w => w.Name == "下午批");

        Assert.Equal(new TimeOnly(9, 30), am.EndTime);    // 上午批止 09:30
        Assert.Equal(-1, am.StartDayOffset);              // 起跨到前一天
        Assert.Equal(new TimeOnly(9, 31), pm.StartTime);  // 下午批起 09:31（界 15:30/15:31）
        Assert.Equal(new TimeOnly(15, 30), pm.EndTime);   // 下午批止 15:30
    }

    [Fact]
    public void DrawingSelection_has_three_windows_with_1800_1801_boundary()
    {
        var ds = CollectionSchedules.DrawingSelection;
        Assert.Equal(3, ds.Count);

        var am = ds.Single(w => w.Name == "上午批");
        var pm = ds.Single(w => w.Name == "下午批");
        Assert.Equal(new TimeOnly(14, 30), am.EndTime);   // 上午批止 14:30（挑图午间界 14:30/14:31，触发 15:00）
        Assert.Equal(new TimeOnly(14, 31), pm.StartTime); // 下午批起 14:31
        Assert.Equal(new TimeOnly(18, 0), pm.EndTime);    // 下午批止 18:00（日界 18:00/18:01，触发 18:30）
    }

    [Fact]
    public void Resolve_maps_relative_rule_to_absolute_window()
    {
        var anchor = new DateOnly(2026, 5, 27);

        var pm = CollectionSchedules.Pricing.Single(w => w.Name == "下午批");
        var (start, end) = pm.Resolve(anchor);
        Assert.Equal(new DateTime(2026, 5, 27, 9, 31, 0), start);
        Assert.Equal(new DateTime(2026, 5, 27, 15, 30, 0), end);

        var am = CollectionSchedules.Pricing.Single(w => w.Name == "上午批");
        var (s2, e2) = am.Resolve(anchor);
        Assert.Equal(new DateTime(2026, 5, 26, 15, 31, 0), s2); // 起跨到前一天
        Assert.Equal(new DateTime(2026, 5, 27, 9, 30, 0), e2);
    }

    [Fact]
    public void TriggerTime_is_one_minute_after_close()
    {
        var pm = CollectionSchedules.Pricing.Single(w => w.Name == "下午批");
        Assert.Equal(new TimeOnly(15, 31), pm.TriggerTime); // 15:30 + 1min
    }
}
