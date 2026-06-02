using Microsoft.Extensions.DependencyInjection;
using TZHJ.Core.Contracts;
using TZHJ.Infrastructure.Gateways.Http;
using TZHJ.Infrastructure.Gateways.Mock;
using TZHJ.Infrastructure.Options;
using TZHJ.Infrastructure.Storage;
using TZHJ.Infrastructure.Sync;

namespace TZHJ.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// 注册 Mock 网关 + 本地存储。这是"无接口先开发"的总开关：
    /// 真接口到位后，把下面四个网关换成 Http* 实现（或新增 AddTzhjHttpInfrastructure），存储部分不动。
    /// </summary>
    public static IServiceCollection AddTzhjMockInfrastructure(this IServiceCollection services, MockOptions mock)
    {
        services.AddSingleton(mock);
        services.AddSingleton(new LocalStorageOptions { Root = mock.LocalRoot });

        // 字段提供者：默认 schema，登录后可被下发配置覆盖（DefaultFieldProvider.Apply）。
        services.AddSingleton<DefaultFieldProvider>();
        services.AddSingleton<IFieldProvider>(sp => sp.GetRequiredService<DefaultFieldProvider>());

        // 对外边界 + 配置下发 + 审计查询：当前全为 Mock。
        services.AddSingleton<IAuthGateway, MockAuthGateway>();
        services.AddSingleton<IConfigGateway, MockConfigGateway>();
        services.AddSingleton<IDataGateway, MockDataGateway>();
        services.AddSingleton<ISubmitGateway, MockSubmitGateway>();
        services.AddSingleton<IAuditGateway, MockAuditGateway>();
        services.AddSingleton<IOperationLogGateway, MockOperationLogGateway>();

        // 补拉编排（手动/登录/会话内定时共用；纯逻辑，跨平台可测）。
        services.AddSingleton<BatchSyncService>();

        // 本地存储（非网关，真接口上线后不变）。
        services.AddSingleton<ILocalBatchStore, LocalBatchStore>();

        return services;
    }

    /// <summary>
    /// 注册真 HTTP 网关 + 本地存储（连后端 TZHJ.Gateway）。与 <see cref="AddTzhjMockInfrastructure"/> 并存，
    /// 由 App 按 UseMock 二选一。UI/ViewModel/ILocalBatchStore 一律不动——切换只在此处。
    /// </summary>
    public static IServiceCollection AddTzhjHttpInfrastructure(this IServiceCollection services, HttpOptions http)
    {
        services.AddSingleton(http);
        services.AddSingleton(new LocalStorageOptions { Root = http.LocalRoot });

        // 字段提供者：默认 schema，登录后被下发 ClientConfig 覆盖（DefaultFieldProvider.Apply）——机制同 Mock 模式。
        services.AddSingleton<DefaultFieldProvider>();
        services.AddSingleton<IFieldProvider>(sp => sp.GetRequiredService<DefaultFieldProvider>());

        // 令牌：HttpAuthGateway 登录成功写入，AuthTokenHandler 给后续受保护请求带上 Bearer。
        services.AddSingleton<IAuthTokenStore, AuthTokenStore>();
        services.AddTransient<AuthTokenHandler>();

        // 四个 typed HttpClient：同一 BaseUrl + 超时 + 令牌处理器。
        void Configure(HttpClient client)
        {
            client.BaseAddress = new Uri(http.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(http.TimeoutSeconds);
        }
        services.AddHttpClient<IAuthGateway, HttpAuthGateway>(Configure).AddHttpMessageHandler<AuthTokenHandler>();
        services.AddHttpClient<IConfigGateway, HttpConfigGateway>(Configure).AddHttpMessageHandler<AuthTokenHandler>();
        services.AddHttpClient<IDataGateway, HttpDataGateway>(Configure).AddHttpMessageHandler<AuthTokenHandler>();
        services.AddHttpClient<ISubmitGateway, HttpSubmitGateway>(Configure).AddHttpMessageHandler<AuthTokenHandler>();
        services.AddHttpClient<IAuditGateway, HttpAuditGateway>(Configure).AddHttpMessageHandler<AuthTokenHandler>();
        services.AddHttpClient<IOperationLogGateway, HttpOperationLogGateway>(Configure).AddHttpMessageHandler<AuthTokenHandler>();

        // 补拉编排（手动/登录/会话内定时共用；纯逻辑，跨平台可测）。
        services.AddSingleton<BatchSyncService>();

        // 本地存储（不变）。
        services.AddSingleton<ILocalBatchStore, LocalBatchStore>();

        return services;
    }
}
