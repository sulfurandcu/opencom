using System.Windows;
using OpenCom.Wpf.Services;

namespace OpenCom.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Log($"未处理异常：{args.Exception}");
            args.Handled = true;
        };
    }
}

