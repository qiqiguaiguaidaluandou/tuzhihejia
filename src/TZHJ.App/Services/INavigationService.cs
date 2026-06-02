using Microsoft.Extensions.DependencyInjection;
using TZHJ.App.ViewModels;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.App.Services;

/// <summary>
/// 壳内导航：创建目标 ViewModel 并通过 <see cref="CurrentChanged"/> 通知 Shell 切换内容区。
/// 侧边栏（映射文件夹）与"开始作业"等都经此导航。
/// </summary>
public interface INavigationService
{
    event Action<ViewModelBase>? CurrentChanged;

    void ToBatchList(FlowType flow, BatchLocation location);
    void ToBatchWork(FlowType flow, BatchLocation location, string folderName);
    void ToExceptions(FlowType flow);
    void ToExceptionResolve(FlowType flow, ExceptionItem exception);
    void ToSchedule();
    void ToSettings();
    void ToOperationLog();
}

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _sp;

    public NavigationService(IServiceProvider sp) => _sp = sp;

    public event Action<ViewModelBase>? CurrentChanged;

    public void ToBatchList(FlowType flow, BatchLocation location) =>
        Raise(ActivatorUtilities.CreateInstance<BatchListViewModel>(_sp, flow, location));

    public void ToBatchWork(FlowType flow, BatchLocation location, string folderName) =>
        Raise(ActivatorUtilities.CreateInstance<BatchWorkViewModel>(_sp, flow, location, folderName));

    public void ToExceptions(FlowType flow) =>
        Raise(ActivatorUtilities.CreateInstance<ExceptionPoolViewModel>(_sp, flow));

    public void ToExceptionResolve(FlowType flow, ExceptionItem exception) =>
        Raise(ActivatorUtilities.CreateInstance<ExceptionResolveViewModel>(_sp, flow, exception));

    public void ToSchedule() =>
        Raise(ActivatorUtilities.CreateInstance<ScheduleViewModel>(_sp));

    public void ToSettings() =>
        Raise(ActivatorUtilities.CreateInstance<SettingsViewModel>(_sp));

    public void ToOperationLog() =>
        Raise(ActivatorUtilities.CreateInstance<OperationLogViewModel>(_sp));

    private ViewModelBase? _current;

    // 导航即加载：切到目标页后立即触发数据加载，不再依赖各 View 的 Loaded 生命周期
    // （ContentControl 切换内容时 Loaded 不一定按预期触发，导致列表空白、需手动刷新才有内容）。
    private async void Raise(ViewModelBase vm)
    {
        var previous = _current;
        _current = vm;
        CurrentChanged?.Invoke(vm);
        previous?.Dispose(); // 释放上一页资源（如 BatchListViewModel 的 FileSystemWatcher）
        try { await vm.LoadAsync(); }
        catch { /* 列表加载失败不应使应用崩溃；命令型操作（补拉/提交等）各自报错 */ }
    }
}
