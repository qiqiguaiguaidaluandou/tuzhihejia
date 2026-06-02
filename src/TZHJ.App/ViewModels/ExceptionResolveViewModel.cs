using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TZHJ.App.Services;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 异常补处理：点一条异常 → 回到来源批次（已处理）取该行全字段 → 重填待填列 → 单行补回传。
/// 成功后把来源批次里该行状态改为「已上传」、并从异常待跟进池移除。
/// </summary>
public sealed partial class ExceptionResolveViewModel : ViewModelBase
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
    private readonly ExceptionItem _exception;
    private Batch? _sourceBatch;

    public ExceptionResolveViewModel(
        ILocalBatchStore store, ISubmitGateway submit, IFieldProvider fieldProvider,
        ISession session, INavigationService nav, IDialogService dialog, IExplorerService explorer,
        IOperationLogGateway opLog,
        FlowType flow, ExceptionItem exception)
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
        _exception = exception;

        Fields = _fieldProvider.FieldsFor(flow);
        TargetSystem = flow == FlowType.Pricing ? "SRM" : "EBS";
    }

    public IReadOnlyList<FieldDefinition> Fields { get; }
    public string TargetSystem { get; }

    public string MaterialCode => _exception.MaterialCode;
    public string SourceBatchText => _exception.SourceBatch;
    public string ReasonText => _exception.Reason;
    public string SubmitButtonText => $"重新回传到{TargetSystem}";

    /// <summary>该行的字段编辑投影（待填列可编辑，其余只读）。来源批次/行不存在时为 null。</summary>
    [ObservableProperty] private RowViewModel? _row;

    /// <summary>来源批次/行存在且账号有回传权限——可补处理。否则只读提示。</summary>
    [ObservableProperty] private bool _canResolve;

    [ObservableProperty] private string _folderPathText = string.Empty;

    public bool CanUpload => CanResolve && Row is { Status: RowStatus.Done };

    public string GateText => !CanResolve
        ? _blockReason
        : Row is { Status: RowStatus.Done }
            ? $"必填项已填，可重新回传 {TargetSystem}。"
            : "请先填写必填列（核价=目标价 / 挑图=是否机加中心可以做），「上传」才可点。";

    private string _blockReason = string.Empty;

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Title = $"异常补处理 · {_exception.MaterialCode}";

            // 来源批次仍在「已处理」里，挂起异常的行带 Status=Exception 留在其中。
            _sourceBatch = await _store.GetBatchAsync(
                _flow, _session.Operator.EmployeeId, BatchLocation.Done, _exception.SourceBatch);
            var model = _sourceBatch?.Rows.FirstOrDefault(r => r.RowKey == _exception.RowKey);

            if (_sourceBatch is null || model is null)
            {
                CanResolve = false;
                _blockReason = "来源批次已不存在或该行已被移除，无法补回传。请到来源批次或异常池核对。";
                Row = null;
                Refresh();
                return;
            }

            FolderPathText = _sourceBatch.FolderPath;

            var requiredKeys = Fields.Where(f => f.IsEditable && f.IsRequired).Select(f => f.Key).ToHashSet();
            var editableKeys = Fields.Where(f => f.IsEditable).Select(f => f.Key).ToHashSet();
            Row = new RowViewModel(model, requiredKeys, editableKeys, Refresh, readOnly: false);

            // 解除挂起：清异常态回到待处理（若待填列已填则自动回到已处理），以便编辑后正常判定可上传。
            Row.Restore();

            if (!_session.Operator.CanSubmit)
            {
                CanResolve = false;
                _blockReason = "当前账号无回传权限，无法补回传。";
            }
            else
            {
                CanResolve = true;
            }

            Refresh();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Back() => _nav.ToExceptions(_flow);

    [RelayCommand]
    private void OpenFolder()
    {
        if (_sourceBatch is not null) _explorer.OpenFolder(_sourceBatch.FolderPath);
    }

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task Upload()
    {
        if (_sourceBatch is null || Row is null) return;
        if (Row.Status != RowStatus.Done)
        {
            _dialog.Error("请先填写必填列后再上传。");
            return;
        }

        if (!_dialog.Confirm("确认上传该行",
                $"将该行（{_exception.MaterialCode}）补回传到 {TargetSystem}，成功后：\n" +
                $"· 来源批次「{_exception.SourceBatch}」中该行状态改为「已上传」\n" +
                $"· 从「异常待跟进」移除\n\n确认？"))
            return;

        IsBusy = true;
        try
        {
            // 单行复用整批回传形状：BatchKey/窗口取自来源批次，Rows 只放这一行。
            var request = new SubmitRequest
            {
                EmployeeId = _session.Operator.EmployeeId,
                Flow = _flow,
                BatchKey = _sourceBatch.Key,
                WindowStart = _sourceBatch.WindowStart,
                WindowEnd = _sourceBatch.WindowEnd,
                Rows = new List<SubmitRow>
                {
                    new() { RowKey = Row.RowKey, Values = new Dictionary<string, string?>(Row.Model.Values) },
                },
            };

            var result = await _submit.SubmitBatchAsync(request);
            if (!result.Success)
            {
                _dialog.Error(result.Message ?? "回传失败，请重试。");
                return;
            }

            // 成功：记一条操作日志（fire-and-forget，记录失败不影响回传）。
            OperationLog.Record(_opLog, SubmitButtonText, _exception.SourceBatch, _flow, _session.Operator.EmployeeId);

            // 该行→已上传，写回来源批次（xlsx+manifest）；再从异常池移除。
            Row.MarkUploaded();
            await _store.SaveBatchAsync(_sourceBatch);
            await _store.RemoveExceptionAsync(
                _flow, _session.Operator.EmployeeId, _exception.SourceBatch, _exception.RowKey);

            _dialog.Success($"已回传 {TargetSystem}：该行标记「已上传」，并移出异常待跟进。");
            _nav.ToExceptions(_flow);
        }
        catch (Exception ex)
        {
            _dialog.Error(FriendlyError.Describe(ex, "回传"));
        }
        finally { IsBusy = false; }
    }

    /// <summary>行值/状态变化或加载完成后，刷新可上传态与闸门提示。</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(GateText));
        UploadCommand.NotifyCanExecuteChanged();
    }
}
