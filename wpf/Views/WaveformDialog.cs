using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenCom.Wpf.Models;

namespace OpenCom.Wpf.Views;

/// <summary>串口波形查看对话框：按当前串口参数（波特率/数据位/校验/停止位）把发送数据
/// 画成 UART 时序波形。竖虚线标出每位边界，滚轮缩放、左键拖动平移。</summary>
public class WaveformDialog : Window
{
    public WaveformDialog(byte[] bytes, SerialSettings s)
    {
        Title = "查看波形";
        Width = 860;
        Height = 320;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 12;

        var frame = new FrameTiming(s);
        // 单位自适应：<1us 用 ns，<1ms 用 us，<1s 用 ms，≥1s 用 s（如 3333ms → 3.3s）
        string Fmt(double sec) => sec < 1e-6 ? $"{sec * 1e9:F1}ns"
            : sec < 1e-3 ? $"{sec * 1e6:F1}us"
            : sec < 1 ? $"{sec * 1e3:F1}ms"
            : $"{sec:F2}s";
        double bitWidth = 1.0 / frame.Baud;                       // X：位宽
        double byteWidth = frame.TotalBits / frame.Baud;          // Y：字节宽
        double total = byteWidth * bytes.Length;                  // Z：总时长
        string parityLetter = "NOPMS"[(int)s.Parity].ToString();
        string stopText = s.StopBits switch
        {
            System.IO.Ports.StopBits.OnePointFive => "1.5",
            System.IO.Ports.StopBits.Two => "2",
            _ => "1",
        };

        var info = new TextBlock
        {
            Text = $"{s.Baud} {s.DataBits}/{parityLetter}/{stopText}" +
                   $"・{Fmt(bitWidth)}・{Fmt(byteWidth)} × {bytes.Length} = {Fmt(total)}",
            Margin = new Thickness(12, 10, 12, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x32)),
        };

        var view = new WaveformView(bytes, frame) { Margin = new Thickness(12, 6, 12, 12) };

        var dock = new DockPanel();
        DockPanel.SetDock(info, Dock.Top);
        dock.Children.Add(info);
        dock.Children.Add(view);

        Content = dock;
    }
}

/// <summary>一帧的位时长组成：起始位 + 数据位（LSB 先行）+ 可选校验位 + 停止位。</summary>
public sealed class FrameTiming
{
    /// <summary>第一个起始位前的空闲高电平时长（位）。</summary>
    public const double IdleBits = 1;

    public int Baud;
    public int DataBits = 8;
    public bool HasParity;
    public double StopBitCount = 1;
    public Parity ParityKind;

    /// <summary>每字节的总位时长（含可变长停止位）。</summary>
    public double TotalBits => 1 + DataBits + (HasParity ? 1 : 0) + StopBitCount;

    public FrameTiming(SerialSettings s)
    {
        Baud = s.Baud > 0 ? s.Baud : 115200;
        DataBits = s.DataBits is >= 5 and <= 8 ? s.DataBits : 8;
        HasParity = s.Parity != Parity.None;
        ParityKind = s.Parity;
        StopBitCount = s.StopBits switch { StopBits.OnePointFive => 1.5, StopBits.Two => 2, _ => 1 };
    }

    /// <summary>生成一字节的位序列：(种类, 值, 位时长)。值 0=低 1=高。</summary>
    public IEnumerable<(BitKind Kind, double Value, double Duration)> ByteBits(byte b)
    {
        yield return (BitKind.Start, 0, 1);                   // 起始位
        for (int i = 0; i < DataBits; i++)
            yield return (BitKind.Data, ((b >> i) & 1) == 1 ? 1 : 0, 1);   // 数据位 LSB 先行
        if (HasParity)
        {
            int ones = 0;
            for (int i = 0; i < DataBits; i++)
                if (((b >> i) & 1) == 1) ones++;
            double p = ParityKind switch
            {
                Parity.Odd => (ones % 2 == 0) ? 1 : 0,        // 奇校验：1 的个数为奇
                Parity.Even => (ones % 2 == 1) ? 1 : 0,       // 偶校验
                Parity.Mark => 1,
                Parity.Space => 0,
                _ => 0,
            };
            yield return (BitKind.Parity, p, 1);              // 校验位
        }
        yield return (BitKind.Stop, 1, StopBitCount);         // 停止位
    }
}

public enum BitKind { Start, Data, Parity, Stop }

