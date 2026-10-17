using System.ComponentModel;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpenCom.Wpf.Models;
using OpenCom.Wpf.Services;
using OpenCom.Wpf.ViewModels;

namespace OpenCom.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        SyncSerialCombos();
        UpdateRecvReadOnly();
        // 接收窗口禁用输入法：Shell 模式敲一个字符必须发一个字符，
        // 拼音组合缓冲会吞掉按键
        InputMethod.SetIsInputMethodEnabled(RecvBox, false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShellMode))
            UpdateRecvReadOnly();
    }

    private void UpdateRecvReadOnly()
    {
        RecvBox.IsReadOnly = !_vm.ShellMode;
        if (_vm.ShellMode) RecvBox.Focus();
    }

    /// <summary>把数据位/校验/停止位/流控下拉同步到已加载的串口配置。</summary>
    private void SyncSerialCombos()
    {
        SelectTag(cbData, _vm.Serial.DataBits.ToString());
        SelectTag(cbParity, _vm.Serial.Parity.ToString());
        SelectTag(cbStop, _vm.Serial.StopBits switch
        {
            StopBits.One => "One", StopBits.OnePointFive => "OnePointFive", _ => "Two"
        });
        SelectTag(cbFlow, _vm.Serial.FlowControl switch
        {
            Handshake.RequestToSend => "RequestToSend", Handshake.XOnXOff => "XOnXOff", _ => "None"
        });
    }

    private static void SelectTag(ComboBox cb, string tag)
    {
        foreach (var obj in cb.Items)
            if (obj is ComboBoxItem item && item.Tag as string == tag)
            {
                cb.SelectedItem = item;
                return;
            }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Shutdown();
        base.OnClosing(e);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // ---------------- 菜单 ----------------

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            "串口调试助手 v0.0.0\n" +
            "联系: sulfurandcu@gmail.com\n\n" +
            "功能：串口收发、HEX/ASCII 显示、循环发送、\n" +
            "触发发送、Shell 交互模式",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------------- 串口参数条 ----------------

    private void Ports_DropDownOpened(object sender, EventArgs e) => _vm.RefreshPortsCommand.Execute(null);

    private void DataBits_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is int v)
            _vm.Serial.DataBits = v;
    }

    private void Parity_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<Parity>(tag, out var v))
            _vm.Serial.Parity = v;
    }

    private void StopBits_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<StopBits>(tag, out var v))
            _vm.Serial.StopBits = v;
    }

    private void Flow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<Handshake>(tag, out var v))
            _vm.Serial.FlowControl = v;
    }

    // ---------------- 发送列表手势 ----------------

    /// <summary>内容格双击 = 发送该行。</summary>
    private void ContentBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox { DataContext: SendRow row })
        {
            _vm.OnRowDoubleClick(row);
            e.Handled = true;
        }
    }

    /// <summary>双击格式格 = 该行 HEX/ASCII 互切；双击触发格（触发/匹配行）= 查看
    /// 该组匹配关系（触发行看自己组的匹配行，匹配行看自己关联触发行组的匹配行）。
    /// 内容格的双击被 TextBox 处理掉了。</summary>
    private void SendGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.DataContext is not SendRow row) return;
        int col = cell.Column?.DisplayIndex ?? -1;
        if (col == 1)   // 格式列
            _vm.ToggleRowFormat(row);
        else if (col == 2)   // 触发列
        {
            if (row.Role == RowRole.Trigger)
                _vm.ShowMatchesForTrigger(_vm.Rows.IndexOf(row));
            else if (row.Role == RowRole.Match && row.TargetIndex >= 0)
                _vm.ShowMatchesForTrigger(row.TargetIndex);
        }
    }

    /// <summary>双击格式表头 = 全部行一起切换；双击触发表头 = 查看所有匹配关系；
    /// 双击次数表头 = 全部行次数设为 1。</summary>
    private void Header_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var col = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject)?.Column?.Header?.ToString();
        if (col == "格式")
            _vm.ToggleAllFormats();
        else if (col == "触发")
            _vm.ShowMatchesCommand.Execute(null);
        else if (col == "次数")
            _vm.ResetAllCounts();
    }

    /// <summary>右键：动态构建上下文菜单。先选中该行再弹。
    /// 内容格的 TextBox 会吞掉右键，列表自己的 ContextMenu 弹不出来——这里手动打开。 </summary>
    private void SendGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not SendRow r || sender is not DataGrid dg)
        {
            return;
        }
        _vm.SelectedRow = r;

        var menu = new ContextMenu();
        menu.Items.Add(CtxItem("单次发送", () => _vm.SendSelectedCommand.Execute(null)));
        menu.Items.Add(CtxItem("循环发送", () => _vm.ToggleLoopCommand.Execute(null)));
        menu.Items.Add(new Separator());

        // 触发发送：把本行设为触发行（内容即触发后发送的数据）
        var trig = CtxItem("触发发送", () => _vm.SetRowRole(r, RowRole.Trigger));
        trig.IsEnabled = r.Role == RowRole.Normal;
        menu.Items.Add(trig);

        // 触发匹配 子菜单：列出现有触发行（第N行）供显式关联，
        // 字体颜色与该触发行所在组一致
        var subMatch = new MenuItem { Header = "触发匹配" };
        bool hasTrigger = false;
        for (int i = 0; i < _vm.Rows.Count; i++)
        {
            if (_vm.Rows[i].Role != RowRole.Trigger) continue;
            hasTrigger = true;
            int target = i;
            var item = new MenuItem { Header = $"第{i + 1}行", Foreground = _vm.Rows[i].GroupColor };
            item.Click += (_, _) => _vm.SetRowRole(r, RowRole.Match, target);
            subMatch.Items.Add(item);
        }
        if (!hasTrigger)
            subMatch.Items.Add(new MenuItem { Header = "（无触发发送行）", IsEnabled = false });
        subMatch.IsEnabled = r.Role != RowRole.Trigger && hasTrigger;
        menu.Items.Add(subMatch);

        // 匹配方式 子菜单：仅匹配行可用，当前模式打勾
        var subMode = new MenuItem { Header = "匹配方式", IsEnabled = r.Role == RowRole.Match };
        foreach (var (name, mode) in new[]
                 {
                     ("完全相等", MatchMode.Equals),
                     ("前缀匹配", MatchMode.Prefix),
                     ("包含匹配", MatchMode.Contains),
                 })
        {
            var m = mode;
            var item = new MenuItem { Header = name, IsChecked = r.Role == RowRole.Match && r.MatchMode == m };
            item.Click += (_, _) => _vm.SetMatchMode(r, m);
            subMode.Items.Add(item);
        }
        menu.Items.Add(subMode);

        var unmark = CtxItem("取消标记", () => _vm.SetRowRole(r, RowRole.Normal));
        unmark.IsEnabled = r.Role != RowRole.Normal;
        menu.Items.Add(unmark);
        var unmarkAll = CtxItem("取消全部", () => _vm.UnmarkAll());
        unmarkAll.IsEnabled = _vm.Rows.Any(x => x.Role != RowRole.Normal);
        menu.Items.Add(unmarkAll);

        menu.PlacementTarget = dg;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem CtxItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // 右键菜单已改为代码动态构建（见 SendGrid_PreviewMouseRightButtonDown）

    /// <summary>内容格失焦 = 提交：HEX 解析失败回滚并提示。</summary>
    private void ContentBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: SendRow row })
            _vm.ValidateRowContent(row);
    }

    /// <summary>次数格失焦 = 校验：非负整数，非法回滚显示。</summary>
    private void CountBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SendRow row }) return;
        if (int.TryParse(((TextBox)sender).Text, out var n) && n >= 0)
            row.SendCount = n;
        else
            ((TextBox)sender).Text = row.SendCount.ToString();   // 回滚显示
    }

    /// <summary>延时输入框失焦 = 校验：非负整数，非法回滚显示。</summary>
    private void DelayBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (int.TryParse(box.Text, out var n) && n >= 0)
            _vm.Trigger.DelayMs = n;
        else
            box.Text = _vm.Trigger.DelayMs.ToString();   // 回滚显示
    }

    /// <summary>Shell 回车符单选（CRLF/CR/LF）。</summary>
    private void Eol_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var eol))
        {
            _vm.SetShellEol(eol);
            miEolCrlf.IsChecked = eol == 0;
            miEolCr.IsChecked = eol == 1;
            miEolLf.IsChecked = eol == 2;
        }
    }

    // ---------------- 接收窗口 / Shell 模式 ----------------

    private void RecvBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_vm.ShellMode) return;
        _vm.ShellKey(e.Text);
        e.Handled = true;
    }

    private void RecvBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.ShellMode) return;
        if (e.Key == Key.Enter)
        {
            _vm.ShellKeyRaw(_vm.ShellEnterBytes());
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            /* Shell 下 Tab 是终端键：发送制表符（MCU shell 常用它补全命令） */
            _vm.ShellKeyRaw(new byte[] { 0x09 });
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.ShellMode = false;
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && RecvBox.SelectionLength == 0)
        {
            _vm.ShellKeyRaw(new byte[] { 0x03 });
            e.Handled = true;
        }
    }

    private void RecvBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        /* 插入光标跟着文本到末尾：否则 Shell 模式下光标停在第一行，
         * 键入的字符会插到最前面而不是回显流的末尾。 */
        RecvBox.CaretIndex = RecvBox.Text.Length;
        RecvBox.ScrollToEnd();
    }
}
