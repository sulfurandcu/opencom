using System.Windows;
using System.Windows.Controls;
using OpenCom.Wpf.ViewModels;

namespace OpenCom.Wpf.Views;

/// <summary>配置命名对话框：保存配置命名 / 重命名配置复用。
/// 名称输入用可编辑下拉框：能直接看到并选取已有的配置（覆盖用）。</summary>
public class SaveConfigDialog : Window
{
    private readonly ComboBox _name = new() { Width = 220, IsEditable = true };

    /// <summary>确认后输入的名称。</summary>
    public string EnteredName => _name.Text.Trim();

    public SaveConfigDialog(string title = "保存配置", string okText = "保存")
    {
        Title = title;
        Width = 340;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 12;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var label = new Label { Content = "配置名称", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_name, 1);
        _name.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        grid.Children.Add(_name);

        var ok = new Button { Content = okText, Width = 70, Height = 26, IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 70, Height = 26, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        Grid.SetRow(btns, 1); Grid.SetColumnSpan(btns, 2);
        grid.Children.Add(btns);

        Content = grid;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                _name.ItemsSource = vm.ListSavedConfigs();   // 下拉可见已有配置
                _name.Text = vm.ConfigName;
            }
        };
        Loaded += (_, _) =>
        {
            _name.Focus();
            if (_name.Template.FindName("PART_EditableTextBox", _name) is TextBox tb) tb.SelectAll();
        };
        ok.Click += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            var name = _name.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("配置名称不能为空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            vm.ConfigName = name;
            DialogResult = true;
        };
    }
}
