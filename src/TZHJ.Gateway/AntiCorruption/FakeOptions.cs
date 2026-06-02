namespace TZHJ.Gateway.AntiCorruption;

/// <summary>
/// FakeDataSource 行为参数（来自 appsettings "Fake" 节）：
/// 便于无接口期把行数/缺图/回传失败等分支调全。
/// </summary>
public sealed class FakeOptions
{
    public int MinRowsPerBatch { get; set; } = 4;
    public int MaxRowsPerBatch { get; set; } = 9;

    /// <summary>某行图纸"缺失"的概率（触发完整性校验/挂起异常演示），0~1。</summary>
    public double DrawingMissingRate { get; set; } = 0.12;

    /// <summary>整批回传失败概率，0~1（0 = 永不失败）。</summary>
    public double SubmitFailureRate { get; set; } = 0.0;

    /// <summary>随机种子（叠加到确定性种子上，固定可复现）。</summary>
    public int Seed { get; set; } = 20260525;
}
