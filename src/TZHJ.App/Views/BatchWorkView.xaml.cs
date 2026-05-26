using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using TZHJ.App.ViewModels;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.App.Views;

public partial class BatchWorkView : UserControl
{
    private bool _columnsBuilt;

    public BatchWorkView() => InitializeComponent();

    // 仅负责按字段 schema 动态建列（视图职责）；行数据加载由 NavigationService 在导航时触发。
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BatchWorkViewModel vm) return;
        if (!_columnsBuilt)
        {
            BuildColumns(vm);
            _columnsBuilt = true;
        }
    }

    /// <summary>列由字段 schema 驱动：只读字段 / 手填文本 / 手填下拉 + 行状态 + 操作（图纸不在表单内展示，到文件夹查看）。</summary>
    private void BuildColumns(BatchWorkViewModel vm)
    {
        Grid.Columns.Clear();

        foreach (var f in vm.Fields.OrderBy(x => x.Order))
            Grid.Columns.Add(BuildFieldColumn(f, vm.IsReadOnly));

        Grid.Columns.Add(ReadOnlyColumn("行状态", nameof(RowViewModel.StatusText), 90));
        Grid.Columns.Add(ReadOnlyColumn("异常原因", nameof(RowViewModel.ExceptionReason), 120));

        if (!vm.IsReadOnly)
            Grid.Columns.Add(BuildActionColumn());
    }

    private static DataGridColumn BuildFieldColumn(FieldDefinition f, bool readOnly)
    {
        // 已处理批次只读查看：所有列只读。
        // 手填下拉：单元格内常驻一个 ComboBox（单击即选，无需双击进编辑态），样式见 FluentTheme。
        if (!readOnly && f.IsEditable && f.Editor == FieldEditor.Dropdown)
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            combo.SetValue(ItemsControl.ItemsSourceProperty, f.Options);
            combo.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4));
            combo.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            combo.SetBinding(ComboBox.SelectedItemProperty, new Binding($"[{f.Key}]")
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return new DataGridTemplateColumn
            {
                Header = f.DisplayName,
                CellTemplate = new DataTemplate { VisualTree = combo },
                Width = 150,
            };
        }

        // 手填文本（如目标价）：单元格内常驻 TextBox（单击即填，无需双击进编辑态）。
        if (!readOnly && f.IsEditable)
        {
            var box = new FrameworkElementFactory(typeof(TextBox));
            box.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 6, 2, 6));
            box.SetValue(Control.PaddingProperty, new Thickness(6, 3, 6, 3)); // 收紧内边距，给数值更多可见宽度
            // 垂直撑满单元格（文字由样式 VerticalContentAlignment=Center 居中），避免按内容高度时被上下切掉
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            box.SetBinding(TextBox.TextProperty, new Binding($"[{f.Key}]")
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            return new DataGridTemplateColumn
            {
                Header = f.DisplayName,
                CellTemplate = new DataTemplate { VisualTree = box },
                Width = 160,
            };
        }

        return new DataGridTextColumn
        {
            Header = f.DisplayName,
            Binding = new Binding($"[{f.Key}]"),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
        };
    }

    private static DataGridTextColumn ReadOnlyColumn(string header, string path, double width) => new()
    {
        Header = header,
        Binding = new Binding(path),
        IsReadOnly = true,
        Width = width,
    };

    /// <summary>操作列：非异常显示「挂起异常」，异常显示「撤销异常」（可见性由行状态驱动）。</summary>
    private static DataGridTemplateColumn BuildActionColumn()
    {
        const string xaml =
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            "<StackPanel Orientation='Horizontal'>" +
            "<Button Content='挂起异常' Padding='8,3' MinHeight='26' " +
            "Visibility='{Binding SuspendVisibility}' " +
            "Command='{Binding DataContext.MarkExceptionCommand, RelativeSource={RelativeSource AncestorType={x:Type UserControl}}}' " +
            "CommandParameter='{Binding}'/>" +
            "<Button Content='撤销异常' Padding='8,3' MinHeight='26' " +
            "Visibility='{Binding RestoreVisibility}' " +
            "Command='{Binding DataContext.RestoreCommand, RelativeSource={RelativeSource AncestorType={x:Type UserControl}}}' " +
            "CommandParameter='{Binding}'/>" +
            "</StackPanel></DataTemplate>";

        return new DataGridTemplateColumn
        {
            Header = "操作",
            CellTemplate = (DataTemplate)XamlReader.Parse(xaml),
            Width = 130,
        };
    }
}
