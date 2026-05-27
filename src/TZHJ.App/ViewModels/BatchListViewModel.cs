using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TZHJ.App.Services;
using TZHJ.Core.Contracts;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 批次列表（待处理 / 已处理）。直接映射本地文件夹：扫描目录 → 列表。
/// 待处理可"手动补拉"（演示取数→落本地）与"开始作业"。
/// </summary>
public sealed partial class BatchListViewModel : ViewModelBase
{
    private readonly ILocalBatchStore _store;
    private readonly IDataGateway _data;
    private readonly ISession _session;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialog;
    private readonly IExplorerService _explorer;

    private readonly FlowType _flow;
    private readonly BatchLocation _location;

    public BatchListViewModel(
        ILocalBatchStore store, IDataGateway data, ISession session,
        INavigationService nav, IDialogService dialog, IExplorerService explorer,
        FlowType flow, BatchLocation location)
    {
        _store = store;
        _data = data;
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

    /// <summary>手动补拉：对配置的时间窗逐个检查本地是否已有，缺的就取数落本地（演示取数链路）。</summary>
    [RelayCommand]
    private async Task ManualFetch()
    {
        if (!IsTodo) return;
        IsBusy = true;
        var fetched = 0;
        try
        {
            var emp = _session.Operator.EmployeeId;
            foreach (var (start, end) in RecentWindows())
            {
                if (_store.BatchExists(_flow, emp, start, end)) continue;
                var result = await _data.FetchBatchAsync(new FetchRequest
                {
                    EmployeeId = emp, Flow = _flow, WindowStart = start, WindowEnd = end,
                });
                if (result.Success)
                {
                    await _store.WriteFetchedBatchAsync(result);
                    fetched++;
                }
            }
            await LoadAsync();
            if (fetched > 0) _dialog.Success($"补拉完成，新增 {fetched} 个批次。");
            else _dialog.Info("本地已是最新，无需补拉。");
        }
        catch (Exception ex)
        {
            _dialog.Error($"补拉失败：{ex.Message}");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// 可补拉的最近时间窗：只取**已关闭（已到触发时刻）**的窗——窗口关闭后才触发取数（见 CollectionWindow.TriggerTime，含登录补拉）。
    /// 尚未关闭（如当前正处其中）或整段在未来的窗一律不取，避免拉到不完整/还没发生的数据。
    /// </summary>
    private IEnumerable<(DateTime Start, DateTime End)> RecentWindows()
    {
        var defs = _session.Config.WindowsFor(_flow);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTime.Now;
        var list = new List<(DateTime, DateTime)>();
        foreach (var anchor in new[] { today, today.AddDays(-1) })
            foreach (var w in defs.Where(w => w.Enabled))
            {
                if (anchor.ToDateTime(w.TriggerTime) > now) continue; // 未到触发时刻：窗口未关闭，不补拉
                list.Add(w.Resolve(anchor));
            }
        return list.OrderByDescending(x => x.Item2);
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

    public string FetchedText => Batch.FetchedAt.ToString("MM-dd HH:mm");
    public string SubmittedText => Batch.SubmittedAt?.ToString("MM-dd HH:mm") ?? "—";
    public bool IsTodo => Batch.Location == BatchLocation.Todo;
}
