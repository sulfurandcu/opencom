using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OpenCom.Wpf.Models;

namespace OpenCom.Wpf.Views;

/// <summary>查看匹配行对话框（匹配行号、内容、命中次数、目标行）。</summary>
public class MatchViewDialog : Window
{
    public MatchViewDialog(IList<SendRow> rows, int filterTrigger = -1)
    {
        Title = "查看匹配行";
        Width = 660;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 12;

        var matches = new List<object>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Role != RowRole.Match) continue;
            if (filterTrigger >= 0 && r.TargetIndex != filterTrigger) continue;   // 只看关联到该触发行的
            matches.Add(new
            {
                Index = i + 1,
                Content = r.Content.Length > 40 ? r.Content[..40] + "…" : r.Content,
                Mode = r.MatchMode switch
                {
                    MatchMode.Contains => "包含",
                    MatchMode.Prefix => "前缀",
                    _ => "相等"
                },
                r.MatchCount,
                Target = r.TargetIndex >= 0 && r.TargetIndex < rows.Count ? r.TargetIndex + 1 : 0,
                Brush = r.GroupColor,   // 组颜色与发送表格一致
            });
        }

        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false,
            Margin = new Thickness(12),
            Background = Brushes.White,
        };
        /* 样式与发送表格一致：选中行 240 灰底，字体保持组色
         * （覆盖系统高亮画刷 + 样式触发器压住模板触发器的前景覆盖） */
        var selBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        grid.Resources[SystemColors.HighlightBrushKey] = selBrush;
        grid.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = selBrush;
        grid.Resources[SystemColors.HighlightTextBrushKey] = Brushes.Black;
        grid.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.Black;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new Binding("Brush")));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 0, 2, 0)));
        var selTrigger = new DataTrigger
        {
            Binding = new Binding("IsSelected")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1),
            },
            Value = true,
        };
        selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new Binding("Brush")));
        cellStyle.Triggers.Add(selTrigger);
        grid.CellStyle = cellStyle;
        grid.Columns.Add(MkCol("行号", "Index", 60));
        grid.Columns.Add(MkCol("匹配内容", "Content", 300));
        grid.Columns.Add(MkCol("模式", "Mode", 70));
        grid.Columns.Add(MkCol("命中次数", "MatchCount", 80));
        grid.Columns.Add(MkCol("目标行", "Target", 70));
        grid.ItemsSource = matches;

        // 无关闭按钮：用窗口右上角系统关闭键
        var dock = new DockPanel();
        dock.Children.Add(grid);   // 表格独占整行宽度

        Content = dock;
        if (matches.Count == 0)
            grid.ItemsSource = new[] { new { Index = (int)0, Content = "（没有匹配行）", Mode = "", MatchCount = (long)0, Target = (int)0, Brush = Brushes.Black } };
    }

    private static DataGridTextColumn MkCol(string header, string binding, double width) =>
        new() { Header = header, Binding = new Binding(binding), Width = width };
}
