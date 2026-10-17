using System.Windows;
using System.Windows.Controls;
using OpenCom.Wpf.ViewModels;

namespace OpenCom.Wpf.Views;

/// <summary>循环发送参数对话框（行号/次数/间隔）。</summary>
public class LoopDialog : Window
{
    private readonly TextBox _row = new() { Width = 80 };
    private readonly TextBox _count = new() { Width = 80, Text = "0" };
    private readonly TextBox _interval = new() { Width = 80, Text = "1000" };

    public LoopDialog()
    {
        Title = "循环发送";
        Width = 500;
        Height = 300;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 12;

        var grid = new Grid { Margin = new Thickness(16) };
        // 输入行固定高度（文本框保持自然高度垂直居中），按钮行占剩余空间
        for (int i = 0; i < 3; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        grid.Children.Add(MkLabel("行号（1 起）", 0));
        grid.Children.Add(MkLabel("次数（0=无限）", 1));
        grid.Children.Add(MkLabel("间隔（毫秒）", 2));
        Grid.SetColumn(_row, 1); Grid.SetRow(_row, 0);
        Grid.SetColumn(_count, 1); Grid.SetRow(_count, 1);
        Grid.SetColumn(_interval, 1); Grid.SetRow(_interval, 2);
        _row.VerticalAlignment = VerticalAlignment.Center;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _interval.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_row);
        grid.Children.Add(_count);
        grid.Children.Add(_interval);

        var ok = new Button { Content = "开始", Width = 70, Height = 26, IsDefault = true };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        btns.Children.Add(ok);
        Grid.SetRow(btns, 3); Grid.SetColumnSpan(btns, 2);
        grid.Children.Add(btns);

        Content = grid;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                _row.Text = vm.LoopRowNumber.ToString();
                _count.Text = vm.LoopCount.ToString();
                _interval.Text = vm.LoopInterval.ToString();
            }
        };
        ok.Click += (_, _) => Commit();
    }

    private static Label MkLabel(string text, int row)
    {
        var l = new Label { Content = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(l, row);
        return l;
    }

    private void Commit()
    {
        if (DataContext is not MainViewModel vm) return;
        if (!int.TryParse(_row.Text, out var r) || r < 1) { MessageBox.Show("行号无效", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (!int.TryParse(_count.Text, out var c) || c < 0) { MessageBox.Show("次数无效", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (!int.TryParse(_interval.Text, out var t) || t < 1) { MessageBox.Show("间隔无效", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        vm.LoopRowNumber = r;
        vm.LoopCount = c;
        vm.LoopInterval = t;
        DialogResult = true;
    }
}
