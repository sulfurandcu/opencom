using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenCom.Wpf.Models;

public enum RowRole { Normal = 0, Match, Trigger }

public enum RowFormat { Hex = 0, Ascii }

public enum MatchMode { Contains = 0, Equals, Prefix }

/// <summary>发送列表的一行。内容列始终可编辑（DataGrid 模板列里常驻 TextBox）。</summary>
public partial class SendRow : ObservableObject
{
    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private RowFormat _format = RowFormat.Hex;   // 新增行默认 HEX（用户决策）

    /// <summary>循环发送全部时本行的发送次数（0 = 跳过本行）。</summary>
    [ObservableProperty]
    private int _sendCount = 1;

    [ObservableProperty]
    private RowRole _role = RowRole.Normal;

    /// <summary>仅匹配行有效。默认完全相等。</summary>
    [ObservableProperty]
    private MatchMode _matchMode = MatchMode.Equals;

    /// <summary>仅匹配行：目标触发行下标，-1 = 未指定。</summary>
    [ObservableProperty]
    private int _targetIndex = -1;

    // ---- 编译缓存（TriggerEngine.Rebuild 生成，读循环使用） ----
    internal byte[] TxBytes { get; set; } = Array.Empty<byte>();
    internal byte[] Pattern { get; set; } = Array.Empty<byte>();

    public long MatchCount { get; set; }

    /// <summary>组颜色：同一触发行组同色（由 MainViewModel 在角色变化后重算）。</summary>
    [ObservableProperty]
    private System.Windows.Media.Brush _groupColor = System.Windows.Media.Brushes.Black;

    /// <summary>最近一次校验通过的内容（HEX 提交失败回滚用）。</summary>
    internal string LastValidContent { get; set; } = "";

    /// <summary>触发列显示文本。</summary>
    public string RoleLabel => Role switch
    {
        RowRole.Match => "匹配",
        RowRole.Trigger => "・发送・",
        _ => ""
    };

    public string FormatLabel => Format == RowFormat.Hex ? "HEX" : "ASC";

    // FormatLabel/RoleLabel 是计算属性：底层字段变化时必须手动发通知，
    // 否则绑定的单元格显示不刷新（"双击格式格没反应"的根因）
    partial void OnFormatChanged(RowFormat value) => OnPropertyChanged(nameof(FormatLabel));

    partial void OnRoleChanged(RowRole value) => OnPropertyChanged(nameof(RoleLabel));
}
