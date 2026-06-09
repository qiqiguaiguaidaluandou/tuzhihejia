using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TZHJ.Gateway.AntiCorruption;
using TZHJ.Gateway.Auth;
using TZHJ.Gateway.Endpoints;
using TZHJ.Gateway.Stores;

var builder = WebApplication.CreateBuilder(args);

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

// ---------- 认证/授权（占位） ----------
builder.Services.AddSingleton<ITokenService, FakeTokenService>();
builder.Services.AddSingleton<IAuthService, FakeAuthService>();

// ---------- 防腐层接缝（本期 Fake；真接口到位后只换这两个实现 = 路线图 B1/B2） ----------
builder.Services.AddSingleton<FakeDataSource>();
builder.Services.AddSingleton<IEbsPlmSource>(sp => sp.GetRequiredService<FakeDataSource>());
builder.Services.AddSingleton<ISubmitSink>(sp => sp.GetRequiredService<FakeDataSource>());

// ---------- 存储（骨架内存；上线落 PostgreSQL） ----------
builder.Services.AddSingleton<IConfigStore, InMemoryConfigStore>();
builder.Services.AddScoped<IAuditStore, PgAuditStore>();

// 用户操作日志：落文件（JSONL），管理员服务器侧直接查；操作员经 /api/oplog/mine 只查本人。
var opLogOptions = new OperationLogOptions();
builder.Configuration.GetSection("OperationLog").Bind(opLogOptions);
builder.Services.AddSingleton(opLogOptions);
builder.Services.AddSingleton<IOperationLogStore, FileOperationLogStore>();

var app = builder.Build();

app.MapTzhjApi();

app.Run();
