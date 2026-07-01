using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Core.Logging;
using TZHJ.Core.Models;
using TZHJ.Core.Schemas;
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

        // 登录（不需令牌）→ 校验本地凭证、签发 JWT。权限由管理员显式维护（Deny-All 白名单，无自动放权）。
        app.MapPost("/api/auth/login", async (LoginRequest req, IAuthService auth, TzhjDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            var result = await auth.LoginAsync(req.EmployeeId, req.Password, ct);

            // 登录审计：成功/失败都记一条（失败工号取自请求体，仅作审计线索）。
            db.ActivityLogs.Add(new ActivityLog
            {
                Action = "Login",
                EmployeeId = result.Operator?.EmployeeId ?? (req.EmployeeId ?? "").Trim(),
                Status = result.Success ? "Success" : "Failed",
                Payload = result.Success ? null : result.Message,
                ClientIp = Ip(ctx),
                Timestamp = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);

            return Results.Json(result);
        });

        // 受保护组：令牌校验 + 把工号绑进 HttpContext（身份以 token 为准，D2）
        var api = app.MapGroup("/api").AddEndpointFilter<TokenEndpointFilter>();

        // 本人改密（需令牌；工号以令牌为准，忽略请求体工号）
        api.MapPost("/auth/change-password", async (ChangePasswordRequest req, IAuthService auth, TzhjDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            var (ok, msg) = await auth.ChangePasswordAsync(empId, req.OldPassword, req.NewPassword, ct);

            db.ActivityLogs.Add(new ActivityLog
            {
                Action = "ChangePassword",
                EmployeeId = empId,
                Status = ok ? "Success" : "Failed",
                Payload = ok ? null : msg,
                ClientIp = Ip(ctx),
                Timestamp = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);

            return Results.Json(ok ? ApiResult.Ok(msg) : ApiResult.Fail(msg));
        });

        MapAdminApi(api);

        // 配置下发
        api.MapGet("/config", (HttpContext ctx, IConfigStore store) =>
            Results.Json(store.Get(ctx.GetEmployeeId())));

        // --- Remote-First 同步与操作接口 ---

        // 1. 同步清单 (基于权限白名单过滤)
        api.MapGet("/sync/catalog", async (HttpContext ctx, IPermissionService perm, TzhjDbContext db, IServerBatchStore store, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();

            // 当前用户的有效数据范围（角色展开的 (流程,组) 并集）
            var grants = await perm.GetGrantsAsync(empId, ct);
            if (grants.Count == 0) return Results.Json(new List<BatchCatalogItem>());

            var allowedFlows = grants.Select(g => g.Flow).ToHashSet();
            var batches = await db.BatchRegistries
                .Where(b => allowedFlows.Contains(b.Flow))
                .ToListAsync(ct);

            var catalog = new List<BatchCatalogItem>();
            foreach (var b in batches)
            {
                var allowed = grants.Any(g => g.Flow == b.Flow && (g.GroupName == "*" || g.GroupName == b.GroupName));
                if (!allowed) continue;

                var files = await store.ListFilesAsync(b.Flow, b.GroupName, b.BatchId, ct);
                catalog.Add(new BatchCatalogItem
                {
                    BatchId = b.BatchId,
                    GroupName = b.GroupName,
                    Flow = b.Flow,
                    Status = b.Status,
                    TotalRows = b.TotalRows, // 填充总行数
                    LastModified = b.LastModified,
                    Files = files
                });
            }
            return Results.Json(catalog);
        });

        // 2. 文件下载 (带权限校验)
        api.MapGet("/sync/download", async (string flow, string groupName, string batchId, string fileName,
                                            HttpContext ctx, IPermissionService perm, IServerBatchStore store, CancellationToken ct) =>
        {
            if (!Enum.TryParse<FlowType>(flow, ignoreCase: true, out var flowType))
                return Results.BadRequest("Invalid flow.");

            var empId = ctx.GetEmployeeId();
            if (!await perm.HasAccessAsync(empId, flowType, groupName, ct)) return Results.Forbid();

            var stream = store.OpenFile(flowType, groupName, batchId, fileName);
            return stream is null ? Results.NotFound() : Results.File(stream, "application/octet-stream", fileName);
        });

        // 3. 行数据更新 (Remote-First 核心，带权限校验)
        api.MapPost("/batch/update-row", async ([FromBody] UpdateRowRequest req, HttpContext ctx, IPermissionService perm, IServerBatchStore store, TzhjDbContext db, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            if (!await perm.HasAccessAsync(empId, req.Flow, req.GroupName, ct)) return Results.Forbid();

            var registry = await db.BatchRegistries.FirstOrDefaultAsync(b => b.BatchId == req.BatchId && b.GroupName == req.GroupName && b.Flow == req.Flow, ct);
            if (registry == null) 
            {
                return Results.NotFound(new { message = $"云端找不到该批次记录。BatchId: {req.BatchId}, Group: {req.GroupName}" });
            }

            await store.UpdateExcelRowAsync(registry.Flow, req.GroupName, req.BatchId, req.RowKey, req.Values, ct);
            registry.LastModified = DateTime.UtcNow;

            db.ActivityLogs.Add(new ActivityLog
            {
                Action = "UpdateRow",
                EmployeeId = empId,
                Flow = req.Flow,
                GroupName = req.GroupName,
                BatchId = req.BatchId,
                ImpactCount = 1,
                Status = "Success",
                Payload = $"Row: {req.RowKey}",
                ClientIp = Ip(ctx),
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // 4. 异常挂起
        api.MapPost("/batch/suspend-exception", async ([FromBody] SuspendExceptionRequest req, HttpContext ctx, IPermissionService perm, TzhjDbContext db, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            if (!await perm.HasAccessAsync(empId, req.Flow, req.GroupName, ct)) return Results.Forbid();

            var entity = new ExceptionEntity
            {
                GroupName = req.GroupName,
                Flow = req.Flow,
                RowKey = req.RowKey,
                MaterialCode = req.MaterialCode,
                DisplayName = req.DisplayName,
                SourceBatch = req.BatchId,
                Reason = req.Reason,
                Status = RowStatus.Exception,
                SuspendedAt = DateTime.UtcNow
            };
            db.Exceptions.Add(entity);

            db.ActivityLogs.Add(new ActivityLog
            {
                Action = "Suspend",
                EmployeeId = empId,
                Flow = req.Flow,
                GroupName = req.GroupName,
                BatchId = req.BatchId,
                ImpactCount = 1,
                Status = "Success",
                Payload = $"Row: {req.RowKey}, Reason: {req.Reason}",
                ClientIp = Ip(ctx),
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // 5. 异常池查询 (组内共享视野)
        api.MapGet("/sync/exceptions", async (HttpContext ctx, IPermissionService perm, TzhjDbContext db, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            var grants = await perm.GetGrantsAsync(empId, ct);

            var list = new List<ExceptionItem>();
            foreach (var g in grants)
            {
                var groupExceptions = await db.Exceptions
                    .Where(e => e.Flow == g.Flow && (g.GroupName == "*" || e.GroupName == g.GroupName) && e.Status == RowStatus.Exception)
                    .Select(e => new ExceptionItem
                    {
                        Flow = e.Flow,
                        RowKey = e.RowKey,
                        GroupName = e.GroupName,
                        MaterialCode = e.MaterialCode,
                        DisplayName = e.DisplayName,
                        SourceBatch = e.SourceBatch,
                        Reason = e.Reason,
                        SuspendedAt = e.SuspendedAt
                    })
                    .ToListAsync(ct);
                list.AddRange(groupExceptions);
            }

            return Results.Json(list.DistinctBy(x => new { x.SourceBatch, x.RowKey }));
        });

        // 6. 异常处理 (补回传或撤销)
        api.MapPost("/batch/resolve-exception", async (string flow, string groupName, string batchId, string rowKey, HttpContext ctx, IPermissionService perm, TzhjDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<FlowType>(flow, ignoreCase: true, out var flowType))
                return Results.BadRequest("Invalid flow.");

            var empId = ctx.GetEmployeeId();
            if (!await perm.HasAccessAsync(empId, flowType, groupName, ct)) return Results.Forbid();

            var entity = await db.Exceptions.FirstOrDefaultAsync(e => e.Flow == flowType && e.SourceBatch == batchId && e.RowKey == rowKey && e.GroupName == groupName, ct);
            if (entity == null && groupName != "Default")
                entity = await db.Exceptions.FirstOrDefaultAsync(e => e.Flow == flowType && e.SourceBatch == batchId && e.RowKey == rowKey && e.GroupName == "Default", ct);

            if (entity != null)
            {
                entity.Status = RowStatus.Uploaded; // 逻辑删除/处理完成
                entity.ResolvedAt = DateTime.UtcNow;
                entity.ResolvedBy = empId;

                db.ActivityLogs.Add(new ActivityLog
                {
                    Action = "Resolve",
                    EmployeeId = empId,
                    Flow = entity.Flow,
                    GroupName = groupName,
                    BatchId = batchId,
                    ImpactCount = 1,
                    Status = "Success",
                    Payload = $"Row: {rowKey}",
                    ClientIp = Ip(ctx),
                    Timestamp = DateTime.UtcNow
                });

                await db.SaveChangesAsync(ct);
            }
            return Results.Ok();
        });

        // 7. 按行重新获取图纸：取数时 PLM 无图、挂异常后图纸补上了 → 复用 PLM 取图接口（支持单料号）重新拉取，
        //    落到来源批次服务端文件夹；客户端据返回文件名同步到本地。Found=false 表示 PLM 仍无图。
        api.MapPost("/batch/refetch-drawing", async ([FromBody] RefetchDrawingRequest req, HttpContext ctx, IPermissionService perm, PlmClient plm, IServerBatchStore store, TzhjDbContext db, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            if (!await perm.HasAccessAsync(empId, req.Flow, req.GroupName, ct)) return Results.Forbid();

            var registry = await db.BatchRegistries.FirstOrDefaultAsync(b => b.BatchId == req.BatchId && b.GroupName == req.GroupName && b.Flow == req.Flow, ct);
            if (registry == null)
                return Results.NotFound(new { message = $"云端找不到该批次记录。BatchId: {req.BatchId}, Group: {req.GroupName}" });

            var code = req.MaterialCode.Trim();

            // 复用现成 PLM 取图接口（FetchDrawingsAsync 本就支持批量/单个料号），只问这一个料号。
            var byCode = await plm.FetchDrawingsAsync(new[] { code }, ct);
            byCode.TryGetValue(code, out var picked);
            var fetched = picked ?? new List<(string FileName, byte[] Content)>();

            if (fetched.Count == 0)
            {
                // PLM 仍无图：不改批次、不留成功痕迹，回 Found=false。
                return Results.Json(new RefetchDrawingResult { Found = false, Message = "PLM 中暂无该料号图纸。" });
            }

            await store.AppendDrawingsAsync(req.Flow, req.GroupName, req.BatchId, fetched, ct);
            registry.LastModified = DateTime.UtcNow;

            db.ActivityLogs.Add(new ActivityLog
            {
                Action = "RefetchDrawing",
                EmployeeId = empId,
                Flow = req.Flow,
                GroupName = req.GroupName,
                BatchId = req.BatchId,
                ImpactCount = fetched.Count,
                Status = "Success",
                Payload = $"Row: {req.RowKey}, Files: {string.Join(", ", fetched.Select(f => f.FileName))}",
                ClientIp = Ip(ctx),
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);

            return Results.Json(new RefetchDrawingResult
            {
                Found = true,
                Files = fetched.Select(f => f.FileName).ToList(),
            });
        });

        // 回传：整批正常行 → SRM/EBS（成功判定 + 幂等 + 失败留痕，见 changes/009）
        api.MapPost("/submit", async ([FromBody] SubmitRequest req, HttpContext ctx, IPermissionService perm, ISubmitSink sink, TzhjDbContext db, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            if (!await perm.HasAccessAsync(empId, req.Flow, req.GroupName, ct)) return Results.Forbid();

            var target = req.Flow == FlowType.Pricing ? "SRM" : "EBS";
            var retryTag = req.IsExceptionRetry ? "，重新回传" : ""; // 落入 payload，供日志区分「重新回传」

            var registry = await db.BatchRegistries.FirstOrDefaultAsync(b => b.BatchId == req.BatchKey && b.Flow == req.Flow && b.GroupName == req.GroupName, ct);
            if (registry == null) return Results.NotFound(new { message = "云端找不到批次记录。" });

            // 幂等：已提交过的批次不再回传外部系统，直接回显既有 AuditId（防网络重发 / 重复点提交）。
            // 例外：异常行"重新回传"显式绕过——批次虽已 Done，但失败行需要真正重推外部系统。
            if (registry.Status == BatchLocation.Done && !req.IsExceptionRetry)
            {
                return Results.Json(new SubmitResult
                {
                    Success = true,
                    AuditId = registry.AuditId,
                    Message = "该批次此前已提交，未重复回传。",
                });
            }

            // 瞬时整批失败（ShouldFailBatch 模拟，或下方 SubmitAsync 抛 HTTP/解析异常）→ 不置 Done，可重试。
            IReadOnlyList<SubmitRowResult> rowResults;
            if (sink.ShouldFailBatch())
            {
                db.ActivityLogs.Add(new ActivityLog
                {
                    Action = "Submit", EmployeeId = empId, Flow = req.Flow, GroupName = req.GroupName, BatchId = req.BatchKey,
                    ImpactCount = 0, Status = "Failed",
                    Payload = $"Target: {target}{retryTag}, 整批回传失败（可重试）",
                    ClientIp = Ip(ctx), Timestamp = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                return Results.Json(new SubmitResult { Success = false, Message = $"回传 {target} 失败，批次未完成，可重试。" });
            }

            try
            {
                rowResults = await sink.SubmitAsync(req.Flow, empId, req.Rows, ct);
            }
            catch (Exception ex)
            {
                db.ActivityLogs.Add(new ActivityLog
                {
                    Action = "Submit", EmployeeId = empId, Flow = req.Flow, GroupName = req.GroupName, BatchId = req.BatchKey,
                    ImpactCount = 0, Status = "Failed",
                    Payload = $"Target: {target}{retryTag}, 回传调用失败（可重试）: {ex.Message}",
                    ClientIp = Ip(ctx), Timestamp = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                return Results.Json(new SubmitResult { Success = false, Message = $"回传 {target} 失败，批次未完成，可重试。" });
            }

            // 逐行永久失败（如"物料编码不存在"）：写入云端异常表（与手动挂起同一张表/事务），并记留痕日志，但不阻断批次。
            // 必须落 DB——Remote-First：同步服务会全量覆盖本地异常池，只写本地不落库则同步后丢失。
            var failedRows = rowResults.Where(r => !r.Success).ToList();
            if (failedRows.Count > 0)
            {
                var rowByKey = req.Rows.GroupBy(r => r.RowKey).ToDictionary(g => g.Key, g => g.First());
                foreach (var fr in failedRows)
                {
                    rowByKey.TryGetValue(fr.RowKey, out var src);

                    // 重传场景：该行的异常记录已存在，更新原因即可，避免重复入池。
                    var existing = req.IsExceptionRetry
                        ? await db.Exceptions.FirstOrDefaultAsync(
                            e => e.Flow == req.Flow && e.SourceBatch == req.BatchKey
                                 && e.RowKey == fr.RowKey && e.GroupName == req.GroupName
                                 && e.Status == RowStatus.Exception, ct)
                        : null;
                    if (existing != null)
                    {
                        existing.Reason = $"{target}回传失败：{fr.Message}";
                        existing.SuspendedAt = DateTime.UtcNow;
                        continue;
                    }

                    db.Exceptions.Add(new ExceptionEntity
                    {
                        GroupName = req.GroupName,
                        Flow = req.Flow,
                        RowKey = fr.RowKey,
                        MaterialCode = src?.Values.GetValueOrDefault(FieldSchemas.PricingKeys.MaterialCode) ?? fr.RowKey,
                        DisplayName = src?.Values.GetValueOrDefault(FieldSchemas.PricingKeys.Name)
                                   ?? src?.Values.GetValueOrDefault(FieldSchemas.PricingKeys.MaterialDesc),
                        SourceBatch = req.BatchKey,
                        Reason = $"{target}回传失败：{fr.Message}",
                        Status = RowStatus.Exception,
                        SuspendedAt = DateTime.UtcNow,
                    });
                }

                var detail = string.Join("; ", failedRows.Select(r => $"{r.RowKey}({r.Message})"));
                db.ActivityLogs.Add(new ActivityLog
                {
                    Action = "Submit", EmployeeId = empId, Flow = req.Flow, GroupName = req.GroupName, BatchId = req.BatchKey,
                    ImpactCount = failedRows.Count, Status = "Failed",
                    Payload = $"Target: {target}{retryTag}, 永久失败行(已转入异常池，不重试): {detail}",
                    ClientIp = Ip(ctx), Timestamp = DateTime.UtcNow,
                });
            }

            // 批次完成：置 Done + 结构化审计（窗口起止 / AuditId 落独立列，供 audit/exists 精确查）。
            var successCount = rowResults.Count(r => r.Success);
            var auditId = $"AUDIT-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..36];
            registry.Status = BatchLocation.Done;
            registry.AuditId = auditId;
            registry.LastModified = DateTime.UtcNow;

            db.ActivityLogs.Add(new ActivityLog
            {
                Action = "Submit",
                EmployeeId = empId,
                Flow = req.Flow,
                GroupName = req.GroupName,
                BatchId = req.BatchKey,
                ImpactCount = successCount,
                Status = "Success",
                WindowStart = req.WindowStart.ToUniversalTime(),
                WindowEnd = req.WindowEnd.ToUniversalTime(),
                AuditId = auditId,
                Payload = $"Target: {target}{retryTag}",
                ClientIp = Ip(ctx),
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync(ct);

            return Results.Json(new SubmitResult
            {
                Success = true,
                AuditId = auditId,
                RowResults = rowResults.ToList(),
                Message = failedRows.Count > 0
                    ? $"已回传 {successCount} 行至 {target}；{failedRows.Count} 行失败已留痕（如物料不存在，不重试）。"
                    : $"已回传 {req.Rows.Count} 行至 {target}。",
            });
        });

        // 用户操作日志：客户端上报本人行为（工号以令牌为准，防伪造）。
        // 此前缺失该端点，客户端 fire-and-forget 上报被静默丢弃——此处补上。
        api.MapPost("/oplog", (OperationLogEntry req, HttpContext ctx, IOperationLogStore store) =>
        {
            var empId = ctx.GetEmployeeId();
            store.Append(new OperationLogEntry
            {
                Operation = req.Operation,
                FormName = req.FormName,
                OperatedAt = req.OperatedAt,
                Flow = req.Flow,
                ClientIp = req.ClientIp ?? Ip(ctx), // 优先客户端本机局域网 IP，缺失则回退连接 IP
                EmployeeId = empId,                 // 以令牌盖章，忽略请求体工号
            });
            return Results.Ok();
        });

        // 用户操作日志：查本人记录（操作员视角——只看本人的业务操作 + 登录/改密，
        // 精简友好：中文动作名 + 结果，隐藏 IP 和内部技术 payload。全量审计走后台 /api/admin/logs）。
        api.MapGet("/oplog/mine", async (HttpContext ctx, TzhjDbContext db, CancellationToken ct) =>
        {
            var empId = ctx.GetEmployeeId();
            var visible = LogText.OperatorActions;
            var rows = await db.ActivityLogs
                .Where(x => x.EmployeeId == empId && visible.Contains(x.Action))
                .OrderByDescending(x => x.Timestamp)
                .Select(x => new { x.Action, x.Status, x.Flow, x.BatchId, x.Payload, x.Timestamp })
                .ToListAsync(ct);

            var items = rows.Select(r => new OperationLogEntry
            {
                EmployeeId = empId,
                Operation = r.Action == "Submit"
                    ? LogText.SubmitLabel(r.Flow, LogText.IsResubmit(r.Payload)) // 提交回传/重新回传 + 目标(SRM/EBS)
                    : LogText.ActionLabel(r.Action),
                FormName = r.BatchId ?? "",
                Flow = r.Flow ?? FlowType.Pricing,
                Result = r.Status == "Failed" ? "失败" : "成功",
                OperatedAt = DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc).ToLocalTime(),
                // ClientIp 不返回：操作员视图精简，不展示 IP
            }).ToList();

            return Results.Json(new OperationLogListResponse { Items = items });
        });

        // 登录补拉判据：按结构化窗口列精确查成功回传记录（009，取代 Payload 文本匹配）
        api.MapGet("/audit/exists", async (string flow, string windowStart, string windowEnd, HttpContext ctx, TzhjDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<FlowType>(flow, ignoreCase: true, out var flowType)) return Results.BadRequest();
            if (!TryParseDt(windowStart, out var ws) || !TryParseDt(windowEnd, out var we)) return Results.BadRequest();

            var wsUtc = ws.ToUniversalTime();
            var weUtc = we.ToUniversalTime();
            var empId = ctx.GetEmployeeId();

            var hit = await db.ActivityLogs
                .Where(r => r.Flow == flowType && r.EmployeeId == empId && r.Action == "Submit" && r.Status == "Success"
                            && r.WindowStart == wsUtc && r.WindowEnd == weUtc)
                .OrderByDescending(r => r.Timestamp)
                .FirstOrDefaultAsync(ct);

            return Results.Json(new AuditExistsResponse
            {
                Exists = hit != null,
                AuditId = hit?.AuditId
            });
        });
    }

    /// <summary>管理端（/api/admin/*，仅启用中的管理员）：用户与权限维护。</summary>
    private static void MapAdminApi(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").AddEndpointFilter<AdminEndpointFilter>();

        // 端点都是薄封装，业务逻辑在 IAdminService（与 Blazor 管理后台共用）。
        // 约定：业务失败走 200 + ApiResult.Success=false（与 login/change-password 一致）。

        // ---- 用户 ----
        admin.MapGet("/users", async (IAdminService svc, CancellationToken ct) =>
            Results.Json(await svc.ListUsersAsync(ct)));

        admin.MapPost("/users", async (CreateUserRequest req, IAdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Json(await svc.CreateUserAsync(req, ctx.GetEmployeeId(), Ip(ctx), ct)));

        admin.MapPost("/users/{employeeId}/reset-password", async (string employeeId, ResetPasswordRequest req, IAdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Json(await svc.ResetPasswordAsync(employeeId, req.NewPassword, ctx.GetEmployeeId(), Ip(ctx), ct)));

        admin.MapPost("/users/{employeeId}/active", async (string employeeId, SetActiveRequest req, IAdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Json(await svc.SetActiveAsync(employeeId, req.IsActive, ctx.GetEmployeeId(), Ip(ctx), ct)));

        admin.MapPut("/users/{employeeId}/roles", async (string employeeId, SetUserRolesRequest req, IAdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Json(await svc.SetUserRolesAsync(employeeId, req.RoleIds, ctx.GetEmployeeId(), Ip(ctx), ct)));

        // ---- 角色 ----
        admin.MapGet("/roles", async (IAdminService svc, CancellationToken ct) =>
            Results.Json(await svc.ListRolesAsync(ct)));

        admin.MapPost("/roles", async (SaveRoleRequest req, IAdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Json(await svc.CreateRoleAsync(req, ctx.GetEmployeeId(), Ip(ctx), ct)));

        admin.MapPut("/roles/{id:int}", async (int id, SaveRoleRequest req, IAdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Json(await svc.UpdateRoleAsync(id, req, ctx.GetEmployeeId(), Ip(ctx), ct)));

        admin.MapDelete("/roles/{id:int}", async (int id, IAdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Json(await svc.DeleteRoleAsync(id, ctx.GetEmployeeId(), Ip(ctx), ct)));

        // ---- 操作日志 / 可选组 ----
        admin.MapGet("/logs", async (string? employeeId, string? action, string? status, string? from, string? to,
                                     int? page, int? pageSize, IAdminService svc, CancellationToken ct) =>
            Results.Json(await svc.QueryLogsAsync(employeeId, action, status, from, to, page, pageSize, ct)));

        admin.MapGet("/groups", async (IAdminService svc, CancellationToken ct) =>
            Results.Json(await svc.ListGroupsAsync(ct)));
    }

    /// <summary>
    /// 操作日志的"操作电脑IP"：优先采信客户端在 X-Client-Ip 头里自报的本机局域网 IP
    /// （同机/反向代理/NAT 下连接 IP 都不等于操作员真实工位 IP）。头由客户端自报、可伪造，
    /// 仅作内网审计线索；校验为合法 IP 才采用，否则回退连接 IP。浏览器（管理后台）不带此头，
    /// 自然回退到连接 IP。
    /// </summary>
    private static string? Ip(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Client-Ip", out var reported)
            && System.Net.IPAddress.TryParse(reported.ToString(), out _))
            return reported.ToString();

        return ctx.Connection.RemoteIpAddress?.ToString();
    }

    private static bool TryParseDt(string s, out DateTime dt) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt);
}
