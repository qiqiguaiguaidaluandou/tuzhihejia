using System.Text;
using TZHJ.Core.Contracts;
using TZHJ.Core.Enums;
using TZHJ.Core.Schemas;

namespace TZHJ.Gateway.AntiCorruption;

/// <summary>
/// 占位防腐层：移植自客户端 MockDataGateway/MockSubmitGateway 的确定性造数。
/// 关键改动：种子用**稳定哈希**（非 string.GetHashCode，后者按进程随机化），
/// 使取数(/fetch)与图纸下载(/drawings)两次独立请求、乃至重启后都能重生同一字节——契合无状态网关。
/// 真接口到位后整类替换为调 EBS/PLM/SRM 的实现（路线图 B1/B2）。
/// </summary>
public sealed class FakeDataSource : IEbsPlmSource, ISubmitSink
{
    private static readonly string[] PricingNames = { "支架", "法兰盘", "轴套", "盖板", "连接件", "端盖", "导轨", "压板", "齿条", "销轴" };
    private static readonly string[] PricingSpecs = { "Q235 3mm", "45# 调质", "不锈钢304", "铝6061", "Q355", "铸铁 HT250", "20CrMnTi", "黄铜H62" };
    private static readonly string[] DrawingNames = { "异形件", "薄壁壳体", "支撑座", "齿轮箱体", "主轴", "泵体", "阀座", "联轴器", "导向块", "法兰" };
    private static readonly string[] DrawingMats = { "钛合金", "铝合金", "铸钢", "铸铝", "40Cr", "铸铁", "304不锈钢", "QT600" };
    private static readonly string[] ProductLines = { "产品线A", "产品线B", "产品线C" };
    private static readonly string[] Departments = { "机加一车间", "机加二车间", "钣金车间" };
    private static readonly string[] Applicants = { "王工", "赵工", "钱工", "孙工" };
    private static readonly string[] ChangeStates = { "N", "N", "N", "Y" };

    private readonly FakeOptions _options;

    public FakeDataSource(FakeOptions options) => _options = options;

    // ===== IEbsPlmSource =====

    public Task<IReadOnlyList<SourceRow>> FetchRowsAsync(
        FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd, CancellationToken ct = default)
    {
        IReadOnlyList<SourceRow> rows = GenerateBatch(flow, employeeId, windowStart, windowEnd);
        return Task.FromResult(rows);
    }

    public Task<byte[]?> OpenDrawingAsync(
        FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd, string drawingId, CancellationToken ct = default)
    {
        var rows = GenerateBatch(flow, employeeId, windowStart, windowEnd);
        var drawing = rows.SelectMany(r => r.Drawings).FirstOrDefault(d => d.DrawingId == drawingId);
        return Task.FromResult(drawing?.Content);
    }

    // ===== ISubmitSink =====

    public Task<IReadOnlyList<SubmitRowResult>> SubmitAsync(
        FlowType flow, string employeeId, IReadOnlyList<SubmitRow> rows, CancellationToken ct = default)
    {
        IReadOnlyList<SubmitRowResult> results = rows
            .Select(r => new SubmitRowResult { RowKey = r.RowKey, Success = true })
            .ToList();
        return Task.FromResult(results);
    }

    public bool ShouldFailBatch() => _options.SubmitFailureRate > 0 && Random.Shared.NextDouble() < _options.SubmitFailureRate;

    // ===== 确定性生成（取数与图纸下载共用，保证字节一致） =====

    private IReadOnlyList<SourceRow> GenerateBatch(FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd)
    {
        var seed = StableSeed($"{_options.Seed}|{employeeId}|{(int)flow}|{windowStart:O}|{windowEnd:O}");
        var rng = new Random(seed);

        var count = rng.Next(_options.MinRowsPerBatch, _options.MaxRowsPerBatch + 1);
        var rows = new List<SourceRow>(count);
        for (var i = 0; i < count; i++)
        {
            if (flow == FlowType.Pricing)
            {
                var row = BuildPricingRow(rng);
                // 造数阶段把行交替分到两组，模拟真实 EBS 响应里的 GROUP_NAME（采集服务按此分组落盘）。
                row.GroupName = i % 2 == 0 ? "组1" : "组2";
                rows.Add(row);
            }
            else
            {
                rows.Add(BuildDrawingRow(rng, windowStart)); // 挑图不分组
            }
        }
        return rows;
    }

    private SourceRow BuildPricingRow(Random rng)
    {
        var code = $"M-{10000 + rng.Next(200, 999)}";
        var name = Pick(rng, PricingNames) + (char)('A' + rng.Next(0, 5));
        var spec = Pick(rng, PricingSpecs);

        var values = new Dictionary<string, string?>
        {
            [FieldSchemas.PricingKeys.MaterialCode] = code,
            [FieldSchemas.PricingKeys.Model] = $"GB-{rng.Next(1000, 9999)}",
            [FieldSchemas.PricingKeys.Name] = $"{name} / {spec}",
            [FieldSchemas.PricingKeys.MaterialDesc] = $"{name} / {spec}（{code}）",
            [FieldSchemas.PricingKeys.DemandQty] = rng.Next(1, 500).ToString(),
            [FieldSchemas.PricingKeys.HasChange] = Pick(rng, ChangeStates),
            [FieldSchemas.PricingKeys.TargetPrice] = null, // 待填列：操作员手填
        };

        return new SourceRow { RowKey = code, Values = values, Drawings = BuildDrawings(rng, code, name) };
    }

