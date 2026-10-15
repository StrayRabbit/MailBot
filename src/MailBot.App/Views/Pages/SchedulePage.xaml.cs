using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailBot.App.ViewModels;
using MailBot.Core.Models;

namespace MailBot.App.Views.Pages;

/// <summary>
/// 调度页：发送窗口/批参数/冷却 + 送达验证时间。DataContext 继承自 MainViewModel。
///
/// 校验策略：
/// - 数值 TextBox 拦非数字（PreviewTextInput + 空格）
/// - HH:mm TextBox 在 LostFocus 时校验格式，失败时边框变红 + Tooltip 提示
/// - 保存按钮由 MainViewModel.SaveConfig 调 AppConfig.Validate 兜底，失败时状态栏 + MessageBox
/// </summary>
public partial class SchedulePage : UserControl
{
    public SchedulePage()
    {
        InitializeComponent();
    }

    /// <summary>拦截非数字字符（数字文本框专用）。</summary>
    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // 只允许 ASCII 数字；负号、点、字母、空格都拦掉
        foreach (var c in e.Text)
        {
            if (c < '0' || c > '9')
            {
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>数值范围超出时不允许粘贴（粘贴路径不触发 PreviewTextInput）。</summary>
    private void NumericRange_Pasting(object sender, System.Windows.DataObjectPastingEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (!int.TryParse(tb.Text + e.DataObject.GetData(typeof(string)) as string,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            e.CancelCommand();
        }
    }

    /// <summary>HH:mm 失焦校验：解析失败时给 TextBox 边框描红 + Tooltip 提示。</summary>
    private void HHmm_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        var s = tb.Text?.Trim() ?? string.Empty;
        var ok = TimeSpan.TryParseExact(s, "hh\\:mm", CultureInfo.InvariantCulture, out _);
        SetErrorVisual(tb, ok, ok ? null : $"不是合法的 HH:mm 格式（当前: \"{s}\"，示例: 09:00）");
    }

    private void SetErrorVisual(System.Windows.Controls.TextBox tb, bool ok, string? error)
    {
        if (ok)
        {
            tb.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);
            tb.ClearValue(System.Windows.Controls.Border.BorderThicknessProperty);
            tb.ToolTip = null;
        }
        else
        {
            tb.BorderBrush = System.Windows.Media.Brushes.Red;
            tb.BorderThickness = new Thickness(1.5);
            tb.ToolTip = error;
        }
    }
}