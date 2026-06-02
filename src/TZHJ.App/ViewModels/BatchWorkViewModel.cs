using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TZHJ.App.Services;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 批次作业页：渲染可编辑网格（列由字段 schema 驱动）、提交闸门、整批上传（回传成功后移入已处理）。
/// 已处理批次只读查看。挂起异常行整批提交时不回传，转入异常池。
/// </summary>
public sealed partial class BatchWorkViewModel : ViewModelBase
{
    private readonly ILocalBatchStore _store;
    private readonly ISubmitGateway _submit;
    private readonly IFieldProvider _fieldProvider;
    private readonly ISession _session;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialog;
    private readonly IExplorerService _explorer;
    private readonly IOperationLogGateway _opLog;

    private readonly FlowType _flow;
    private readonly BatchLocation _location;
    private readonly string _folderName;
    private Batch? _batch;

    public BatchWorkViewModel(
        ILocalBatchStore store, ISubmitGateway submit, IFieldProvider fieldProvider,
        ISession session, INavigationService nav, IDialogService dialog, IExplorerService explorer,
        IOperationLogGateway opLog,
        FlowType flow, BatchLocation location, string folderName)
    {
        _store = store;
        _submit = submit;
        _fieldProvider = fieldProvider;
        _session = session;
        _nav = nav;
        _dialog = dialog;
        _explorer = explorer;
        _opLog = opLog;
        _flow = flow;
        _location = location;
        _folderName = folderName;

        Fields = _fieldProvider.FieldsFor(flow);
        IsReadOnly = location == BatchLocation.Done;
        TargetSystem = flow == FlowType.Pricing ? "SRM" : "EBS";
    }

    public IReadOnlyList<FieldDefinition> Fields { get; }
    public ObservableCollection<RowViewModel> Rows { get; } = new();

    public bool IsReadOnly { get; }
    public string TargetSystem { get; }

    /// <summary>整批回传按钮文案：核价→回传到SRM，挑图→回传到EBS。</summary>
    public string SubmitButtonText => $"回传到{TargetSystem}";

    [ObservableProperty] private string _folderPathText = string.Empty;

    /// <summary>自动暂存提示（如"已自动暂存 · 14:03:21"），信息条上轻量展示，不弹 Toast。</summary>
    [ObservableProperty] private string _autoSaveHint = string.Empty;

    public int PendingCount => Rows.Count(r => r.Status == RowStatus.Pending);
    public int DoneCount => Rows.Count(r => r.Status is RowStatus.Done or RowStatus.Uploaded);
    public int ExceptionCount => Rows.Count(r => r.Status == RowStatus.Exception);
    public string ProgressText => $"已处理 {DoneCount + ExceptionCount} / {Rows.Count}";

    public bool CanSubmit => !IsReadOnly && Rows.Count > 0 && PendingCount == 0 && _session.Operator.CanSubmit;

    public string GateText => CanSubmit
        ? $"✅ 全部行已处理完毕（已填写或挂起异常），可整批回传 {TargetSystem}。"
        : $"还有 {PendingCount} 行处于「待处理」，每行需先填写或挂起异常，「上传」才可点（提交闸门）。";

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _batch = await _store.GetBatchAsync(_flow, _session.Operator.EmployeeId, _location, _folderName);
            if (_batch is null)
            {
                _dialog.Error("批次不存在或已被删除。");
                _nav.ToBatchList(_flow, _location);
                return;
            }

            Title = $"批次作业 · {_batch.WindowStart:MM-dd HH:mm} ~ {_batch.WindowEnd:MM-dd HH:mm}";
            FolderPathText = _batch.FolderPath;

            var requiredKeys = Fields.Where(f => f.IsEditable && f.IsRequired).Select(f => f.Key).ToHashSet();
            var editableKeys = Fields.Where(f => f.IsEditable).Select(f => f.Key).ToHashSet();

            Rows.Clear();
            foreach (var row in _batch.Rows)
                Rows.Add(new RowViewModel(row, requiredKeys, editableKeys, OnRowChanged, IsReadOnly));

