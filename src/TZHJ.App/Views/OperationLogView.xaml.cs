using System.Windows.Controls;

namespace TZHJ.App.Views;

public partial class OperationLogView : UserControl
{
    // 数据加载由 NavigationService 在导航时触发（见 INavigationService.Raise）。
    public OperationLogView() => InitializeComponent();
}
