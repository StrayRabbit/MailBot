using System.Windows;

namespace MailBot.App.Controls;

/// <summary>
/// 居中显示 MessageBox 的辅助类。
/// WPF 的 MessageBox.Show(text) 无 owner 重载会按屏幕左上角定位，
/// 不在主窗口中央。这里封装一组重载，自动拿当前活动主窗口做 owner，
/// 保证弹窗跟随主窗口居中。
/// </summary>
public static class MessageBoxEx
{
    private static Window? ResolveOwner(Window? hint)
    {
        // 优先级：调用方传入 → 当前活动窗口 → Application.MainWindow
        if (hint is not null) return hint;
        if (Application.Current?.MainWindow is { IsLoaded: true } main) return main;
        return null;
    }

    public static void Show(string text)
        => Show(text, "", MessageBoxButton.OK, MessageBoxImage.None, owner: null);

    public static void Show(string text, string title)
        => Show(text, title, MessageBoxButton.OK, MessageBoxImage.None, owner: null);

    public static void Show(string text, string title, MessageBoxButton button)
        => Show(text, title, button, MessageBoxImage.None, owner: null);

    public static void Show(string text, string title, MessageBoxButton button, MessageBoxImage icon)
        => Show(text, title, button, icon, owner: null);

    public static void Show(Window owner, string text, string title, MessageBoxButton button, MessageBoxImage icon)
        => Show(text, title, button, icon, owner: owner);

    public static void Show(string text, string title, MessageBoxButton button, MessageBoxImage icon, Window? owner)
    {
        // 无 owner 时 WPF 会把 MessageBox 当成独立顶层窗口，不跟随主窗口；
        // 传 null 让 WPF 自行定位(默认左上角)。这里强制兜底到 MainWindow。
        var resolved = ResolveOwner(owner) ?? Application.Current?.MainWindow;
        MessageBox.Show(resolved, text, title, button, icon);
    }

    public static MessageBoxResult Show(string text, string title, MessageBoxButton button, Window? owner)
    {
        var resolved = ResolveOwner(owner) ?? Application.Current?.MainWindow;
        return MessageBox.Show(resolved, text, title, button);
    }

    public static MessageBoxResult ShowYesNo(string text, string title, Window? owner = null)
    {
        var resolved = ResolveOwner(owner) ?? Application.Current?.MainWindow;
        return MessageBox.Show(resolved, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
    }
}