    private SourceRow BuildDrawingRow(Random rng, DateTime windowStart)
    {
        var code = $"P-{2000 + rng.Next(1, 99)}";
        var name = Pick(rng, DrawingNames);
        var mat = Pick(rng, DrawingMats);
        var ebsId = $"EBS-{windowStart:yyyyMMdd}-{rng.Next(1, 9999):D4}";

        var values = new Dictionary<string, string?>
        {
            [FieldSchemas.DrawingKeys.EbsId] = ebsId,
            [FieldSchemas.DrawingKeys.InvOrg] = rng.Next(2) == 0 ? "本部" : "分厂",
            [FieldSchemas.DrawingKeys.SourceNo] = $"PO-{rng.Next(100000, 999999)}",
            [FieldSchemas.DrawingKeys.Project] = $"项目{(char)('A' + rng.Next(0, 4))}",
            [FieldSchemas.DrawingKeys.ProductLine] = Pick(rng, ProductLines),
            [FieldSchemas.DrawingKeys.PlanNo] = $"FA-{rng.Next(1000, 9999)}",
            [FieldSchemas.DrawingKeys.DeptDesc] = Pick(rng, Departments),
            [FieldSchemas.DrawingKeys.MaterialCode] = code,
            [FieldSchemas.DrawingKeys.MaterialDesc] = $"{name} / {mat}",
            [FieldSchemas.DrawingKeys.CurrentQty] = rng.Next(1, 200).ToString(),
            [FieldSchemas.DrawingKeys.CreateDate] = windowStart.AddDays(-rng.Next(1, 5)).ToString("yyyy-MM-dd"),
            [FieldSchemas.DrawingKeys.DemandDate] = windowStart.AddDays(rng.Next(7, 30)).ToString("yyyy-MM-dd"),
            [FieldSchemas.DrawingKeys.Applicant] = Pick(rng, Applicants),
            [FieldSchemas.DrawingKeys.Remark] = rng.Next(4) == 0 ? "加急" : "",
            [FieldSchemas.DrawingKeys.HasChange] = Pick(rng, ChangeStates),
            [FieldSchemas.DrawingKeys.CanMachine] = null, // 待填列：操作员手填（是/否）
        };

        return new SourceRow { RowKey = ebsId, Values = values, Drawings = BuildDrawings(rng, code, name) };
    }

    private List<SourceDrawing> BuildDrawings(Random rng, string materialCode, string name)
    {
        var list = new List<SourceDrawing>();
        if (rng.NextDouble() < _options.DrawingMissingRate)
            return list; // 无图纸 → UI 标"缺失"

        var safeName = MakeSafe(name);
        var pdfName = $"{materialCode}__{safeName}.pdf";
        var stepName = $"{materialCode}__{safeName}.step";
        list.Add(new SourceDrawing { DrawingId = pdfName, FileName = pdfName, MaterialCode = materialCode, Content = MakePlaceholderPdf($"{materialCode}  (MOCK DRAWING)") });
        list.Add(new SourceDrawing { DrawingId = stepName, FileName = stepName, MaterialCode = materialCode, Content = MakePlaceholderStep(materialCode) });
        return list;
    }

    private static string Pick(Random rng, string[] pool) => pool[rng.Next(pool.Length)];

    private static string MakeSafe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>稳定哈希（FNV-1a 32 位）：不随进程变化，保证两次请求重生同一批数据。</summary>
    private static int StableSeed(string s)
    {
        unchecked
        {
            const uint offset = 2166136261, prime = 16777619;
            var hash = offset;
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                hash ^= b;
                hash *= prime;
            }
            return (int)hash;
        }
    }

    /// <summary>结构最简的合法单页 PDF（双击能打开看到一行文字），文字保持 ASCII。</summary>
    private static byte[] MakePlaceholderPdf(string asciiText)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        var content = $"BT /F1 16 Tf 36 150 Td ({Escape(asciiText)}) Tj ET";
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Escape(string s) => s.Replace("(", "\\(").Replace(")", "\\)");

    private static byte[] MakePlaceholderStep(string code)
    {
        var step = "ISO-10303-21;\nHEADER;\n" +
                   $"FILE_DESCRIPTION(('TZHJ MOCK STEP {code}'),'2;1');\n" +
                   $"FILE_NAME('{code}.step','2026-05-25T00:00:00',(''),(''),'mock','TZHJ','');\n" +
                   "FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n";
        return Encoding.UTF8.GetBytes(step);
    }
}
