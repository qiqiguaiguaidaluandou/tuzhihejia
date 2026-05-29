using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using TZHJ.App.Services;
using TZHJ.Core.Enums;

namespace TZHJ.App.ViewModels;

/// <summary>
/// 外壳：侧边栏（映射本地文件夹的两流程功能区 + 系统）+ 内容区。两流程严格隔离，分两组展示。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _nav;

    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private NavItem? _selectedItem;

    public IReadOnlyList<NavGroup> Groups { get; }

    // 个人资料（右上角账户菜单展示，不含权限）
    public string UserName { get; }
    public string EmployeeId { get; }
    public string Department { get; }
    public string Position { get; }
    public string Avatar { get; }

    public ShellViewModel(INavigationService nav, ISession session)
    {
        _nav = nav;
        _nav.CurrentChanged += vm => CurrentPage = vm;

        var op = session.Operator;
        UserName = op.DisplayName;
        EmployeeId = op.EmployeeId;
        Department = string.IsNullOrWhiteSpace(op.Department) ? "—" : op.Department!;
        Position = string.IsNullOrWhiteSpace(op.Position) ? "—" : op.Position!;
        Avatar = string.IsNullOrEmpty(op.DisplayName) ? "?" : op.DisplayName[..1];

        var groups = new List<NavGroup>();
        if (op.CanAccess(FlowType.Pricing)) groups.Add(BuildFlowGroup("图纸核价", FlowType.Pricing));
        if (op.CanAccess(FlowType.DrawingSelection)) groups.Add(BuildFlowGroup("机加工挑图", FlowType.DrawingSelection));
        groups.Add(new NavGroup
        {
            Header = "系统",
            Items = new[]
            {
                new NavItem { Title = "采集计划", Icon = Symbol.CalendarClock, Navigate = () => _nav.ToSchedule() },
                new NavItem { Title = "设置", Icon = Symbol.Settings, Navigate = () => _nav.ToSettings() },
            },
        });
        Groups = groups;

        // 默认进第一组的"待处理"。
        if (Groups[0].Items.Count > 0)
            Select(Groups[0].Items[0]);
    }

    private NavGroup BuildFlowGroup(string header, FlowType flow) => new()
    {
        Header = header,
        Items = new[]
        {
            new NavItem { Title = "待处理", Icon = Symbol.DocumentBulletList, Navigate = () => _nav.ToBatchList(flow, BatchLocation.Todo) },
            new NavItem { Title = "已处理", Icon = Symbol.CheckmarkCircle, IconKind = "Green", Navigate = () => _nav.ToBatchList(flow, BatchLocation.Done) },
            new NavItem { Title = "异常待跟进", Icon = Symbol.Warning, IconKind = "Orange", Navigate = () => _nav.ToExceptions(flow) },
        },
    };

    [RelayCommand]
    private void Select(NavItem item)
    {
        if (SelectedItem is not null) SelectedItem.IsSelected = false;
        SelectedItem = item;
        item.IsSelected = true;
        item.Navigate();
    }
}

public sealed partial class NavItem : ObservableObject
{
    public required string Title { get; init; }
    public required Symbol Icon { get; init; }
    public required Action Navigate { get; init; }

    /// <summary>导航图标的语义淡色键（Green/Orange），null 则用中性灰、选中随强调色。见 FluentTheme 的 NavIcon 样式。</summary>
    public string? IconKind { get; init; }

    [ObservableProperty] private bool _isSelected;
}

public sealed class NavGroup
{
    public required string Header { get; init; }
    public required IReadOnlyList<NavItem> Items { get; init; }
}
