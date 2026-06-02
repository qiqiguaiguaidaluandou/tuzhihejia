using System.Globalization;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Gateway.AntiCorruption;
using TZHJ.Gateway.Auth;
using TZHJ.Gateway.Stores;

namespace TZHJ.Gateway.Endpoints;

/// <summary>无状态网关的全部端点（§4 契约）。逻辑都在防腐层/存储后面，端点只做转换。</summary>
public static class ApiEndpoints
{
    public static void MapTzhjApi(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok("ok"));

        // 登录（不需令牌）→ 占位认证签发令牌
        app.MapPost("/api/auth/login", (LoginRequest req, IAuthService auth) =>
            Results.Json(auth.Login(req.EmployeeId, req.Password)));

        // 受保护组：令牌校验 + 把工号绑进 HttpContext（身份以 token 为准，D2）
        var api = app.MapGroup("/api").AddEndpointFilter<TokenEndpointFilter>();

        // 配置下发
        api.MapGet("/config", (HttpContext ctx, IConfigStore store) =>
            Results.Json(store.Get(ctx.GetEmployeeId())));

        // 取数（第一阶段）：行 + 图纸元数据（无字节）
        api.MapPost("/fetch", async (FetchRequest req, HttpContext ctx, IEbsPlmSource source, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            var rows = await source.FetchRowsAsync(req.Flow, empId, req.WindowStart, req.WindowEnd, ct);
            var resp = new FetchResponse
            {
                Success = true,
                Flow = req.Flow,
                EmployeeId = empId,
                WindowStart = req.WindowStart,
                WindowEnd = req.WindowEnd,
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
            return Results.Json(resp);
        });

        // 图纸下载（第二阶段）：流式字节。无状态——按 flow+window+drawingId 确定性重生。
        api.MapGet("/drawings", async (string flow, string windowStart, string windowEnd, string drawingId,
                                       HttpContext ctx, IEbsPlmSource source, CancellationToken ct) =>
        {
            if (!Enum.TryParse<FlowType>(flow, ignoreCase: true, out var flowType))
                return Results.BadRequest(new { message = $"未知流程: {flow}" });
            if (!TryParseDt(windowStart, out var ws) || !TryParseDt(windowEnd, out var we))
                return Results.BadRequest(new { message = "windowStart/windowEnd 格式应为往返(O)时间。" });

            var bytes = await source.OpenDrawingAsync(flowType, ctx.GetEmployeeId(), ws, we, drawingId, ct);
            return bytes is null
                ? Results.NotFound(new { message = "图纸不存在。" })
                : Results.File(bytes, "application/octet-stream", fileDownloadName: drawingId);
        });

        // 回传：整批正常行 → SRM/EBS，并记审计日志
        api.MapPost("/submit", async (SubmitRequest req, HttpContext ctx, ISubmitSink sink, IAuditStore audit, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            if (sink.ShouldFailBatch())
                return Results.Json(new SubmitResult { Success = false, Message = "（Fake）回传失败：演示用错误态，请重试。" });

            var rowResults = await sink.SubmitAsync(req.Flow, empId, req.Rows, ct);
            var target = req.Flow == FlowType.Pricing ? "SRM" : "EBS";
            var auditId = audit.Record(req.Flow, empId, req.BatchKey, req.WindowStart, req.WindowEnd, target, req.Rows.Count);

            return Results.Json(new SubmitResult
            {
                Success = true,
                AuditId = auditId,
                RowResults = rowResults.ToList(),
                Message = $"已回传 {req.Rows.Count} 行至 {target}。",
            });
        });

        // 用户操作日志：上报一条（回传/补回传成功后）。工号以令牌为准盖章，防伪造。
        api.MapPost("/oplog", (OperationLogEntry entry, HttpContext ctx, IOperationLogStore store) =>
        {
            var stamped = new OperationLogEntry
            {
                Operation = entry.Operation,
                ClientIp = entry.ClientIp,
                FormName = entry.FormName,
                OperatedAt = entry.OperatedAt,
                Flow = entry.Flow,
                EmployeeId = ctx.GetEmployeeId(),
            };
            store.Append(stamped);
            return Results.Ok();
        });

        // 用户操作日志：查本人记录（操作员"操作日志"页用；按令牌工号过滤——只能看到自己）。
        api.MapGet("/oplog/mine", (HttpContext ctx, IOperationLogStore store) =>
            Results.Json(new OperationLogListResponse { Items = store.ListByEmployee(ctx.GetEmployeeId()).ToList() }));

        // 登录补拉：查某窗口是否已成功回传过
        api.MapGet("/audit/exists", (string flow, string windowStart, string windowEnd, HttpContext ctx, IAuditStore audit) =>
        {
            if (!Enum.TryParse<FlowType>(flow, ignoreCase: true, out var flowType))
                return Results.BadRequest(new { message = $"未知流程: {flow}" });
            if (!TryParseDt(windowStart, out var ws) || !TryParseDt(windowEnd, out var we))
                return Results.BadRequest(new { message = "windowStart/windowEnd 格式应为往返(O)时间。" });

            var (exists, auditId) = audit.Find(flowType, ctx.GetEmployeeId(), ws, we);
            return Results.Json(new AuditExistsResponse { Exists = exists, AuditId = auditId });
        });
    }

    private static bool TryParseDt(string s, out DateTime dt) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt);
}
