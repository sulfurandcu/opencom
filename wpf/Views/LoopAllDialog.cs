using System.Windows;
using System.Windows.Controls;
using OpenCom.Wpf.ViewModels;

namespace OpenCom.Wpf.Views;

/// <summary>循环发送全部行参数对话框：轮数（0=无限）/行间间隔/轮间间隔。</summary>
public class LoopAllDialog : Window
{
    private readonly TextBox _cycles = new() { Width = 80, Text = "0" };
    private readonly TextBox _rowInterval = new() { Width = 80, Text = "100" };
    private readonly TextBox _cycleInterval = new() { Width = 80, Text = "1000" };

    public LoopAllDialog()
    {
        Title = "循环发送全部";
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        grid.Children.Add(MkLabel("轮数（0=无限）", 0));
        grid.Children.Add(MkLabel("行间间隔（毫秒）", 1));
        grid.Children.Add(MkLabel("轮间间隔（毫秒）", 2));
        Grid.SetColumn(_cycles, 1); Grid.SetRow(_cycles, 0);
        Grid.SetColumn(_rowInterval, 1); Grid.SetRow(_rowInterval, 1);
        Grid.SetColumn(_cycleInterval, 1); Grid.SetRow(_cycleInterval, 2);
        _cycles.VerticalAlignment = VerticalAlignment.Center;
        _rowInterval.VerticalAlignment = VerticalAlignment.Center;
        _cycleInterval.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_cycles);
        grid.Children.Add(_rowInterval);
        grid.Children.Add(_cycleInterval);

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
                _cycles.Text = vm.LoopAllCycles.ToString();
                _rowInterval.Text = vm.LoopAllRowInterval.ToString();
                _cycleInterval.Text = vm.LoopAllCycleInterval.ToString();
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
        if (!int.TryParse(_cycles.Text, out var c) || c < 0) { MessageBox.Show("轮数无效", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (!int.TryParse(_rowInterval.Text, out var a) || a < 1) { MessageBox.Show("行间间隔无效", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (!int.TryParse(_cycleInterval.Text, out var b) || b < 1) { MessageBox.Show("轮间间隔无效", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        vm.LoopAllCycles = c;
        vm.LoopAllRowInterval = a;
        vm.LoopAllCycleInterval = b;
        DialogResult = true;
    }
}
