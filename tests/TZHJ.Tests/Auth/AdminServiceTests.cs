using Microsoft.EntityFrameworkCore;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Gateway.Auth;
using TZHJ.Gateway.Stores;

namespace TZHJ.Tests.Auth;

public class AdminServiceTests
{
    private static readonly PasswordService Passwords = new();

    private static TzhjDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TzhjDbContext>()
            .UseInMemoryDatabase("admin-" + Guid.NewGuid().ToString("N")).Options);

    private static AdminService Svc(TzhjDbContext db) => new(db, Passwords);

    private static SaveRoleRequest Role(string name, params (FlowType, string)[] ranges) => new()
    {
        Name = name,
        Permissions = ranges.Select(r => new PermissionDto { Flow = r.Item1, GroupName = r.Item2 }).ToList(),
    };

    [Fact]
    public async Task CreateUser_persists_and_forces_password_change()
    {
        using var db = NewDb();
        var r = await Svc(db).CreateUserAsync(new CreateUserRequest
        {
            EmployeeId = "10086", DisplayName = "张三", InitialPassword = "Init@12345", IsAdmin = false,
        }, "admin", null);

        Assert.True(r.Success);
        var u = await db.AppUsers.SingleAsync();
        Assert.True(u.MustChangePassword);
        Assert.True(Passwords.Verify(u.PasswordHash, "Init@12345"));
    }

    [Fact]
    public async Task CreateUser_rejects_duplicate_and_short_password()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "10086", DisplayName = "张三", InitialPassword = "Init@12345" }, "admin", null);

        Assert.False((await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "10086", DisplayName = "重复", InitialPassword = "Init@12345" }, "admin", null)).Success);
        Assert.False((await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "10087", DisplayName = "短", InitialPassword = "short" }, "admin", null)).Success);
    }

    [Fact]
    public async Task Role_create_and_assign_shows_in_user_effective_permissions()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "10086", DisplayName = "张三", InitialPassword = "Init@12345" }, "admin", null);
        await svc.CreateRoleAsync(Role("核价员-组1", (FlowType.Pricing, "组1")), "admin", null);
        await svc.CreateRoleAsync(Role("挑图员", (FlowType.DrawingSelection, "*")), "admin", null);

        var roles = await svc.ListRolesAsync();
        Assert.Equal(2, roles.Count);
        var ids = roles.Select(r => r.Id).ToList();

        var assign = await svc.SetUserRolesAsync("10086", ids, "admin", null);
        Assert.True(assign.Success);

        var user = (await svc.ListUsersAsync()).Single();
        Assert.Equal(2, user.Roles.Count);
        Assert.Contains(user.EffectivePermissions, p => p.Flow == FlowType.Pricing && p.GroupName == "组1");
        Assert.Contains(user.EffectivePermissions, p => p.Flow == FlowType.DrawingSelection && p.GroupName == "*");

        // 角色挂载人数随之变化
        var pricingRole = (await svc.ListRolesAsync()).First(r => r.Name == "核价员-组1");
        Assert.Equal(1, pricingRole.UserCount);
    }

    [Fact]
    public async Task Assign_unknown_role_is_rejected()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "10086", DisplayName = "张三", InitialPassword = "Init@12345" }, "admin", null);
        Assert.False((await svc.SetUserRolesAsync("10086", new List<int> { 999 }, "admin", null)).Success);
    }

    [Fact]
    public async Task Duplicate_role_name_is_rejected()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateRoleAsync(Role("核价员-组1", (FlowType.Pricing, "组1")), "admin", null);
        Assert.False((await svc.CreateRoleAsync(Role("核价员-组1", (FlowType.Pricing, "组2")), "admin", null)).Success);
    }

    [Fact]
    public async Task Delete_role_cascades_user_assignments()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "10086", DisplayName = "张三", InitialPassword = "Init@12345" }, "admin", null);
        await svc.CreateRoleAsync(Role("核价员-组1", (FlowType.Pricing, "组1")), "admin", null);
        var id = (await svc.ListRolesAsync()).Single().Id;
        await svc.SetUserRolesAsync("10086", new List<int> { id }, "admin", null);

        Assert.True((await svc.DeleteRoleAsync(id, "admin", null)).Success);
        Assert.Empty(await db.UserRoles.ToListAsync());
        Assert.Empty((await svc.ListUsersAsync()).Single().Roles);
    }

    [Fact]
    public async Task Cannot_deactivate_self()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "admin", DisplayName = "管理员", InitialPassword = "Admin@12345", IsAdmin = true }, "admin", null);
        var r = await svc.SetActiveAsync("admin", false, "admin", null);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Cannot_deactivate_last_active_admin()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "admin", DisplayName = "管理员", InitialPassword = "Admin@12345", IsAdmin = true }, "system", null);

        // 由另一个工号发起停用（绕开"不能停用自己"），唯一管理员仍应被末位保护拦下
        var r = await svc.SetActiveAsync("admin", false, "someone-else", null);
        Assert.False(r.Success);
        Assert.True((await db.AppUsers.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Can_deactivate_admin_when_another_active_admin_exists()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "a1", DisplayName = "管理员1", InitialPassword = "Admin@12345", IsAdmin = true }, "system", null);
        await svc.CreateUserAsync(new CreateUserRequest { EmployeeId = "a2", DisplayName = "管理员2", InitialPassword = "Admin@12345", IsAdmin = true }, "system", null);

        var r = await svc.SetActiveAsync("a1", false, "a2", null);
        Assert.True(r.Success);
        Assert.False((await db.AppUsers.SingleAsync(u => u.EmployeeId == "a1")).IsActive);
    }

    [Fact]
    public async Task QueryLogs_filters_by_action_and_pages()
    {
        using var db = NewDb();
        for (var i = 0; i < 5; i++)
            db.ActivityLogs.Add(new ActivityLog { Action = "Login", EmployeeId = "10086", Status = "Success", Timestamp = DateTime.UtcNow.AddMinutes(-i) });
        db.ActivityLogs.Add(new ActivityLog { Action = "Submit", EmployeeId = "10086", Status = "Success", Timestamp = DateTime.UtcNow });
        db.SaveChanges();

        var logins = await Svc(db).QueryLogsAsync(null, "Login", null, null, null, 1, 2);
        Assert.Equal(5, logins.Total);
        Assert.Equal(2, logins.Items.Count); // 分页：每页 2
    }
}
