using System.IO;
using System.Text.Json;
using OpenCom.Wpf.Models;

namespace OpenCom.Wpf.Services;

/// <summary>JSON 配置持久化。行内容与串口/显示/触发参数一起存。
/// 主配置：C:\Users\&lt;用户名&gt;\.opencom\opencom.json（与 log/txt 同目录）；
/// 命名配置：同目录 configs\&lt;名称&gt;.json（文件菜单保存/加载）。</summary>
public sealed class ConfigService
{
    private static string HomeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencom");

    private static string MainPath => Path.Combine(HomeDir, "opencom.json");
    private static string ConfigsDir => Path.Combine(HomeDir, "configs");

    private sealed class ConfigDto
    {
        public SerialSettings? Serial { get; set; }
        public DisplayOptions? Display { get; set; }
        public TriggerOptions? Trigger { get; set; }
        public List<RowDto>? Rows { get; set; }
    }

    private sealed class RowDto
    {
        public string? Content { get; set; }
        public int Format { get; set; }
        public int Role { get; set; }
        public int MatchMode { get; set; }
        public int TargetIndex { get; set; }
        public int? SendCount { get; set; }   // 旧配置无此字段 → 默认 1
    }

    public void Save(SerialSettings serial, DisplayOptions display, TriggerOptions trigger, IList<SendRow> rows)
    {
        Directory.CreateDirectory(HomeDir);
        File.WriteAllText(MainPath, Serialize(BuildDto(serial, display, trigger, rows)));
    }

    public (SerialSettings serial, DisplayOptions display, TriggerOptions trigger, List<SendRow> rows) Load()
    {
        if (!File.Exists(MainPath))
            return (new SerialSettings(), new DisplayOptions(), new TriggerOptions(), new List<SendRow>());
        try
        {
            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(MainPath));
            return FromDto(dto);
        }
        catch { /* 配置损坏时用默认值启动 */ }
        return (new SerialSettings(), new DisplayOptions(), new TriggerOptions(), new List<SendRow>());
    }

    // ---------------- 命名配置（文件菜单） ----------------

    /// <summary>已保存的命名配置（按名称排序）。</summary>
    public string[] ListConfigs()
    {
        if (!Directory.Exists(ConfigsDir)) return Array.Empty<string>();
        try
        {
            return Directory.GetFiles(ConfigsDir, "*.json")
                .Select(p => Path.GetFileNameWithoutExtension(p)!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public void SaveNamed(string name, SerialSettings serial, DisplayOptions display, TriggerOptions trigger, IList<SendRow> rows)
    {
        Directory.CreateDirectory(ConfigsDir);
        File.WriteAllText(Path.Combine(ConfigsDir, Sanitize(name) + ".json"),
                          Serialize(BuildDto(serial, display, trigger, rows)));
    }

    public (SerialSettings serial, DisplayOptions display, TriggerOptions trigger, List<SendRow> rows)? LoadNamed(string name)
    {
        var path = Path.Combine(ConfigsDir, Sanitize(name) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(path));
            return FromDto(dto);
        }
        catch { return null; }
    }

    /// <summary>配置名里的非法文件名字符换成下划线。</summary>
    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public void DeleteNamed(string name)
    {
        var path = Path.Combine(ConfigsDir, Sanitize(name) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>重命名；目标名已存在或源不存在返回 false。</summary>
    public bool RenameNamed(string oldName, string newName)
    {
        var from = Path.Combine(ConfigsDir, Sanitize(oldName) + ".json");
        var to = Path.Combine(ConfigsDir, Sanitize(newName) + ".json");
        if (!File.Exists(from) || File.Exists(to)) return false;
        File.Move(from, to);
        return true;
    }

    // ---------------- DTO 转换 ----------------

    private static string Serialize(ConfigDto dto) =>
        JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

    private static ConfigDto BuildDto(SerialSettings serial, DisplayOptions display, TriggerOptions trigger, IList<SendRow> rows) =>
        new()
        {
            Serial = serial,
            Display = display,
            Trigger = trigger,
            Rows = rows.Select(r => new RowDto
            {
                Content = r.Content,
                Format = (int)r.Format,
                Role = (int)r.Role,
                MatchMode = (int)r.MatchMode,
                TargetIndex = r.TargetIndex,
                SendCount = r.SendCount,
            }).ToList(),
        };

    private static (SerialSettings, DisplayOptions, TriggerOptions, List<SendRow>) FromDto(ConfigDto? dto)
    {
        var serial = new SerialSettings();
        var display = new DisplayOptions();
        var trigger = new TriggerOptions();
        var rows = new List<SendRow>();
        if (dto?.Serial != null) serial = dto.Serial;
        if (dto?.Display != null) display = dto.Display;
        if (dto?.Trigger != null) trigger = dto.Trigger;
        if (dto?.Rows != null)
        {
            foreach (var rd in dto.Rows)
            {
                rows.Add(new SendRow
                {
                    Content = rd.Content ?? "",
                    Format = (RowFormat)rd.Format,
                    Role = (RowRole)rd.Role,
                    MatchMode = (MatchMode)rd.MatchMode,
                    TargetIndex = rd.TargetIndex,
                    SendCount = rd.SendCount ?? 1,
                });
            }
        }
        return (serial, display, trigger, rows);
    }
}
