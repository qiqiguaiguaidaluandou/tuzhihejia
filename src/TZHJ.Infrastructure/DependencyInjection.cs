using Microsoft.Extensions.DependencyInjection;
using TZHJ.Core.Contracts;
using TZHJ.Infrastructure.Fields;
using TZHJ.Infrastructure.Gateways.Http;
using TZHJ.Infrastructure.Options;
using TZHJ.Infrastructure.Storage;
using TZHJ.Infrastructure.Sync;

namespace TZHJ.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// 注册真 HTTP 网关 + 本地存储（连后端 TZHJ.Gateway）。客户端唯一链路：
    /// UI/ViewModel/ILocalBatchStore 一律不动——外部接口到位后只换后端防腐层实现。
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
