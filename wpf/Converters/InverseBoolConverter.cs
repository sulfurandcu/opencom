using System.Globalization;
using System.Windows.Data;

namespace OpenCom.Wpf.Converters;

/// <summary>bool 取反（用于"串口打开后禁用参数条"这类绑定）。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}