/// <summary>波形绘制控件：自绘 UART 时序波形，竖虚线标出每位边界。</summary>
public class WaveformView : FrameworkElement
{
    // 波形颜色按位类型区分：起始位红 / 数据位蓝 / 校验位紫 / 停止位绿
    private static readonly Pen StartPen = new(new SolidColorBrush(Color.FromRgb(0xD8, 0x40, 0x40)), 1.2);
    private static readonly Pen DataPen = new(new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xC0)), 1.2);
    private static readonly Pen ParityPen = new(new SolidColorBrush(Color.FromRgb(0x8E, 0x44, 0xAD)), 1.2);
    private static readonly Pen StopPen = new(new SolidColorBrush(Color.FromRgb(0x1E, 0x84, 0x4B)), 1.2);
    private static readonly Pen IdlePen = new(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x98)), 1.2);   // 起始前的空闲高电平
    private static readonly Brush BitBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x90, 0x90, 0xA8));
    private static readonly Pen BitPen = new(BitBrush, 0.8)
        { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };   // 位边界：灰色虚线（4-3 节拍，稀疏时才能看出是虚线）
    private static readonly Brush ByteBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0xD8, 0x40, 0x40));
    private static readonly Pen BytePen = new(ByteBrush, 0.7)
        { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) };   // 字节边界：红色虚线
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x66));
    private static readonly Pen TickPen = new(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA8)), 0.8);   // 时间轴刻度

    private readonly byte[] _bytes;
    private readonly FrameTiming _frame;
    private readonly double _bitTime;      // 秒/位
    private readonly double _idleTime;     // 起始前的空闲高电平时长（秒）
    private readonly double _totalTime;    // 秒

    private double _zoom = 1.0;            // 像素/秒
    private double _offset;                // 像素
    private Point _dragStart;
    private double _dragOffset;
    private bool _dragging;

    public WaveformView(byte[] bytes, FrameTiming frame)
    {
        _bytes = bytes;
        _frame = frame;
        _bitTime = 1.0 / frame.Baud;
        _idleTime = FrameTiming.IdleBits * _bitTime;
        _totalTime = (2 * _idleTime + bytes.Length * frame.TotalBits * _bitTime);
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.SizeWE;
        SizeChanged += (_, _) => FitToWidth();
    }

    private void FitToWidth()
    {
        // 打开时波形居中：占窗口宽度 96%，左右各留 2% 边距
        if (ActualWidth > 0 && _totalTime > 0)
        {
            _zoom = ActualWidth * 0.96 / _totalTime;
            _offset = ActualWidth * 0.02;
        }
        InvalidateVisual();
    }

    // ---------------- 交互：滚轮缩放（锚定光标位置）、左键拖动平移 ----------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        double x = e.GetPosition(this).X;
        double t = (x - _offset) / _zoom;          // 光标下的时间点
        // 缩放下限：总波形宽度不小于窗口的 80%
        double minZoom = _totalTime > 0 ? ActualWidth * 0.8 / _totalTime : 1.0;
        _zoom = Math.Max(minZoom, _zoom * factor);
        _offset = x - t * _zoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragOffset = _offset;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _offset = _dragOffset + (p.X - _dragStart.X);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---------------- 绘制 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bg = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bytes.Length == 0 || _zoom <= 0) return;

        // 顶部 46：上方标注文字（底边距波形 6px）+ 上方空白 ≈27px；
        // 底部 ActualHeight-46：下方标注下方也留 ≈27px 空白，与上方对称
        double top = 46, bottom = ActualHeight - 46;
        double yHigh = top, yLow = bottom;
        double tToX(double t) => t * _zoom + _offset;
        double levelToY(double level) => level > 0.5 ? yHigh : yLow;

        // 每字节一条红色虚线（字节边界；第一个字节从空闲高电平之后开始）
        double tPerByte = _frame.TotalBits * _bitTime;
        for (int i = 0; i <= _bytes.Length; i++)
        {
            double x = tToX(_idleTime + i * tPerByte);
            if (x >= 0 && x <= ActualWidth)
                dc.DrawLine(BytePen, new Point(x, top), new Point(x, bottom));
        }

        // 波形：只画**水平线段**（不画竖线），按位类型着色——
        // 起始位红 / 数据位蓝 / 校验位紫 / 停止位绿；电平跳变由位边界虚线表达。
        // 第一个起始位前有一段空闲高电平（灰色）。
        var geos = new Dictionary<BitKind, StreamGeometry>
        {
            [BitKind.Start] = new StreamGeometry(),
            [BitKind.Data] = new StreamGeometry(),
            [BitKind.Parity] = new StreamGeometry(),
            [BitKind.Stop] = new StreamGeometry(),
        };
        var ctxs = geos.ToDictionary(kv => kv.Key, kv => kv.Value.Open());
        try
        {
            double t = _idleTime;
            foreach (var b in _bytes)
            {
                foreach (var (kind, v, d) in _frame.ByteBits(b))
                {
                    double x0 = tToX(t), x1 = tToX(t + d * _bitTime);
                    t += d * _bitTime;
                    if (x1 < 0 || x0 > ActualWidth) continue;
                    double y = levelToY(v);
                    ctxs[kind].BeginFigure(new Point(x0, y), false, false);
                    ctxs[kind].LineTo(new Point(x1, y), true, false);
                }
            }
        }
        finally
        {
            foreach (var c in ctxs.Values) c.Close();
        }
        // 空闲高电平线段（灰色）：第一个起始位之前 + 最后一个停止位之后
        var idleGeo = new StreamGeometry();
        using (var c = idleGeo.Open())
        {
            c.BeginFigure(new Point(tToX(0), yHigh), false, false);
            c.LineTo(new Point(tToX(_idleTime), yHigh), true, false);
            double tEnd = _idleTime + _bytes.Length * tPerByte;
            c.BeginFigure(new Point(tToX(tEnd), yHigh), false, false);
            c.LineTo(new Point(tToX(tEnd + _idleTime), yHigh), true, false);
        }
        idleGeo.Freeze();
        dc.DrawGeometry(null, IdlePen, idleGeo);

        void DrawKind(BitKind kind, Pen pen)
        {
            geos[kind].Freeze();
            dc.DrawGeometry(null, pen, geos[kind]);
        }
        DrawKind(BitKind.Start, StartPen);
        DrawKind(BitKind.Data, DataPen);
        DrawKind(BitKind.Parity, ParityPen);
        DrawKind(BitKind.Stop, StopPen);

        // 竖虚线：每位边界（时间跨字节连续累积——之前每字节清零导致只有第一个字节有虚线）
        {
            double t = _idleTime;
            foreach (var b in _bytes)
            {
                foreach (var (_, _, d) in _frame.ByteBits(b))
                {
                    t += d * _bitTime;
                    double x = tToX(t);
                    if (x >= 0 && x <= ActualWidth)
                        dc.DrawLine(BitPen, new Point(x, top), new Point(x, bottom));
                }
            }
        }

        var typeface = new Typeface("Consolas");
        var labelTypeface = new Typeface("Microsoft YaHei UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 顶部标注（无竖线）：数据位逐个标 0/1（蓝色），校验位标 O/E/M/S（紫色）
        // 位太窄（< 14px）时不标，放大后可读
        double bitPx = _bitTime * _zoom;
        if (bitPx >= 14)
        {
            var blue = new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xC0));
            var purple = new SolidColorBrush(Color.FromRgb(0x8E, 0x44, 0xAD));
            for (int i = 0; i < _bytes.Length; i++)
            {
                double baseT = _idleTime + i * tPerByte;
                var b = _bytes[i];
                for (int k = 0; k < _frame.DataBits; k++)
                {
                    double cx = tToX(baseT + (1 + k + 0.5) * _bitTime);
                    if (cx < 0 || cx > ActualWidth) continue;
                    var ft = new FormattedText(((b >> k) & 1) == 1 ? "1" : "0",
                                               CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                               labelTypeface, 10, blue, dpi);
                    // 与下方标注对称：文字底边距波形 6px
                    dc.DrawText(ft, new Point(cx - ft.Width / 2, top - 6 - ft.Height));
                }
                if (_frame.HasParity)
                {
                    string pl = _frame.ParityKind switch
                    {
                        Parity.Odd => "O",
                        Parity.Even => "E",
                        Parity.Mark => "M",
                        _ => "S",
                    };
                    double cx = tToX(baseT + (1 + _frame.DataBits + 0.5) * _bitTime);
                    if (cx >= 0 && cx <= ActualWidth)
                    {
                        var ft = new FormattedText(pl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                                   labelTypeface, 10, purple, dpi);
                        dc.DrawText(ft, new Point(cx - ft.Width / 2, top - 6 - ft.Height));
                    }
                }
            }
        }

        // 时间轴（无刻度线）：时间从第一个起始位起算（t=0 在起始位处），
        // 每字节边界一个时间（单位三级自适应 us/ms/s）；字节值（0xAA）标在相邻时间之间
        string FmtAxis(double sec) => sec < 1e-6 ? $"{sec * 1e9:F1}ns"
            : sec < 1e-3 ? $"{sec * 1e6:F1}us"
            : sec < 1 ? $"{sec * 1e3:F1}ms"
            : $"{sec:F2}s";
        for (int i = 0; i <= _bytes.Length; i++)
        {
            double t = _idleTime + i * tPerByte;
            double x = tToX(t);
            if (x < -40 || x > ActualWidth + 40) continue;
            var ft = new FormattedText(FmtAxis(t - _idleTime),
                                       CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       typeface, 10, TextBrush, dpi);
            dc.DrawText(ft, new Point(x - ft.Width / 2, bottom + 6));
        }
        double bytePx = tPerByte * _zoom;
        if (bytePx >= 48)
        {
            for (int i = 0; i < _bytes.Length; i++)
            {
                double x = tToX(_idleTime + (i + 0.5) * tPerByte);
                if (x < -30 || x > ActualWidth + 30) continue;
                var ft = new FormattedText($"0x{_bytes[i]:X2}",
                                           CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                           typeface, 10, TextBrush, dpi);
                dc.DrawText(ft, new Point(x - ft.Width / 2, bottom + 6));
            }
        }
    }
}
