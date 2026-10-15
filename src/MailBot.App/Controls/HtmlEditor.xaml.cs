using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace MailBot.App.Controls;

/// <summary>
/// 简易富文本编辑器：基于 WebBrowser + contenteditable，支持加粗 / 斜体 / 下划线 / 超链接命令。
/// 设计文档 7.2 节关键交互 4。
/// </summary>
public sealed partial class HtmlEditor : UserControl
{
    private string _editorHtmlPath = string.Empty;
    private DispatcherTimer? _syncTimer;

    /// <summary>抑制下一轮反向同步，避免 VM → WebBrowser 写入后又被同步回 VM 形成循环。</summary>
    private bool _suppressSync;

    public HtmlEditor()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 用户切换焦点（点保存按钮、切到其他控件）时立即同步，避免依赖定时轮询漏掉最近编辑
        Browser.LostFocus += Browser_LostFocus;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _editorHtmlPath = Path.Combine(Path.GetTempPath(), $"mailbot_editor_{GetHashCode()}.html");
        File.WriteAllText(_editorHtmlPath, EditorTemplate);
        Browser.Navigate(new Uri(_editorHtmlPath));
        // 文档就绪后把当前 HtmlBody 应用到 WebBrowser。
        // 处理"VM 构造早于 UI Loaded"的时序：LoadConfig 触发的 OnHtmlBodyChanged
        // 在 Browser.Document 还没就绪时会静默跳过，需要在 LoadCompleted 补一次。
        Browser.LoadCompleted += Browser_LoadCompleted;

        // 兜底定时轮询（500ms），应对 LostFocus 没触发的场景
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _syncTimer.Tick += SyncTick;
        _syncTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _syncTimer?.Stop();
        _syncTimer = null;
    }

    /// <summary>
    /// 显式触发一次 WebBrowser → HtmlBody 同步。
    /// 主窗口在「保存配置」Click 时先调此方法，保证 KV 存的是最新编辑内容。
    /// </summary>
    public void SyncNow() => SyncFromBrowser();

    private void Browser_LoadCompleted(object? sender, NavigationEventArgs e)
    {
        ApplyHtmlBody();
    }

    private void Browser_LostFocus(object? sender, RoutedEventArgs e)
    {
        SyncFromBrowser();
    }

    private void SyncTick(object? sender, EventArgs e) => SyncFromBrowser();

    /// <summary>WebBrowser → HtmlBody：把当前编辑内容同步到绑定源。</summary>
    private void SyncFromBrowser()
    {
        if (_suppressSync) return;
        if (Browser.Document is null) return;
        try
        {
            // 仅读 body 内容，不读 outerHTML（含 head/style 的额外噪音）
            var body = ((dynamic)Browser.Document).body;
            if (body is null) return;
            var current = (string)body.innerHTML;
            if (!string.Equals(current, HtmlBody, StringComparison.Ordinal))
            {
                _suppressSync = true;
                HtmlBody = current;
                _suppressSync = false;
            }
        }
        catch
        {
            // WebBrowser 正在 navigate / 文档未就绪时忽略
        }
    }

    /// <summary>HtmlBody → WebBrowser：把绑定源内容写到富文本编辑器。</summary>
    private void ApplyHtmlBody()
    {
        if (_suppressSync) return;
        if (Browser.Document is null) return;
        try
        {
            // 只写 body.innerHTML，不要写 documentElement.outerHTML——
            // 后者会替换整个 <html> 元素，破坏 head/body 结构，导致后续 body.innerHTML 失效。
            var body = ((dynamic)Browser.Document).body;
            if (body is null) return;
            body.innerHTML = HtmlBody ?? string.Empty;
        }
        catch
        {
            // WebBrowser 尚未加载完成时忽略（LoadCompleted 会再调一次）
        }
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => Exec("Bold");
    private void Italic_Click(object sender, RoutedEventArgs e) => Exec("Italic");
    private void Underline_Click(object sender, RoutedEventArgs e) => Exec("Underline");

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.Document is null) return;
        var sel = (dynamic)((dynamic)Browser.Document).selection;
        if (sel.type == "Text")
        {
            var dialog = new LinkDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Url))
            {
                ((dynamic)Browser.Document).execCommand("CreateLink", false, dialog.Url);
            }
        }
        else
        {
            MessageBoxEx.Show("请先选中要添加链接的文字");
        }
    }

    private void Exec(string command)
    {
        if (Browser.Document is null) return;
        ((dynamic)Browser.Document).documentElement.focus();
        ((dynamic)Browser.Document).execCommand(command, false, null);
    }

    /// <summary>HTML 正文（双向绑定到 VM）。</summary>
    public static readonly DependencyProperty HtmlBodyProperty =
        DependencyProperty.Register(nameof(HtmlBody), typeof(string), typeof(HtmlEditor),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnHtmlBodyChanged));

    public string HtmlBody
    {
        get => (string)GetValue(HtmlBodyProperty);
        set => SetValue(HtmlBodyProperty, value);
    }

    private static void OnHtmlBodyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (HtmlEditor)d;
        // 来自反向同步（WebBrowser → VM）的更新，不再写回 WebBrowser，避免循环。
        if (ctrl._suppressSync) return;
        ctrl.ApplyHtmlBody();
    }

    private const string EditorTemplate = """
        <!doctype html><html><head><meta charset="utf-8">
        <style>body { font-family: 'Segoe UI', sans-serif; padding: 12px; }</style>
        </head><body contenteditable="true"><p></p></body></html>
        """;
}