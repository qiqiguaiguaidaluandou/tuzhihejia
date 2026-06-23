namespace TZHJ.Gateway.AntiCorruption;

/// <summary>防腐层内部模型：从源系统（EBS+PLM）取到的一行。与对外 wire DTO 解耦。</summary>
public sealed class SourceRow
{
    public required string RowKey { get; init; }

    /// <summary>组别（核价用，来自 EBS 响应的 GROUP_NAME）。挑图不分组，为 null。</summary>
    public string? GroupName { get; set; }

    public Dictionary<string, string?> Values { get; init; } = new();
    public List<SourceDrawing> Drawings { get; init; } = new();
}

/// <summary>防腐层内部模型：一张图纸（含字节）。对外取数只给元数据，字节走 /drawings 流式端点。</summary>
public sealed class SourceDrawing
{
    public required string DrawingId { get; init; }   // = FileName，天然唯一、含料号前缀
    public required string FileName { get; init; }
    public required string MaterialCode { get; init; }
    public required byte[] Content { get; init; }
}
