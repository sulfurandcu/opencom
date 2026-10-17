using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenCom.Wpf.Models;
using OpenCom.Wpf.Services;
using OpenCom.Wpf.Views;

namespace OpenCom.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxRows = 200;
    private const int RecvTextCap = 512 * 1024;   // 接收文本上限

    private static readonly Color[] GroupColors =
    {
        Color.FromRgb(0xC0, 0x39, 0x2B), Color.FromRgb(0x1E, 0x84, 0x4B), Color.FromRgb(0x1F, 0x61, 0x80),
        Color.FromRgb(0x8E, 0x44, 0xAD), Color.FromRgb(0xD3, 0x54, 0x00), Color.FromRgb(0x0E, 0x66, 0x5C),
        Color.FromRgb(0xC0, 0x1E, 0x6F), Color.FromRgb(0x2E, 0x86, 0xC1), Color.FromRgb(0x6D, 0x4C, 0x41),
        Color.FromRgb(0x27, 0xAE, 0x60), Color.FromRgb(0x7D, 0x3C, 0x98), Color.FromRgb(0x17, 0x60, 0xA0),
    };

    private readonly SerialService _serial = new();
    private readonly TriggerEngine _trigger = new();
    private readonly ConfigService _config = new();
    private readonly Dispatcher _ui = Application.Current.Dispatcher;

    private CancellationTokenSource? _loopCts;
    private readonly StringBuilder _recv = new();
    private bool _prevCr;                       // CR/LF 归一化跨块状态
    private readonly List<byte> _utf8Tail = new();   // 不完整 UTF-8 序列跨块续接
    private readonly DispatcherTimer _silenceTimer;   // RX 静默断行
    private DateTime _lastAppendTick;
    private const int LineBufCap = 8 * 1024;          // 行缓冲上限（超上限丢最旧，防设备不换行时无限增长）
    private readonly List<byte> _lineBuf = new();     // 触发匹配的行缓冲（读线程写，定时器线程收尾）
    private readonly object _lineLock = new();
    private long _lastRxTick;                         // 最后收到 RX 字节的时刻（TickCount64）
    private readonly List<(byte[] Data, bool IsTx)> _pendingRx = new();   // 暂停期间缓存的显示数据（RX 与 TX 都暂存）
    private long _rxTotal, _txTotal;
    private int _loopSent;

    public SerialSettings Serial { get; } = new();
    public DisplayOptions Display { get; } = new();

    public ObservableCollection<SendRow> Rows { get; } = new();
    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<int> BaudRates { get; } = new()
        { 300, 600, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    [ObservableProperty] private SendRow? _selectedRow;
    [ObservableProperty] private bool _portOpen;
    [ObservableProperty] private bool _loopRunning;
    [ObservableProperty] private bool _shellMode;
    [ObservableProperty] private bool _recvPaused;
    [ObservableProperty] private string _receiveText = "";
    [ObservableProperty] private string _statusText = "未打开";
    [ObservableProperty] private string _portButtonText = "打开串口";
    [ObservableProperty] private string _loopButtonText = "循环发送";
    [ObservableProperty] private string _loopAllButtonText = "循环全部";
    [ObservableProperty] private string _fmtButtonText = "ASCII";
    [ObservableProperty] private string _pauseButtonText = "暂停";
    [ObservableProperty] private string _countersText = "RX: 0  TX: 0";
    [ObservableProperty] private int _loopRowNumber = 1;      // 循环对话框：行号（1 起）
    [ObservableProperty] private int _loopCount;              // 循环对话框：次数（0=无限）
    [ObservableProperty] private int _loopInterval = 1000;    // 循环对话框：间隔 ms
    [ObservableProperty] private int _loopAllCycles;          // 循环全部：轮数（0=无限）
    [ObservableProperty] private int _loopAllRowInterval = 100;   // 循环全部：行间间隔 ms
    [ObservableProperty] private int _loopAllCycleInterval = 1000;// 循环全部：轮间间隔 ms

    public MainViewModel()
    {
        var (serial, display, rows) = _config.Load();
        Serial = serial;
        Display = display;
        foreach (var r in rows.Take(MaxRows)) Rows.Add(r);

        _serial.DataReceived += OnDataReceived;
        _serial.PortGone += OnPortGone;
        _serial.WriteFailed += msg =>
            _ui.BeginInvoke(() => StatusText = msg);

        // 串口打开时勾选 DTR/RTS 立即生效
        Serial.PropertyChanged += (_, e) =>
        {
            if (!_serial.IsOpen) return;
            if (e.PropertyName == nameof(SerialSettings.Dtr)) _serial.SetDtr(Serial.Dtr);
            if (e.PropertyName == nameof(SerialSettings.Rts)) _serial.SetRts(Serial.Rts);
        };

        RefreshPorts();
        RebuildTriggers();
        LogService.Log($"opencom 启动：加载 {Rows.Count} 行，端口 {Serial.Port}");

        // RX 静默断行：超过 3 个字节时间没新数据就补换行（用户定：2026-10-17 由 5 字节收紧）
        _silenceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        _silenceTimer.Tick += (_, _) =>
        {
            if (!_serial.IsOpen) return;
            /* 空闲判定（用户定义）：超过 **3 个字节时间**没新数据即断行，
             * 3 × 10 位 ÷ 波特率；下限 10ms（轮询间隔 10ms，再低也反应不了）。
             * 例：115200 → 10ms（下限）；9600 → 10ms（下限）；300 → 100ms。 */
            int baud = Serial.Baud > 0 ? Serial.Baud : 115200;
            int silenceMs = Math.Max(10, 3 * 10000 / baud);
            /* 静默断行 = 一行收完：把缓冲行喂给触发引擎（行级匹配） */
            if (Environment.TickCount64 - Interlocked.Read(ref _lastRxTick) >= silenceMs)
                FlushLineBuf();

            if (ShellMode || _recv.Length == 0) return;
            if (_recv[^1] == '\n') return;
            if ((DateTime.Now - _lastAppendTick).TotalMilliseconds < silenceMs) return;
            _recv.Append('\n');
            LogService.Recv("\n");
            ReceiveText = _recv.ToString();
            OnPropertyChanged(nameof(ReceiveText));
        };
        _silenceTimer.Start();
    }

    // ---------------- 串口 ----------------

    [RelayCommand]
    private void TogglePort()
    {
        if (_serial.IsOpen)
        {
            _serial.Close();
            PortOpen = false;
            PortButtonText = "打开串口";
            UpdateStatusText();
            LogService.Log($"关闭串口 {Serial.Port}");
        }
        else
        {
            try
            {
                if (string.IsNullOrEmpty(Serial.Port))
                {
                    MessageBox.Show("请选择端口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _serial.Open(Serial);
                PortOpen = true;
                PortButtonText = "关闭串口";
                UpdateStatusText();
                LogService.Log($"打开串口 {Serial.Port} {Serial.Baud} {Serial.DataBits}" +
                               $"{(int)Serial.Parity}{(int)Serial.StopBits}{(int)Serial.FlowControl} DTR={(Serial.Dtr ? 1 : 0)} RTS={(Serial.Rts ? 1 : 0)}");
            }
            catch (Exception ex)
            {
                LogService.Log($"打开串口失败 {Serial.Port}: {ex.Message}");
                MessageBox.Show($"打开串口失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>状态栏统一重建（端口状态 + 暂停标记）。</summary>
    private void UpdateStatusText()
    {
        var s = _serial.IsOpen ? $"{Serial.Port} {Serial.Baud} 已打开" : "未打开";
        if (RecvPaused) s += "（监视已暂停）";
        StatusText = s;
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        /* 端口选择策略：上次用的端口还在 → 保持；不在了 → 用第一个存在的端口
         * （默认 COM1 不存在时不再傻等它）。先记住当前端口再重建列表：清空
         * ItemsSource 会把组合框选中清掉、双向绑定随之把 Serial.Port 写成
         * null（"打开串口失败 PortName 为 null"的根因）。 */
        var cur = Serial.Port;
        Ports.Clear();
        var found = SerialService.EnumeratePorts();
        foreach (var p in found) Ports.Add(p);

        if (!string.IsNullOrEmpty(cur) && Ports.Contains(cur))
            Serial.Port = cur;          // 上次用的还在
        else if (Ports.Count > 0)
            Serial.Port = Ports[0];     // 不在了 → 第一个存在的端口
        else
            Serial.Port = "";           // 一个串口都没有
    }

    [RelayCommand]
    private void TogglePause()
    {
        RecvPaused = !RecvPaused;
        PauseButtonText = RecvPaused ? "恢复" : "暂停";
        UpdateStatusText();   // 暂停/恢复都重建状态栏（恢复后别残留"已暂停"）
        if (!RecvPaused) FlushPendingRx();   // 恢复：把暂停期间的数据补上
    }

    private void FlushPendingRx()
    {
        foreach (var (data, isTx) in _pendingRx) AppendReceive(data, isTx);
        _pendingRx.Clear();
    }

    private void OnDataReceived(byte[] data)
    {
        _rxTotal += data.Length;
        _ui.BeginInvoke(UpdateCounters);

        /* 先把本块 RX 排进显示队列，再跑触发匹配（TX 显示排在其后）：
         * 窗口里先看到完整的匹配帧，再看到 [TX] 响应。 */
        _ui.BeginInvoke(() => AppendReceive(data, isTx: false));

        // 行级匹配：一行收完（CR/LF 或静默断行）才喂触发引擎（暂停只暂停显示）
        FeedLine(data);
    }

    /// <summary>触发命中：发送目标行内容并排入 TX 显示。</summary>
    private void OnTriggerHit(SendRow target)
    {
        if (!_serial.Write(target.TxBytes)) return;   // 发送失败已记日志
        _txTotal += target.TxBytes.Length;
        LogService.Log($"触发命中：发送 {target.TxBytes.Length} 字节");
        _ui.BeginInvoke(UpdateCounters);
        _ui.BeginInvoke(() => AppendReceive(target.TxBytes, isTx: true));
    }

    /// <summary>RX 数据进行缓冲，CR/LF 处把完整的一行喂给触发引擎。
    /// 匹配只在行内进行：一行收完才喂、喂完即重置状态，
    /// 跨行的数据拼不成匹配（行由 CR/LF 或静默断行界定）。
    /// 注意：每行**一次**喂完（不分块），EQUALS 整行相等依赖这一点。</summary>
    private void FeedLine(byte[] data)
    {
        Interlocked.Exchange(ref _lastRxTick, Environment.TickCount64);
        List<byte[]>? done = null;
        lock (_lineLock)
        {
            int start = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != (byte)'\r' && data[i] != (byte)'\n') continue;
                // 行结束符：缓冲 + 本段组成完整一行
                done ??= new List<byte[]>();
                var line = new byte[_lineBuf.Count + (i - start)];
                _lineBuf.CopyTo(line);
                Array.Copy(data, start, line, _lineBuf.Count, i - start);
                if (line.Length > 0) done.Add(line);
                _lineBuf.Clear();
                start = i + 1;
            }
            if (start < data.Length)
            {
                for (int i = start; i < data.Length; i++) _lineBuf.Add(data[i]);
                if (_lineBuf.Count > LineBufCap) _lineBuf.RemoveRange(0, _lineBuf.Count - LineBufCap);
            }
        }
        if (done == null) return;
        foreach (var line in done)
        {
            _trigger.Feed(Rows, line, OnTriggerHit);
            _trigger.ResetStates();   // 行边界：清掉跨行 carry
        }
    }

    /// <summary>静默断行 = 一行收完：把缓冲行喂给触发引擎（由定时器调用）。</summary>
    private void FlushLineBuf()
    {
        byte[]? line = null;
        lock (_lineLock)
        {
            if (_lineBuf.Count > 0)
            {
                line = _lineBuf.ToArray();
                _lineBuf.Clear();
            }
        }
        if (line == null) return;
        _trigger.Feed(Rows, line, OnTriggerHit);
        _trigger.ResetStates();
    }

    private void OnPortGone(Exception ex)
    {
        LogService.Log($"串口断开：{ex.GetType().Name}: {ex.Message}");
        _ui.BeginInvoke(() =>
        {
            _serial.Close();
            PortOpen = false;
            PortButtonText = "打开串口";
            StatusText = "串口已断开";
            MessageBox.Show($"串口连接中断：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    // ---------------- 发送 ----------------

    [RelayCommand]
    private void SendSelected()
    {
        if (SelectedRow == null) return;
        SendRow(SelectedRow);
    }

    public void SendRow(SendRow row)
    {
        if (!_serial.IsOpen)
        {
            MessageBox.Show("请先打开串口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TriggerEngine.TryCompileContent(row);
        if (row.TxBytes.Length == 0)
        {
            MessageBox.Show("该行内容为空或解析失败", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_serial.Write(row.TxBytes))
        {
            MessageBox.Show($"发送失败（请检查串口是否正常）", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _txTotal += row.TxBytes.Length;
        UpdateCounters();
        AppendReceive(row.TxBytes, isTx: true);
    }

    private enum LoopKind { None, Single, All }
    private LoopKind _loopKind;

    /// <summary>停止任一正在运行的循环（单行循环/全部循环共用）。</summary>
    private void StopLoop(string what)
    {
        _loopCts?.Cancel();
        _loopKind = LoopKind.None;
        LoopRunning = false;
        LoopButtonText = "循环发送";
        LoopAllButtonText = "循环全部";
        LogService.Log($"{what}停止（已发送 {_loopSent} 次）");
    }

    [RelayCommand]
    private void ToggleLoop()
    {
        if (LoopRunning)
        {
            // 只有单行循环运行中才停；循环全部运行中点此按钮无动作（用户决策）
            if (_loopKind == LoopKind.Single) StopLoop("循环发送");
            return;
        }
        if (SelectedRow == null)
        {
            MessageBox.Show("请先选中一行", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 先检查串口是否打开（与单次发送一致）
        if (!_serial.IsOpen)
        {
            MessageBox.Show("请先打开串口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 循环参数对话框（行号/次数/间隔）
        LoopRowNumber = Rows.IndexOf(SelectedRow) + 1;
        var dlg = new LoopDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = this,
        };
        if (dlg.ShowDialog() != true) return;

        int rowIdx = LoopRowNumber - 1;
        if (rowIdx < 0 || rowIdx >= Rows.Count)
        {
            MessageBox.Show("行号超出范围", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (LoopCount < 0) LoopCount = 0;
        if (LoopInterval < 1) LoopInterval = 1;

        var row = Rows[rowIdx];
        TriggerEngine.TryCompileContent(row);
        if (row.TxBytes.Length == 0) return;

        LoopRunning = true;
        LoopButtonText = "停止";
        _loopKind = LoopKind.Single;
        _loopSent = 0;
        LogService.Log($"循环发送开始：行 {LoopRowNumber}，次数 {(LoopCount == 0 ? "无限" : LoopCount.ToString())}，间隔 {LoopInterval}ms");
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && (LoopCount == 0 || _loopSent < LoopCount))
                {
                    if (!_serial.Write(row.TxBytes)) break;   // 发送失败：结束循环
                    _txTotal += row.TxBytes.Length;
                    _loopSent++;
                    _ = _ui.BeginInvoke(() =>
                    {
                        UpdateCounters();
                        AppendReceive(row.TxBytes, isTx: true);
                    });
                    await Task.Delay(LoopInterval, ct);
                }
            }
            catch (TaskCanceledException) { }
            _ = _ui.BeginInvoke(() =>
            {
                _loopKind = LoopKind.None;
                LoopRunning = false;
                LoopButtonText = "循环发送";
                LoopAllButtonText = "循环全部";
            });
        }, ct);
    }

    /// <summary>循环发送列表中所有行：每轮按行序发送（含触发/匹配行，用户决策），
    /// 每行发 SendCount 次，每次发送后等行间间隔，一轮结束等轮间间隔；
    /// 轮数 0 = 无限；SendCount = 0 的行跳过。</summary>
    [RelayCommand]
    private void ToggleLoopAll()
    {
        if (LoopRunning)
        {
            // 只有循环全部运行中才停；单行循环运行中点此按钮无动作（用户决策）
            if (_loopKind == LoopKind.All) StopLoop("循环全部");
            return;
        }
        if (Rows.Count == 0)
        {
            MessageBox.Show("发送列表为空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 先检查串口是否打开（与单次发送一致）
        if (!_serial.IsOpen)
        {
            MessageBox.Show("请先打开串口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new LoopAllDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = this,
        };
        if (dlg.ShowDialog() != true) return;

        if (LoopAllCycles < 0) LoopAllCycles = 0;
        if (LoopAllRowInterval < 1) LoopAllRowInterval = 1;
        if (LoopAllCycleInterval < 1) LoopAllCycleInterval = 1;

        // 预编译所有行；全部为空/跳过则提示
        foreach (var r in Rows) TriggerEngine.TryCompileContent(r);
        var active = Rows.Where(r => r.TxBytes.Length > 0 && r.SendCount > 0).ToList();
        if (active.Count == 0)
        {
            MessageBox.Show("没有可发送的行（内容为空/解析失败或次数为 0）", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoopRunning = true;
        LoopAllButtonText = "停止";
        _loopKind = LoopKind.All;
        _loopSent = 0;
        LogService.Log($"循环发送全部开始：{active.Count} 行，轮数 {(LoopAllCycles == 0 ? "无限" : LoopAllCycles.ToString())}，" +
                       $"行间间隔 {LoopAllRowInterval}ms，轮间间隔 {LoopAllCycleInterval}ms");
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        var rows = active;
        _ = Task.Run(async () =>
        {
            try
            {
                int cycles = LoopAllCycles;
                int doneCycles = 0;
                while (!ct.IsCancellationRequested && (cycles == 0 || doneCycles < cycles))
                {
                    foreach (var r in rows)
                    {
                        if (ct.IsCancellationRequested) break;
                        for (int k = 0; k < r.SendCount; k++)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (!_serial.Write(r.TxBytes)) return;   // 发送失败：结束循环
                            _txTotal += r.TxBytes.Length;
                            _loopSent++;
                            _ = _ui.BeginInvoke(() =>
                            {
                                UpdateCounters();
                                AppendReceive(r.TxBytes, isTx: true);
                            });
                            await Task.Delay(LoopAllRowInterval, ct);
                        }
                    }
                    doneCycles++;
                    if (!ct.IsCancellationRequested)
                        await Task.Delay(LoopAllCycleInterval, ct);
                }
            }
            catch (TaskCanceledException) { }
            _ = _ui.BeginInvoke(() =>
            {
                _loopKind = LoopKind.None;
                LoopRunning = false;
                LoopButtonText = "循环发送";
                LoopAllButtonText = "循环全部";
            });
        }, ct);
    }

    /// <summary>双击内容格 = 发送该行。</summary>
    public void OnRowDoubleClick(SendRow row) => SendRow(row);

    /// <summary>双击次数表头：全部行次数重置为 1。</summary>
    public void ResetAllCounts()
    {
        foreach (var r in Rows) r.SendCount = 1;
    }

    // ---------------- 行操作 ----------------

    [RelayCommand]
    private void AddRow()
    {
        if (Rows.Count >= MaxRows) { MessageBox.Show("最多支持 200 行", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var row = new SendRow();
        Rows.Add(row);
        SelectedRow = row;
        RebuildTriggers();
    }

    [RelayCommand]
    private void InsertRow()
    {
        if (Rows.Count >= MaxRows) { MessageBox.Show("最多支持 200 行", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        int idx = SelectedRow == null ? Rows.Count : Math.Max(0, Rows.IndexOf(SelectedRow));
        var row = new SendRow();
        Rows.Insert(idx, row);
        SelectedRow = row;
        RebuildTriggers();
    }

    [RelayCommand]
    private void DeleteRow()
    {
        if (SelectedRow == null) return;
        Rows.Remove(SelectedRow);
        RebuildTriggers();
    }

    [RelayCommand]
    private void MoveUp()
    {
        int i = SelectedRow == null ? -1 : Rows.IndexOf(SelectedRow);
        if (i <= 0) return;
        Rows.Move(i, i - 1);
        RebuildTriggers();
    }

    [RelayCommand]
    private void MoveDown()
    {
        int i = SelectedRow == null ? -1 : Rows.IndexOf(SelectedRow);
        if (i < 0 || i >= Rows.Count - 1) return;
        Rows.Move(i, i + 1);
        RebuildTriggers();
    }

    /// <summary>内容提交校验：HEX 解析失败整行回滚并提示。</summary>
    public void ValidateRowContent(SendRow row)
    {
        if (row.Format != RowFormat.Hex)
        {
            row.LastValidContent = row.Content;
            return;
        }
        if (TriggerEngine.TryCompileContent(row))
        {
            row.LastValidContent = row.Content;
            return;
        }
        MessageBox.Show("HEX 格式解析失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        row.Content = row.LastValidContent;
        TriggerEngine.TryCompileContent(row);
    }

    /// <summary>双击格式格：该行 HEX/ASCII 互切。</summary>
    public void ToggleRowFormat(SendRow row)
    {
        row.Format = row.Format == RowFormat.Hex ? RowFormat.Ascii : RowFormat.Hex;
        if (!TriggerEngine.TryCompileContent(row))
        {
            row.Format = row.Format == RowFormat.Hex ? RowFormat.Ascii : RowFormat.Hex;
            MessageBox.Show("格式切换失败：内容在该格式下无法解析，已还原", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        row.LastValidContent = row.Content;
        RebuildTriggers();
    }

    /// <summary>双击格式表头：全部行一起切换；解析失败的行保持原格式。</summary>
    public void ToggleAllFormats()
    {
        int failed = 0;
        foreach (var row in Rows)
        {
            var old = row.Format;
            row.Format = row.Format == RowFormat.Hex ? RowFormat.Ascii : RowFormat.Hex;
            if (!TriggerEngine.TryCompileContent(row)) { row.Format = old; failed++; }
            row.LastValidContent = row.Content;
        }
        RebuildTriggers();
        if (failed > 0)
            MessageBox.Show("部分行内容在该格式下无法解析，已保持原格式", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>右键菜单：设置行角色。</summary>
    /// <summary>设置行角色。设为匹配行时必须**显式指定目标触发行**
    /// （先设触发行，右键"设为匹配行"子菜单列出所有触发行供选择关联）。</summary>
    public void SetRowRole(SendRow? row, RowRole role, int matchTarget = -1)
    {
        if (row == null) return;
        if (role == RowRole.Match)
        {
            if (matchTarget < 0 || matchTarget >= Rows.Count ||
                Rows[matchTarget].Role != RowRole.Trigger)
            {
                MessageBox.Show("请先设置触发行，并在子菜单中选择要关联的触发行", "提示",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            row.TargetIndex = matchTarget;
            row.MatchMode = MatchMode.Equals;   // 默认完全相等
        }
        else
        {
            row.TargetIndex = -1;
        }
        row.Role = role;
        RebuildTriggers();
    }

    /// <summary>匹配方式：仅匹配行可改。</summary>
    public void SetMatchMode(SendRow? row, MatchMode mode)
    {
        if (row == null || row.Role != RowRole.Match) return;
        row.MatchMode = mode;
        RebuildTriggers();
    }

    /// <summary>查看匹配行对话框（显示匹配行、命中次数、目标行）。</summary>
    [RelayCommand]
    private void ShowMatches()
    {
        var dlg = new MatchViewDialog(Rows.ToList(), -1)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }

    /// <summary>右键"查看触发本行的匹配行"：只列关联到该触发行的匹配行。</summary>
    public void ShowMatchesForTrigger(int triggerIndex)
    {
        if (triggerIndex < 0 || triggerIndex >= Rows.Count) return;
        var dlg = new MatchViewDialog(Rows.ToList(), triggerIndex)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
    }

    private void RebuildTriggers()
    {
        _trigger.Rebuild(Rows);
        int rank = 0;
        var triggerRanks = new Dictionary<int, int>();   // 行下标 -> 触发行序号
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i].Role == RowRole.Trigger) triggerRanks[i] = rank++;
        foreach (var row in Rows)
        {
            if (row.Role == RowRole.Trigger && triggerRanks.TryGetValue(Rows.IndexOf(row), out var rk))
                row.GroupColor = new SolidColorBrush(GroupColors[rk % GroupColors.Length]);
            else if (row.Role == RowRole.Match && row.TargetIndex >= 0 && triggerRanks.TryGetValue(row.TargetIndex, out var rk2))
                row.GroupColor = new SolidColorBrush(GroupColors[rk2 % GroupColors.Length]);
            else
                row.GroupColor = Brushes.Black;
        }
    }

    // ---------------- 接收显示 ----------------

    private void AppendReceive(byte[] data, bool isTx)
    {
        // TX 显示受「发送数据」开关控制（默认关）
        if (isTx && !Display.ShowTx) return;
        // 暂停监视：RX 与 TX 都不显示，先入待显队列，恢复时按序补上
        if (RecvPaused)
        {
            _pendingRx.Add((data, isTx));
            return;
        }
        var line = new StringBuilder();
        // Shell 模式下设备回显不加时标/方向前缀
        bool shellEcho = !isTx && ShellMode;
        void AppendPrefix()
        {
            if (shellEcho) return;
            if (Display.Timestamp) line.Append($"[{DateTime.Now:HH:mm:ss.fff}] ");
            if (Display.Direction) line.Append(isTx ? "[TX] " : "[RX] ");
        }
        /* TX 另起一行：先把未结束的行收尾。
         * 触发场景下匹配帧刚写完、静默断行还没到，直接写 TX 会把两帧挤进
         * 同一行且 TX 没有自己的前缀（触发 TX 显示不出来的根因）。 */
        if (isTx && _recv.Length > 0 && _recv[^1] != '\n')
        {
            _recv.Append('\n');
            LogService.Recv("\n");
        }
        /* 显示格式由按钮/菜单切换；Shell 回显永远是 ASCII（终端观感）。
         * HEX 连续累计、**空闲才换行**（由静默断行定时器补换行），不做
         * 16 字节硬换行；前缀只在**行首**加（上一行已换行或缓冲区为空）。 */
        bool useHex = Display.Hex && !(ShellMode && !isTx);
        if (useHex)
        {
            if (_recv.Length == 0 || _recv[^1] == '\n') AppendPrefix();
            foreach (var b in data) line.Append($"{b:X2} ");
        }
        else
        {
            AppendPrefix();
            line.Append(NormalizeAscii(data));
        }
        /* TX 发送结束点已知：发完即断行。RX 不追加——
         * 它的换行由 CR/LF 归一化 + 静默断行定时器产生，块尾追加会多空行。 */
        if (isTx && !line.ToString().EndsWith('\n')) line.Append('\n');

        /* 接收窗口数据同时写入 opencom.txt（与显示逐字一致）。
         * 换行策略与屏幕相同：CR/LF 归一化 + 静默断行定时器产生，
         * 不按接收块强制断行（静默定时器补换行时也会写日志）。 */
        LogService.Recv(line.ToString());

        _lastAppendTick = DateTime.Now;
        _recv.Append(line);
        if (_recv.Length > RecvTextCap) _recv.Remove(0, _recv.Length - RecvTextCap);
        ReceiveText = _recv.ToString();
        OnPropertyChanged(nameof(ReceiveText));
    }

    private void UpdateCounters() => CountersText = $"RX: {_rxTotal}  TX: {_txTotal}";

    /// <summary>
    /// ASCII 接收归一化：
    /// CR→换行、CR CR LF 里冗余 CR 吞掉、裸 LF→换行、Tab 保留、
    /// 其余控制字符显示 '.'、UTF-8 不完整序列跨块续接、非法字节 '.'。
    /// </summary>
    private string NormalizeAscii(byte[] data)
    {
        var total = new byte[_utf8Tail.Count + data.Length];
        _utf8Tail.CopyTo(total);
        data.CopyTo(total, _utf8Tail.Count);
        _utf8Tail.Clear();

        var sb = new StringBuilder();
        int i2 = 0;
        while (i2 < total.Length)
        {
            int need = Utf8SeqLen(total[i2]);
            if (i2 + need > total.Length) break;             // 不完整序列 → 留给下一块
            if (need == 1)
            {
                byte c = total[i2];
                if (c == (byte)'\r')
                {
                    if (!_prevCr) sb.Append("\r\n");
                    _prevCr = true;                          // 冗余 CR 吞掉
                }
                else if (c == (byte)'\n')
                {
                    if (!_prevCr) sb.Append("\r\n");         // 裸 LF 也换行
                    _prevCr = false;
                }
                else
                {
                    _prevCr = false;
                    if (c == (byte)'\t') sb.Append('\t');
                    else if (c < 0x20 || c == 0x7F) sb.Append('.');
                    else sb.Append((char)c);
                }
            }
            else if (IsValidUtf8(total, i2, need))
            {
                _prevCr = false;
                sb.Append(Encoding.UTF8.GetString(total, i2, need));
            }
            else
            {
                _prevCr = false;
                sb.Append('.');
            }
            i2 += need;
        }
        if (i2 < total.Length)
            for (int k = i2; k < total.Length; k++) _utf8Tail.Add(total[k]);
        return sb.ToString();
    }


private static int Utf8SeqLen(byte b) => b switch
    {
        < 0x80 => 1,
        < 0xC0 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        _ => 4,
    };

    private static bool IsValidUtf8(byte[] buf, int start, int len)
    {
        try
        {
            return Encoding.UTF8.GetString(buf, start, len) != "�";
        }
        catch
        {
            return false;
        }
    }
    [RelayCommand]
    private void ClearReceive()
    {
        // 清空前若有未断行的数据，先在日志里补换行收尾：否则下一次接收
        // 会拼到这条旧行上，日志与（已清空的）屏幕内容不一致。
        if (_recv.Length > 0 && _recv[^1] != '\n') LogService.Recv("\n");
        _recv.Clear();
        ReceiveText = "";
        OnPropertyChanged(nameof(ReceiveText));
    }

    [RelayCommand]
    private void CopyReceive()
    {
        if (!string.IsNullOrEmpty(ReceiveText)) Clipboard.SetText(ReceiveText);
    }

    [RelayCommand]
    private void SaveReceive()
    {
        var dlg = new SaveFileDialog { Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*", FileName = "receive.txt" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, ReceiveText);
    }

    // ---------------- 显示开关 / Shell ----------------

    partial void OnShellModeChanged(bool value)
    {
        // 退出 Shell 时也要恢复状态栏（否则残留 Shell 提示）
        if (value) StatusText = "Shell 模式：键入即发送，Esc 退出";
        else UpdateStatusText();
    }

    [RelayCommand]
    private void ToggleShell()
    {
        ShellMode = !ShellMode;
    }

    /// <summary>Shell 模式：敲一个字符立即发送一个字符。
    /// 键入的内容**不显示**（只有设备回显），
    /// 即使勾选「显示发送数据」也不显示。</summary>
    public void ShellKey(string text)
    {
        if (!ShellMode) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!_serial.Write(bytes)) return;
        _txTotal += bytes.Length;
        UpdateCounters();
    }

    public byte[] ShellEnterBytes()
    {
        var eol = Display.ShellEol switch { 0 => "\r\n", 2 => "\n", _ => "\r" };
        return Encoding.UTF8.GetBytes(eol);
    }

    /// <summary>Shell 模式发送原始字节（EOL/控制字符），计入 TX 统计。
    /// 同样不显示（与键入一致）。</summary>
    public void ShellKeyRaw(byte[] data)
    {
        if (data.Length == 0) return;
        if (!_serial.Write(data)) return;
        _txTotal += data.Length;
        UpdateCounters();
    }

    public void SetShellEol(int eol)
    {
        Display.ShellEol = eol;
    }

    // ---------------- 收尾 ----------------

    public void Shutdown()
    {
        _loopCts?.Cancel();
        _silenceTimer.Stop();
        _serial.Close();
        _config.Save(Serial, Display, Rows.ToList());
        LogService.Log($"opencom 退出：RX {_rxTotal} 字节，TX {_txTotal} 字节");
    }
}
