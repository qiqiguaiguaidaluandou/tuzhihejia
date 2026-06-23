using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TZHJ.Core.Contracts;
using TZHJ.Gateway.AntiCorruption;
using TZHJ.Gateway.Auth;
using TZHJ.Gateway.Components;
using TZHJ.Gateway.Endpoints;
using TZHJ.Gateway.Stores;
using TZHJ.Gateway;

var builder = WebApplication.CreateBuilder(args);

// 支持本地私密配置覆盖（不提交 git）
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// JSON：枚举走字符串（FlowType=Pricing/DrawingSelection），web 默认 camelCase。客户端 HttpJson 用同一套。
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---------- 数据库 (PostgreSQL) ----------
var conn = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<TzhjDbContext>(options => options.UseNpgsql(conn));

// ---------- 选项（来自 appsettings） ----------
var fake = new FakeOptions();
builder.Configuration.GetSection("Fake").Bind(fake);
builder.Services.AddSingleton(fake);

var configOptions = new ConfigStoreOptions();
builder.Configuration.GetSection("Config").Bind(configOptions);
builder.Services.AddSingleton(configOptions);

var storageOptions = new ServerStorageOptions();
builder.Configuration.GetSection("Storage").Bind(storageOptions);
builder.Services.AddSingleton(storageOptions);

// ---------- 认证/授权（本地凭证，管理员维护） ----------
var jwtOptions = new JwtOptions();
builder.Configuration.GetSection("Jwt").Bind(jwtOptions);
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<ITokenService, JwtTokenService>();
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IAuthService, DbAuthService>();
builder.Services.AddScoped<IAdminService, AdminService>();

// ---------- 管理后台（Blazor Server + Cookie 鉴权，仅管理员） ----------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication("AdminCookie")
    .AddCookie("AdminCookie", o =>
    {
        o.Cookie.Name = "tzhj_admin";
        o.LoginPath = "/admin/login";
        o.AccessDeniedPath = "/admin/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(o =>
    o.AddPolicy("AdminOnly", p => p.RequireClaim("tzhj:isAdmin", "true")));

// ---------- 字段提供者 ----------
builder.Services.AddSingleton<IFieldProvider, ServerFieldProvider>();

// ---------- 存储服务 ----------
builder.Services.AddSingleton<IServerBatchStore, FileServerBatchStore>();
builder.Services.AddSingleton<IConfigStore, InMemoryConfigStore>();
builder.Services.AddScoped<IAuditStore, PgAuditStore>();
builder.Services.AddScoped<IOperationLogStore, PgOperationLogStore>();

// ---------- 后台采集服务 (模拟主动获取) ----------
builder.Services.AddHostedService<DataIngestionService>();

// ---------- 防腐层接缝 ----------
// EBS 取数：Ebs:Enabled=true 走真实接口(EbsPlmSource)，否则继续用 FakeDataSource 造数。
var ebsOptions = new EbsOptions();
builder.Configuration.GetSection("Ebs").Bind(ebsOptions);
builder.Services.AddSingleton(ebsOptions);
builder.Services.AddSingleton<EbsTokenProvider>();

builder.Services.AddSingleton<FakeDataSource>();
// 回传(SRM/EBS)仍由 FakeDataSource 顶替（路线图 B2，本期不涉及）。
builder.Services.AddSingleton<ISubmitSink>(sp => sp.GetRequiredService<FakeDataSource>());

if (ebsOptions.Enabled)
    builder.Services.AddHttpClient<IEbsPlmSource, EbsPlmSource>();
else
    builder.Services.AddSingleton<IEbsPlmSource>(sp => sp.GetRequiredService<FakeDataSource>());

var app = builder.Build();

// 启动种子：仅当用户表为空且配置了 Admin 段时，建第一个管理员（引导入口）。
await SeedBootstrapAdminAsync(app);

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapTzhjApi();
app.MapAdminAuthEndpoints();                 // /admin/login、/admin/logout（Cookie 登入登出）
app.MapRazorComponents<App>()                // /admin/* 管理后台页面
   .AddInteractiveServerRenderMode();

app.Run();

// 引导管理员：避免"有库无人能登录"。生产请在 appsettings.local.json 配 Admin 段并改默认口令。
static async Task SeedBootstrapAdminAsync(WebApplication app)
{
    var section = app.Configuration.GetSection("Admin");
    var empId = section["EmployeeId"];
    var password = section["Password"];
    if (string.IsNullOrWhiteSpace(empId) || string.IsNullOrWhiteSpace(password))
        return; // 未配置则不种子

    using var scope = app.Services.CreateScope();
    var sp = scope.ServiceProvider;
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("BootstrapAdmin");
    try
    {
        var db = sp.GetRequiredService<TzhjDbContext>();
        if (!await db.Database.CanConnectAsync())
        {
            logger.LogWarning("种子管理员跳过：数据库不可达。");
            return;
        }
        if (await db.AppUsers.AnyAsync())
            return; // 已有用户，不再种子

        var pwd = sp.GetRequiredService<IPasswordService>();
        db.AppUsers.Add(new AppUser
        {
            EmployeeId = empId.Trim(),
            DisplayName = section["DisplayName"] ?? "系统管理员",
            Department = section["Department"],
            Position = section["Position"],
            PasswordHash = pwd.Hash(password),
            IsActive = true,
            IsAdmin = true,
            MustChangePassword = true, // 首登强制改默认口令
        });
        await db.SaveChangesAsync();
        logger.LogInformation("已创建引导管理员 {EmployeeId}（首登须改密）。", empId);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "种子管理员失败（可能尚未应用迁移），可稍后手动创建。");
    }
}
