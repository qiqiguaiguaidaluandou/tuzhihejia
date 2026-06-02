using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TZHJ.App.Services;
using TZHJ.Core.Contracts;
using TZHJ.Infrastructure.Fields;
using TZHJ.Infrastructure.Options;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 登录：调 IAuthGateway 认证 → 调 IConfigGateway 取配置 → 应用字段集/本地根 → 写会话。
/// 成功后触发 <see cref="LoginSucceeded"/>，由 LoginWindow 切到主界面。
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IAuthGateway _auth;
    private readonly IConfigGateway _config;
    private readonly ISession _session;
    private readonly DefaultFieldProvider _fieldProvider;
    private readonly LocalStorageOptions _storage;

    public LoginViewModel(
        IAuthGateway auth,
        IConfigGateway config,
        ISession session,
        DefaultFieldProvider fieldProvider,
        LocalStorageOptions storage)
    {
        _auth = auth;
        _config = config;
        _session = session;
        _fieldProvider = fieldProvider;
        _storage = storage;
    }

    [ObservableProperty] private string _employeeId = "108645";
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    public event EventHandler? LoginSucceeded;

    [RelayCommand]
    private async Task LoginAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var auth = await _auth.LoginAsync(EmployeeId.Trim(), Password);
            if (!auth.Success || auth.Operator is null)
            {
                Error = auth.Message ?? "登录失败。";
                return;
            }

            // 取下发配置，应用字段集与本地根（字段配置化在此生效）。
            var config = await _config.GetConfigAsync(auth.Operator.EmployeeId);
            _fieldProvider.Apply(config);
            _storage.Root = config.LocalRoot;
            _session.SignIn(auth.Operator, config);

            LoginSucceeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Error = FriendlyError.Describe(ex, "登录");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
