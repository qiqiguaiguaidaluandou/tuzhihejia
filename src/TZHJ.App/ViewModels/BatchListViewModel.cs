using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TZHJ.App.Services;
using TZHJ.Core.Contracts;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;
using TZHJ.Infrastructure.Sync;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 批次列表（待处理 / 已处理）。直接映射本地文件夹：扫描目录 → 列表。
/// 待处理可"手动补拉"（演示取数→落本地）与"开始作业"。
/// </summary>
public sealed partial class BatchListViewModel : ViewModelBase
{
    private readonly ILocalBatchStore _store;
    private readonly BatchSyncService _sync;
    private readonly ISession _session;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialog;
    private readonly IExplorerService _explorer;

    private readonly FlowType _flow;
    private readonly BatchLocation _location;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _refreshDebounce;

    public BatchListViewModel(
        ILocalBatchStore store, BatchSyncService sync, ISession session,
        INavigationService nav, IDialogService dialog, IExplorerService explorer,
        FlowType flow, BatchLocation location)
    {
        _store = store;
        _sync = sync;
        _session = session;
        _nav = nav;
        _dialog = dialog;
        _explorer = explorer;
        _flow = flow;
        _location = location;

        var flowName = flow == FlowType.Pricing ? "图纸核价" : "挑图纸";
        var locName = location == BatchLocation.Todo ? "待处理" : "已处理";
        Title = $"{flowName} · {locName}";
        IsTodo = location == BatchLocation.Todo;
        LocationRootPath = LocalPaths.LocationRoot(_session.Config.LocalRoot, flow, session.Operator.EmployeeId, location);
        StartWatching();
    }

    public bool IsTodo { get; }
    public string LocationRootPath { get; }

    public ObservableCollection<BatchRowVM> Batches { get; } = new();

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Batches.Clear();
            var list = await _store.ListBatchesAsync(_flow, _session.Operator.EmployeeId, _location);
            foreach (var b in list)
                Batches.Add(new BatchRowVM(b));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private void OpenLocationFolder() => _explorer.OpenFolder(LocationRootPath);

    [RelayCommand]
    private void OpenBatchFolder(BatchRowVM row) => _explorer.OpenFolder(row.Batch.FolderPath);

    [RelayCommand]
    private void OpenBatch(BatchRowVM row) =>
        _nav.ToBatchWork(_flow, _location, row.Batch.FolderName);

    /// <summary>手动补拉：经 BatchSyncService 补齐"已关闭、本地无、审计未命中"的窗（与登录补拉/会话内定时同一套逻辑）。</summary>
    [RelayCommand]
    private async Task ManualFetch()
    {
        if (!IsTodo) return;
        IsBusy = true;
        try
        {
            var windows = _session.Config.WindowsFor(_flow);
            var result = await _sync.SyncAsync(_flow, _session.Operator.EmployeeId, windows, DateTime.Now);
            await LoadAsync();

            if (result.Fetched > 0) _dialog.Success($"补拉完成，新增 {result.Fetched} 个批次。");
            else if (result.Failed > 0) _dialog.Error($"补拉部分失败：{result.Failed} 个窗口取数未成功，请重试。");
            else _dialog.Info("本地已是最新，无需补拉。");
        }
        catch (Exception ex)
        {
            _dialog.Error(FriendlyError.Describe(ex, "补拉"));
        }
        finally { IsBusy = false; }
    }

    // ---------- 文件夹即真相源：FileSystemWatcher 同步 ----------

    /// <summary>监视本位置目录的子目录增删/改名，外部变化去抖后自动刷新列表。
    /// 删除走甲案：如实同步（该批次从列表消失）、不阻止、不做持久作废标记。</summary>
    private void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(LocationRootPath); // 目录可能尚未建（无批次时），监视前确保存在
            _watcher = new FileSystemWatcher(LocationRootPath)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnFolderChanged;
            _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null; // 监视起不来不影响列表本身（仍可手动刷新）
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    /// <summary>去抖：文件操作常成串到达，合并到最后一次后再在 UI 线程刷新。</summary>
    private void ScheduleRefresh()
    {
        _refreshDebounce?.Cancel();
        _refreshDebounce = new CancellationTokenSource();
        var token = _refreshDebounce.Token;
        _ = Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try { await Task.Delay(300, token); }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            try { await LoadAsync(); }
            catch { /* 刷新失败不崩溃 */ }
        });
    }

    public override void Dispose()
    {
        _refreshDebounce?.Cancel();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFolderChanged;
            _watcher.Deleted -= OnFolderChanged;
            _watcher.Renamed -= OnFolderChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}

/// <summary>批次列表的一行（只读投影）。</summary>
public sealed class BatchRowVM
{
    public BatchRowVM(Batch batch) => Batch = batch;

    public Batch Batch { get; }

    public string WindowText => $"{Batch.WindowStart:MM-dd HH:mm} ~ {Batch.WindowEnd:MM-dd HH:mm}";
    public string FolderName => Batch.FolderName;
    public int MaterialCount => Batch.MaterialCount;

    public string ProgressText => Batch.Location == BatchLocation.Todo
        ? $"{Batch.DoneCount + Batch.ExceptionCount} / {Batch.MaterialCount}"
        : $"正常 {Batch.DoneCount} / 异常 {Batch.ExceptionCount}";

    public string StatusText => Batch.Location switch
    {
        BatchLocation.Done => "已处理",
        _ when Batch.DoneCount == 0 && Batch.ExceptionCount == 0 => "未处理",
        _ => "处理中",
    };

    // 状态色键（与 StatusKindToBrush/StatusKindToBg 对齐）：绿=已处理 · 灰=未处理 · 橙=含异常的处理中 · 蓝=处理中
    public string StatusKind => Batch.Location switch
    {
        BatchLocation.Done => "Green",
        _ when Batch.DoneCount == 0 && Batch.ExceptionCount == 0 => "Gray",
        _ when Batch.ExceptionCount > 0 => "Orange",
        _ => "Blue",
    };

    public string FetchedText => Batch.FetchedAt.ToString("MM-dd HH:mm");
    public string SubmittedText => Batch.SubmittedAt?.ToString("MM-dd HH:mm") ?? "—";
    public bool IsTodo => Batch.Location == BatchLocation.Todo;
}
