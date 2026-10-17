using System.Collections.Concurrent;
using System.IO.Ports;
using OpenCom.Wpf.Models;

namespace OpenCom.Wpf.Services;

/// <summary>
/// 串口读写封装：
/// RX 由读线程同步 Read + ReadTimeout 轮询；
/// TX 走**非阻塞队列**——写线程消费队列，UI 只入队、永不因写超时卡住。
/// </summary>
public sealed class SerialService
{
    private const int TxQueueCap = 64 * 1024;   // 待发队列上限（字节），满则丢弃

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _txTask;
    private BlockingCollection<byte[]>? _txQueue;
    private long _txPending;   // 队列中待发字节数
    private readonly object _openLock = new();   // 串行化打开/关闭，避免异步关闭与新打开竞态
    private volatile bool _open;                 // 逻辑开关状态：Close 立即置 false（异步关闭期间 IsOpen 也要立刻反映）
    private Task _closeTask = Task.CompletedTask;   // 最近一次异步关闭任务（Open 前必须等它完成）

    public bool IsOpen => _open && _port is { IsOpen: true };
    public string? CurrentPort => _port?.PortName;

    /// <summary>收到数据（原始字节）。在后台线程触发。</summary>
    public event Action<byte[]>? DataReceived;
    /// <summary>串口意外断开（异常退出读循环）。</summary>
    public event Action<Exception>? PortGone;
    /// <summary>写线程实际发送失败（UI 已入队成功，这里只报告）。</summary>
    public event Action<string>? WriteFailed;

    /// <summary>端口按**数字**排序（COM1..COM255 顺序），
    /// 不用字符串序——否则 COM7 会排在 COM101 后面。</summary>
    public static string[] EnumeratePorts() =>
        SerialPort.GetPortNames()
            .OrderBy(p => int.TryParse(p.AsSpan(3), out var n) ? n : int.MaxValue)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToArray();

    public void Open(SerialSettings s)
    {
        Close();
        /* 必须等异步关闭**真正完成**再开新端口：否则排队中的关闭任务会在
         * 新端口打开之后执行，把新端口又关掉（"打开了还提示请先打开串口"
         * 的根因——日志里连续多次打开、无关闭记录）。 */
        _closeTask.Wait();
        lock (_openLock)
        {
            int baud = s.Baud > 0 ? s.Baud : 115200;
            var port = new SerialPort(s.Port, baud, s.Parity, s.DataBits, s.StopBits)
            {
                Handshake = s.FlowControl,
                DtrEnable = s.Dtr,
                RtsEnable = s.Rts,
                ReadTimeout = 500,
                WriteTimeout = 500,   // 每次写前由写线程按数据长度/波特率重算
            };
            port.Open();
            /* 清掉关闭期间积累的陈旧数据：端口关着时设备可能还在发，
             * 数据堆在驱动缓冲里，一打开会先把过时数据吐出来 */
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            _open = true;
            _port = port;
            _cts = new CancellationTokenSource();
            _txQueue = new BlockingCollection<byte[]>();
            _txPending = 0;
            _readTask = Task.Run(() => ReadLoop(port, _cts.Token));
            _txTask = Task.Run(() => WriteLoop(port, _txQueue, _cts.Token));
        }
    }

    public void Close()
    {
        _open = false;
        _cts?.Cancel();
        _txQueue?.CompleteAdding();
        /* 实际的 port.Close() 放后台线程：刚发过数据时，串口流会等挂起的
         * 写排空（最长 WriteTimeout 2 秒）才返回，同步关会让 UI 卡住
         * （"发送数据后关闭串口有时很慢"）。Open 会等这个任务完成。 */
        var port = _port;
        _closeTask = Task.Run(() =>
        {
            lock (_openLock)
            {
                try
                {
                    // 关闭前清空驱动缓冲：残留的旧数据不要留给下一次打开
                    port?.DiscardInBuffer();
                    port?.DiscardOutBuffer();
                    port?.Close();
                }
                catch { }
                if (ReferenceEquals(_port, port)) _port = null;
                _txQueue = null;
            }
        });
    }

    /// <summary>发送数据（非阻塞入队）。端口未开返回 false，队列满丢弃并记日志。</summary>
    public bool Write(byte[] data)
    {
        if (_port is not { IsOpen: true } || _txQueue == null || data.Length == 0)
            return false;
        if (Interlocked.Read(ref _txPending) + data.Length > TxQueueCap)
        {
            LogService.Log($"发送队列已满（{Interlocked.Read(ref _txPending)} 字节待发），丢弃 {data.Length} 字节");
            return false;
        }
        Interlocked.Add(ref _txPending, data.Length);
        try
        {
            _txQueue.Add(data);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;   // 队列已关闭（端口刚被关）
        }
    }

    public void SetDtr(bool on) { if (_port is { IsOpen: true }) _port.DtrEnable = on; }
    public void SetRts(bool on) { if (_port is { IsOpen: true }) _port.RtsEnable = on; }

    // ---------------- 后台线程 ----------------

    private void ReadLoop(SerialPort port, CancellationToken ct)
    {
        /* 用同步 Read + ReadTimeout 轮询，不用 BaseStream.ReadAsync：
         * 实测 CH340 等 USB 串口上异步读会立刻返回 0 字节，循环空转上百次后
         * 驱动报 "I/O operation has been aborted"，被误判成串口断开（COM7 打不开
         * 的根因）。同步读超时抛 TimeoutException，属正常轮询。 */
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = port.Read(buf, 0, buf.Length);
                }
                catch (TimeoutException) { continue; }        // 超时轮询，正常
                catch (InvalidOperationException) { break; }  // 端口已关闭
                catch (OperationCanceledException) { break; } // 关闭中止挂起读，正常（别误报 PortGone）
                if (n <= 0) continue;
                var copy = new byte[n];
                Buffer.BlockCopy(buf, 0, copy, 0, n);
                DataReceived?.Invoke(copy);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                PortGone?.Invoke(ex);
        }
    }

    private void WriteLoop(SerialPort port, BlockingCollection<byte[]> queue, CancellationToken ct)
    {
        try
        {
            foreach (var data in queue.GetConsumingEnumerable(ct))
            {
                try
                {
                    /* 写超时按**本次数据长度**和波特率计算：
                     * 字节数 × 10 位/字节 ÷ 波特率 × 1.5 余量，最低 500ms。 */
                    int baud = port.BaudRate > 0 ? port.BaudRate : 115200;
                    port.WriteTimeout = Math.Max(500, (int)((long)data.Length * 15_000 / baud));
                    port.Write(data, 0, data.Length);
                }
                catch (Exception ex)
                {
                    LogService.Log($"发送失败：{ex.GetType().Name}: {ex.Message}");
                    WriteFailed?.Invoke($"发送失败（设备未响应或对端未连接）：{ex.Message}");
                }
                Interlocked.Add(ref _txPending, -data.Length);
            }
        }
        catch (OperationCanceledException) { }
    }
}
