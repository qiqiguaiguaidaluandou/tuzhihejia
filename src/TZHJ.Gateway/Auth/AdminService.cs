using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TZHJ.Core.Contracts.Http;
using TZHJ.Gateway.Stores;

namespace TZHJ.Gateway.Auth;

/// <summary>
/// 管理操作的唯一实现：用户、角色、挂角色、操作日志、可选组。
/// 同时被 REST 端点(/api/admin/*) 和 Blazor 管理后台调用，逻辑不重复。
/// actor = 执行操作的管理员工号（写审计用）；ip 可空（Blazor 交互态可能取不到）。
/// </summary>
public interface IAdminService
{
    Task<List<UserSummary>> ListUsersAsync(CancellationToken ct = default);
    Task<ApiResult> CreateUserAsync(CreateUserRequest req, string actor, string? ip, CancellationToken ct = default);
    Task<ApiResult> ResetPasswordAsync(string employeeId, string newPassword, string actor, string? ip, CancellationToken ct = default);
    Task<ApiResult> SetActiveAsync(string employeeId, bool isActive, string actor, string? ip, CancellationToken ct = default);
    Task<ApiResult> SetUserRolesAsync(string employeeId, List<int> roleIds, string actor, string? ip, CancellationToken ct = default);

    Task<List<RoleSummary>> ListRolesAsync(CancellationToken ct = default);
    Task<ApiResult> CreateRoleAsync(SaveRoleRequest req, string actor, string? ip, CancellationToken ct = default);
    Task<ApiResult> UpdateRoleAsync(int id, SaveRoleRequest req, string actor, string? ip, CancellationToken ct = default);
    Task<ApiResult> DeleteRoleAsync(int id, string actor, string? ip, CancellationToken ct = default);

    Task<AdminLogListResponse> QueryLogsAsync(string? employeeId, string? action, string? status, string? from, string? to, int? page, int? pageSize, CancellationToken ct = default);
    Task<List<GroupOption>> ListGroupsAsync(CancellationToken ct = default);
}

public sealed class AdminService : IAdminService
{
    private readonly TzhjDbContext _db;
    private readonly IPasswordService _passwords;

    public AdminService(TzhjDbContext db, IPasswordService passwords)
    {
        _db = db;
        _passwords = passwords;
    }

    // ---------- 用户 ----------

    public async Task<List<UserSummary>> ListUsersAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var users = await _db.AppUsers.OrderBy(u => u.EmployeeId).ToListAsync(ct);
        var userRoles = await _db.UserRoles
            .Include(ur => ur.Role!).ThenInclude(r => r.Permissions)
            .ToListAsync(ct);

