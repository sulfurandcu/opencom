using System.IO;

namespace OpenCom.Wpf.Services;

/// <summary>
/// 文件日志：运行日志 → C:\Users\&lt;用户名&gt;\.opencom\opencom.log，
/// 接收窗口数据 → 同目录 opencom.txt（用户指定）。
/// </summary>
public static class LogService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencom");

    public static string LogPath => Path.Combine(Dir, "opencom.log");
    public static string RecvPath => Path.Combine(Dir, "opencom.txt");

    private static readonly object Lock = new();

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            lock (Lock)
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* 日志失败不影响功能 */ }
    }

    public static void Recv(string text)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            lock (Lock)
                File.AppendAllText(RecvPath, text);
        }
        catch { }
    }
}
