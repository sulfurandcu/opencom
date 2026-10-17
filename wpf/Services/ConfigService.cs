using System.IO;
using System.Text.Json;
using OpenCom.Wpf.Models;

namespace OpenCom.Wpf.Services;

/// <summary>JSON 配置持久化。行内容与串口/显示参数一起存。
/// 路径 C:\Users\&lt;用户名&gt;\.opencom\opencom.json（与 log/txt 同目录）。</summary>
public sealed class ConfigService
{
    private readonly string _path =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".opencom", "opencom.json");

    private sealed class ConfigDto
    {
        public SerialSettings? Serial { get; set; }
        public DisplayOptions? Display { get; set; }
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

    public void Save(SerialSettings serial, DisplayOptions display, IList<SendRow> rows)
    {
        var dto = new ConfigDto
        {
            Serial = serial,
            Display = display,
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
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    public (SerialSettings serial, DisplayOptions display, List<SendRow> rows) Load()
    {
        var serial = new SerialSettings();
        var display = new DisplayOptions();
        var rows = new List<SendRow>();
        if (!File.Exists(_path)) return (serial, display, rows);

        try
        {
            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(_path));
            if (dto?.Serial != null) serial = dto.Serial;
            if (dto?.Display != null) display = dto.Display;
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
        }
        catch { /* 配置损坏时用默认值启动 */ }

        return (serial, display, rows);
    }
}