            Recompute();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_batch is not null) _explorer.OpenFolder(_batch.FolderPath);
    }

    [RelayCommand]
    private async Task Back()
    {
        await FlushAutoSaveAsync(); // 离开前把未落盘的改动写回
        _nav.ToBatchList(_flow, _location);
    }

    [RelayCommand]
    private void MarkException(RowViewModel row)
    {
        // 不预填默认原因，由操作员自己写（沿用"原因为空则不挂起"）。
        var reason = _dialog.Prompt("挂起异常", "请填写异常原因：",
            row.ExceptionReason is { Length: > 0 } ? row.ExceptionReason : null);
        if (string.IsNullOrWhiteSpace(reason)) return;
        row.Suspend(reason);
        Recompute();
        ScheduleAutoSave();
    }

    [RelayCommand]
    private void Restore(RowViewModel row)
    {
        row.Restore();
        Recompute();
        ScheduleAutoSave();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_batch is null || IsReadOnly) return;
        await SaveCoreAsync();
        _dialog.Success("已写回本批次表格（暂存）。");
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        if (_batch is null) return;

        // 提交闸门二次校验（防御 UI 状态与模型短暂不一致）：仍有「待处理」行则中止。
        if (Rows.Count == 0 || Rows.Any(r => r.Status == RowStatus.Pending))
        {
            _dialog.Error("仍有「待处理」行，请先填写或挂起异常后再整批提交。");
            Recompute();
            return;
        }

        var normal = Rows.Where(r => r.Status != RowStatus.Exception).ToList();
        var exceptions = Rows.Where(r => r.Status == RowStatus.Exception).ToList();

        if (!_dialog.Confirm("确认整批上传",
                $"正常行（回传 {TargetSystem}）：{normal.Count} 行\n" +
                $"挂起异常行（不回传，转入异常池）：{exceptions.Count} 行\n\n" +
                $"回传由后端执行并记审计日志，成功后批次将移入「已处理」。确认？"))
            return;

        IsBusy = true;
        try
        {
            var request = new SubmitRequest
            {
                EmployeeId = _session.Operator.EmployeeId,
                Flow = _flow,
                BatchKey = _batch.Key,
                WindowStart = _batch.WindowStart,
                WindowEnd = _batch.WindowEnd,
                Rows = normal.Select(r => new SubmitRow
                {
                    RowKey = r.RowKey,
                    Values = new Dictionary<string, string?>(r.Model.Values),
                }).ToList(),
            };

            var result = await _submit.SubmitBatchAsync(request);
            if (!result.Success)
            {
                _dialog.Error(result.Message ?? "回传失败，请重试。");
                return;
            }

            // 成功：记一条操作日志（fire-and-forget，记录失败不影响回传）。
            OperationLog.Record(_opLog, SubmitButtonText, _batch.FolderName, _flow, _session.Operator.EmployeeId);

            // 正常行→已上传；异常行入池；持久化后整批移入已处理。
            foreach (var r in normal) r.MarkUploaded();

            if (exceptions.Count > 0)
            {
                var items = exceptions.Select(r => new ExceptionItem
                {
                    Flow = _flow,
                    RowKey = r.RowKey,
                    MaterialCode = r.Model.Get("materialCode") ?? r.RowKey,
                    DisplayName = r.Model.Get("name") ?? r.Model.Get("materialDesc"),
                    SourceBatch = _batch.FolderName,
                    Reason = r.Model.ExceptionReason ?? "未知",
                    SuspendedAt = DateTime.Now,
                });
                await _store.AddExceptionsAsync(_flow, _session.Operator.EmployeeId, items);
            }

            await _store.SaveBatchAsync(_batch);
            await _store.MoveToDoneAsync(_batch);

            _dialog.Success($"上传成功：{normal.Count} 行已回传 {TargetSystem}，批次已移入「已处理」。");
            _nav.ToBatchList(_flow, BatchLocation.Todo);
        }
        catch (Exception ex)
        {
            // 断网/超时/服务器报错翻成操作员可读提示；批次未移入「已处理」，可原样重试。
            _dialog.Error(FriendlyError.Describe(ex, "回传"));
        }
        finally { IsBusy = false; }
    }

    private void Recompute()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(ExceptionCount));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(GateText));
        SubmitCommand.NotifyCanExecuteChanged();
    }

    // ---------- 自动暂存（填写/挂起后无需手点「暂存」）----------

    private CancellationTokenSource? _autoSaveDebounce;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _dirty;

    /// <summary>行值改动（编辑待填列）：重算闸门 + 防抖自动暂存。</summary>
    private void OnRowChanged()
    {
        Recompute();
        ScheduleAutoSave();
    }

    /// <summary>标脏并防抖：连续输入合并到停手 ~800ms 后才落盘一次，避免每个键都写 xlsx。</summary>
    private void ScheduleAutoSave()
    {
        if (_batch is null || IsReadOnly) return;
        _dirty = true;
        _autoSaveDebounce?.Cancel();
        _autoSaveDebounce = new CancellationTokenSource();
        var token = _autoSaveDebounce.Token;
        _ = Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try { await Task.Delay(800, token); }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            await AutoSaveAsync();
        });
    }

    private async Task AutoSaveAsync()
    {
        if (!_dirty) return;
        try
        {
            await SaveCoreAsync();
            AutoSaveHint = $"已自动暂存 · {DateTime.Now:HH:mm:ss}";
        }
        catch
        {
            // 自动暂存失败不打断填写，只提示；操作员仍可点「暂存」或离开时再尝试。
            AutoSaveHint = "自动暂存失败，请手动点「暂存」";
        }
    }

    /// <summary>离开/关闭前把未落盘改动写回。</summary>
    private async Task FlushAutoSaveAsync()
    {
        _autoSaveDebounce?.Cancel();
        if (_dirty) await AutoSaveAsync();
    }

    /// <summary>串行化落盘：自动暂存、手动暂存、离开时刷盘共用，避免并发写同一 xlsx。</summary>
    private async Task SaveCoreAsync()
    {
        if (_batch is null || IsReadOnly) return;
        await _saveLock.WaitAsync();
        try
        {
            await _store.SaveBatchAsync(_batch);
            _dirty = false;
        }
        finally { _saveLock.Release(); }
    }

    public override void Dispose()
    {
        _autoSaveDebounce?.Cancel();
        // 导航/关闭时尽力写回未落盘改动（本地写通常即时完成；经 _saveLock 串行不与进行中的保存打架）。
        if (_dirty && _batch is not null && !IsReadOnly) _ = SaveCoreAsync();
    }
}
