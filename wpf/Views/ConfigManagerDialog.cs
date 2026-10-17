using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenCom.Wpf.Services;
using OpenCom.Wpf.ViewModels;

namespace OpenCom.Wpf.Views;

/// <summary>配置管理对话框：加载 / 删除 / F2 行内重命名命名配置
/// （双击列表项=加载，加载后自动关闭）。</summary>
public partial class ConfigManagerDialog : Window
{
    private sealed partial class ConfigItem : ObservableObject
    {
        public string Name { get; set; } = "";
        public string OriginalName { get; set; } = "";   // 编辑前名字（TextBox 绑定会实时覆盖 Name）
        [ObservableProperty] private bool _isEditing;
    }

    private readonly ConfigService _config;
    private readonly MainViewModel _vm;
    private readonly ListBox _list = new();
    private readonly Button _load = new() { Content = "加载", Width = 70, Height = 26, IsDefault = true };
    private readonly Button _delete = new() { Content = "删除", Width = 70, Height = 26 };
    private ConfigItem? _editing;

    public ConfigManagerDialog(ConfigService config, MainViewModel vm)
    {
        _config = config;
        _vm = vm;
        Title = "配置管理";
        Width = 420;
        Height = 380;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 12;

        // 列表项模板：文本 + 编辑框（F2 进入编辑时切换）
        const string tpl = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid>
                    <TextBlock x:Name="tb" Text="{Binding Name}" VerticalAlignment="Center" Padding="4,2"/>
                    <TextBox x:Name="ed" Text="{Binding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                             VerticalAlignment="Center" Padding="2,1" Visibility="Collapsed"/>
                </Grid>
                <DataTemplate.Triggers>
                    <DataTrigger Binding="{Binding IsEditing}" Value="True">
                        <Setter TargetName="tb" Property="Visibility" Value="Collapsed"/>
                        <Setter TargetName="ed" Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </DataTemplate.Triggers>
            </DataTemplate>
            """;
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(tpl);
        _list.Margin = new Thickness(12);
        _list.MouseDoubleClick += (_, _) => LoadSelected();
        _list.KeyDown += List_KeyDown;
        _list.PreviewKeyDown += List_PreviewKeyDown;
        _list.AddHandler(LostKeyboardFocusEvent,
                         new KeyboardFocusChangedEventHandler(List_LostKeyboardFocus), true);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
        };
        foreach (var b in new[] { _load, _delete })
        {
            b.Margin = new Thickness(6, 0, 0, 0);
            btns.Children.Add(b);
        }

        var dock = new DockPanel();
        DockPanel.SetDock(btns, Dock.Bottom);
        dock.Children.Add(btns);
        dock.Children.Add(_list);

        Content = dock;
        _load.Click += (_, _) => LoadSelected();
        _delete.Click += (_, _) => DeleteSelected();
        /* 编辑中点击编辑框之外的任何位置都提交退出：
         * 空白处/无焦点区域不会触发失焦，光靠 LostKeyboardFocus 不够 */
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnAnyMouseDown), true);
        RefreshList();
    }

    private void OnAnyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editing == null) return;
        if (_list.ItemContainerGenerator.ContainerFromItem(_editing) is ListBoxItem box)
        {
            var ed = FindTextBox(box);
            if (ed != null && e.OriginalSource is DependencyObject src && IsVisualDescendant(ed, src))
                return;   // 点在编辑框内：继续编辑
        }
        CommitEdit();
    }

    private void RefreshList()
    {
        var names = _config.ListConfigs();
        _list.ItemsSource = names.Select(n => new ConfigItem { Name = n, OriginalName = n }).ToList();
        _list.SelectedIndex = names.Length > 0 ? 0 : -1;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = _list.SelectedItem is ConfigItem;
        _load.IsEnabled = has;
        _delete.IsEnabled = has;
    }

    // ---------------- 加载 / 删除 ----------------

    private void LoadSelected()
    {
        CommitEdit();
        if (_list.SelectedItem is not ConfigItem item) return;
        _vm.LoadConfigByName(item.Name);   // 串口开着时 VM 会提示拒绝
        Close();                           // 加载后自动关闭
    }

    private void DeleteSelected()
    {
        CommitEdit();
        if (_list.SelectedItem is not ConfigItem item) return;
        var r = MessageBox.Show($"删除配置“{item.Name}”？", "配置管理",
                                MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _config.DeleteNamed(item.Name);
        RefreshList();
    }

    // ---------------- F2 行内重命名 ----------------

    private void BeginEdit(ConfigItem item)
    {
        CommitEdit();
        _editing = item;
        item.IsEditing = true;
        _list.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem box) return;
            var ed = FindTextBox(box);
            if (ed != null)
            {
                ed.Focus();
                ed.SelectAll();
            }
        });
    }

    private static TextBox? FindTextBox(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb) return tb;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }

    private void CommitEdit()
    {
        if (_editing == null) return;
        var item = _editing;
        _editing = null;
        item.IsEditing = false;
        var newName = item.Name.Trim();
        if (newName.Length == 0) return;                       // 空名：当取消
        if (newName == item.OriginalName) return;              // 未改动
        if (!_config.RenameNamed(item.OriginalName, newName))
        {
            MessageBox.Show("重命名失败（目标名称已存在？）", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshList();
            return;
        }
        var idx = _list.SelectedIndex;
        RefreshList();
        if (idx >= 0 && idx < _list.Items.Count) _list.SelectedIndex = idx;
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && _editing == null && _list.SelectedItem is ConfigItem item)
        {
            BeginEdit(item);
            e.Handled = true;
        }
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editing == null) return;
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            var item = _editing;
            _editing = null;
            if (item != null) item.IsEditing = false;
            e.Handled = true;
        }
    }

    private void List_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editing == null) return;
        // 焦点还在列表内（如从条目移到行内编辑框）不算离开，不提交——
        // 否则进入编辑框的动作会立刻把自己取消掉
        if (e.NewFocus is DependencyObject nf && IsVisualDescendant(_list, nf)) return;
        CommitEdit();
    }

    private static bool IsVisualDescendant(DependencyObject parent, DependencyObject child)
    {
        DependencyObject? p = child;
        while (p != null)
        {
            if (ReferenceEquals(p, parent)) return true;
            p = VisualTreeHelper.GetParent(p);
        }
        return false;
    }
}