        return users.Select(u =>
        {
            var mine = userRoles.Where(x => x.EmployeeId == u.EmployeeId).ToList();
            return new UserSummary
            {
                EmployeeId = u.EmployeeId,
                DisplayName = u.DisplayName,
                Department = u.Department,
                Position = u.Position,
                IsActive = u.IsActive,
                IsAdmin = u.IsAdmin,
                MustChangePassword = u.MustChangePassword,
                IsLocked = u.LockoutUntil is { } until && until > now,
                Roles = mine.Select(x => new RoleRef { Id = x.RoleId, Name = x.Role!.Name }).ToList(),
                EffectivePermissions = mine.SelectMany(x => x.Role!.Permissions)
                    .Select(p => new PermissionDto { Flow = p.Flow, GroupName = p.GroupName })
                    .DistinctBy(p => new { p.Flow, p.GroupName })
                    .ToList(),
            };
        }).ToList();
    }

    public async Task<ApiResult> CreateUserAsync(CreateUserRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        var empId = (req.EmployeeId ?? "").Trim();
        if (empId.Length == 0 || string.IsNullOrWhiteSpace(req.DisplayName))
            return ApiResult.Fail("工号和姓名不能为空。");
        if (string.IsNullOrEmpty(req.InitialPassword) || req.InitialPassword.Length < DbAuthService.MinPasswordLength)
            return ApiResult.Fail($"初始密码长度至少 {DbAuthService.MinPasswordLength} 位。");
        if (await _db.AppUsers.AnyAsync(u => u.EmployeeId == empId, ct))
            return ApiResult.Fail($"工号 {empId} 已存在。");

        _db.AppUsers.Add(new AppUser
        {
            EmployeeId = empId,
            DisplayName = req.DisplayName.Trim(),
            Department = req.Department,
            Position = req.Position,
            PasswordHash = _passwords.Hash(req.InitialPassword),
            IsActive = true,
            IsAdmin = req.IsAdmin,
            MustChangePassword = true,
        });
        Log(actor, ip, "AdminCreateUser", $"empId={empId}, admin={req.IsAdmin}");
        await _db.SaveChangesAsync(ct);
        return ApiResult.Ok($"已创建用户 {empId}。");
    }

    public async Task<ApiResult> ResetPasswordAsync(string employeeId, string newPassword, string actor, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < DbAuthService.MinPasswordLength)
            return ApiResult.Fail($"新密码长度至少 {DbAuthService.MinPasswordLength} 位。");

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.EmployeeId == employeeId, ct);
        if (user is null) return ApiResult.Fail("用户不存在。");

        user.PasswordHash = _passwords.Hash(newPassword);
        user.MustChangePassword = true;
        user.FailedAttempts = 0;
        user.LockoutUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
        Log(actor, ip, "AdminResetPassword", $"empId={employeeId}");
        await _db.SaveChangesAsync(ct);
        return ApiResult.Ok($"已重置 {employeeId} 的密码。");
    }

    public async Task<ApiResult> SetActiveAsync(string employeeId, bool isActive, string actor, string? ip, CancellationToken ct = default)
    {
        if (!isActive && string.Equals(employeeId, actor, StringComparison.Ordinal))
            return ApiResult.Fail("不能停用当前登录的管理员账号。");

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.EmployeeId == employeeId, ct);
        if (user is null) return ApiResult.Fail("用户不存在。");

        // 末位管理员保护：系统须始终保留至少一个启用的管理员，否则没人能进后台。
        // 仅在「启用中的管理员被停用」时校验；服务端兜底，绕过 UI(/api/admin/*) 也拦得住。
        if (!isActive && user.IsActive && user.IsAdmin)
        {
            var otherActiveAdmins = await _db.AppUsers
                .CountAsync(u => u.IsAdmin && u.IsActive && u.EmployeeId != employeeId, ct);
            if (otherActiveAdmins == 0)
                return ApiResult.Fail("系统至少需保留一个启用的管理员，不能停用。");
        }

        user.IsActive = isActive;
        if (isActive) { user.FailedAttempts = 0; user.LockoutUntil = null; }
        user.UpdatedAt = DateTime.UtcNow;
        Log(actor, ip, "AdminSetActive", $"empId={employeeId}, active={isActive}");
        await _db.SaveChangesAsync(ct);
        return ApiResult.Ok($"已{(isActive ? "启用" : "停用")} {employeeId}。");
    }

    public async Task<ApiResult> SetUserRolesAsync(string employeeId, List<int> roleIds, string actor, string? ip, CancellationToken ct = default)
    {
        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.EmployeeId == employeeId, ct);
        if (user is null) return ApiResult.Fail("用户不存在。");

        var ids = roleIds.Distinct().ToList();
        var validIds = await _db.Roles.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        var missing = ids.Except(validIds).ToList();
        if (missing.Count > 0) return ApiResult.Fail($"角色不存在：{string.Join(",", missing)}");

        var existing = await _db.UserRoles.Where(ur => ur.EmployeeId == employeeId).ToListAsync(ct);
        _db.UserRoles.RemoveRange(existing);
        foreach (var rid in validIds)
            _db.UserRoles.Add(new UserRole { EmployeeId = employeeId, RoleId = rid });

        Log(actor, ip, "AdminSetUserRoles", $"empId={employeeId}, roles=[{string.Join(",", validIds)}]");
        await _db.SaveChangesAsync(ct);
        return ApiResult.Ok($"已更新 {employeeId} 的角色。");
    }

    // ---------- 角色 ----------

    public async Task<List<RoleSummary>> ListRolesAsync(CancellationToken ct = default)
    {
        var roles = await _db.Roles.Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
        var counts = await _db.UserRoles.GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() }).ToListAsync(ct);

        return roles.Select(r => new RoleSummary
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            Permissions = r.Permissions.Select(p => new PermissionDto { Flow = p.Flow, GroupName = p.GroupName }).ToList(),
            UserCount = counts.FirstOrDefault(c => c.RoleId == r.Id)?.Count ?? 0,
        }).ToList();
    }

    public async Task<ApiResult> CreateRoleAsync(SaveRoleRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return ApiResult.Fail("角色名不能为空。");
        if (await _db.Roles.AnyAsync(r => r.Name == name, ct)) return ApiResult.Fail($"角色 {name} 已存在。");

        var role = new Role { Name = name, Description = req.Description, Permissions = BuildPermissions(req.Permissions) };
        _db.Roles.Add(role);
        Log(actor, ip, "AdminCreateRole", $"name={name}, ranges={role.Permissions.Count}");
        await _db.SaveChangesAsync(ct);
        return ApiResult.Ok($"已创建角色 {name}。");
    }

    public async Task<ApiResult> UpdateRoleAsync(int id, SaveRoleRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        var role = await _db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return ApiResult.Fail("角色不存在。");

        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return ApiResult.Fail("角色名不能为空。");
        if (await _db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct)) return ApiResult.Fail($"角色 {name} 已存在。");

        role.Name = name;
        role.Description = req.Description;
        role.UpdatedAt = DateTime.UtcNow;
        _db.RolePermissions.RemoveRange(role.Permissions);
        role.Permissions.Clear();
        foreach (var rp in BuildPermissions(req.Permissions)) role.Permissions.Add(rp);

        Log(actor, ip, "AdminUpdateRole", $"id={id}, name={name}, ranges={role.Permissions.Count}");
        await _db.SaveChangesAsync(ct);
        return ApiResult.Ok($"已更新角色 {name}。");
    }

    public async Task<ApiResult> DeleteRoleAsync(int id, string actor, string? ip, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return ApiResult.Fail("角色不存在。");

        _db.Roles.Remove(role);
        Log(actor, ip, "AdminDeleteRole", $"id={id}, name={role.Name}");
        await _db.SaveChangesAsync(ct);
        return ApiResult.Ok($"已删除角色 {role.Name}。");
    }

    // ---------- 日志 / 组 ----------

    public async Task<AdminLogListResponse> QueryLogsAsync(string? employeeId, string? action, string? status,
        string? from, string? to, int? page, int? pageSize, CancellationToken ct = default)
    {
        var q = _db.ActivityLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(employeeId)) q = q.Where(x => x.EmployeeId == employeeId);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(x => x.Action == action);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        if (TryDt(from, out var f)) { var fu = f.ToUniversalTime(); q = q.Where(x => x.Timestamp >= fu); }
        if (TryDt(to, out var t)) { var tu = t.ToUniversalTime(); q = q.Where(x => x.Timestamp <= tu); }

        var total = await q.CountAsync(ct);
        var p = page is > 0 ? page.Value : 1;
        var ps = pageSize is > 0 and <= 200 ? pageSize.Value : 50;
        var items = await q.OrderByDescending(x => x.Timestamp)
            .Skip((p - 1) * ps).Take(ps)
            .Select(x => new AdminLogEntry
            {
                Id = x.Id,
                Timestamp = x.Timestamp,
                EmployeeId = x.EmployeeId,
                Action = x.Action,
                Status = x.Status,
                Flow = x.Flow,
                GroupName = x.GroupName,
                BatchId = x.BatchId,
                ImpactCount = x.ImpactCount,
                Payload = x.Payload,
                ClientIp = x.ClientIp,
            })
            .ToListAsync(ct);

        return new AdminLogListResponse { Total = total, Items = items };
    }

    public async Task<List<GroupOption>> ListGroupsAsync(CancellationToken ct = default)
    {
        var pairs = await _db.BatchRegistries
            .Select(b => new { b.Flow, b.GroupName })
            .Distinct()
            .ToListAsync(ct);
        return pairs
            .Select(x => new GroupOption { Flow = x.Flow, GroupName = x.GroupName })
            .OrderBy(g => g.Flow).ThenBy(g => g.GroupName)
            .ToList();
    }

    // ---------- 辅助 ----------

    private void Log(string actor, string? ip, string action, string payload) =>
        _db.ActivityLogs.Add(new ActivityLog
        {
            Action = action,
            EmployeeId = actor,
            Status = "Success",
            Payload = payload,
            ClientIp = ip,
            Timestamp = DateTime.UtcNow,
        });

    private static List<RolePermission> BuildPermissions(List<PermissionDto> input) =>
        input.Where(p => !string.IsNullOrWhiteSpace(p.GroupName))
             .DistinctBy(p => new { p.Flow, p.GroupName })
             .Select(p => new RolePermission { Flow = p.Flow, GroupName = p.GroupName.Trim() })
             .ToList();

    private static bool TryDt(string? s, out DateTime dt)
    {
        if (string.IsNullOrWhiteSpace(s)) { dt = default; return false; }
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt);
    }
}
