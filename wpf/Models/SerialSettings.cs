using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenCom.Wpf.Models;

/// <summary>串口参数。</summary>
public partial class SerialSettings : ObservableObject
{
    [ObservableProperty] private string _port = "COM1";
    [ObservableProperty] private int _baud = 115200;
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private System.IO.Ports.Parity _parity = System.IO.Ports.Parity.None;
    [ObservableProperty] private System.IO.Ports.StopBits _stopBits = System.IO.Ports.StopBits.One;
    [ObservableProperty] private System.IO.Ports.Handshake _flowControl = System.IO.Ports.Handshake.None;
    [ObservableProperty] private bool _dtr;
    [ObservableProperty] private bool _rts;
}

/// <summary>接收显示选项。</summary>
public partial class DisplayOptions : ObservableObject
{
    [ObservableProperty] private bool _hex = true;   // 默认 HEX 显示
    [ObservableProperty] private bool _timestamp = true;
    [ObservableProperty] private bool _direction = true;
    [ObservableProperty] private bool _showTx;
    /// <summary>Shell 回车符：0=CRLF 1=CR 2=LF。</summary>
    [ObservableProperty] private int _shellEol = 1;
}
