using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TZHJ.App.Services;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 操作日志页：操作员只看自己的操作记录（操作按钮/电脑IP/时间/表单名称）。
/// 数据来自后端，按令牌工号过滤——看不到别人的；管理员看全部走服务器侧。
/// </summary>
public sealed partial class OperationLogViewModel : ViewModelBase
{
    private readonly IOperationLogGateway _opLog;

    public OperationLogViewModel(IOperationLogGateway opLog)
    {
        _opLog = opLog;
        Title = "操作日志 · 我的操作记录";
    }

    public ObservableCollection<OperationLogEntry> Items { get; } = new();

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Items.Clear();
            foreach (var item in await _opLog.ListMineAsync())
                Items.Add(item);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();
}
