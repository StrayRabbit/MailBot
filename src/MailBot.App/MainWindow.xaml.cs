using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MailBot.App.ViewModels;
using MailBot.App.Views.Pages;
using MailBot.Core.Senders;
using MailBot.Core.Sending;

namespace MailBot.App;

public partial class MainWindow : Window
{
    /// <summary>当前 DataContext 对应的 VM；切换页面前后通过它同步 SelectedPageIndex。</summary>
    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>侧栏 5 个页面的实例，索引 = NavItem.Index。AccountsPage 注入 SmtpDispatcher 以支持 SMTP 测试连接。</summary>
    private readonly UserControl[] _pages;

    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();

        // Pages 在 DataContext 设置之前创建（DataContext 由 App.xaml.cs 在构造后注入）。
        var app = (App)Application.Current;
        var dispatcher = new SmtpDispatcher();
        var senderPool = app.SenderPool
            ?? throw new InvalidOperationException("App 尚未初始化 SenderPool");
        var verifierRepo = app.VerifierRepo
            ?? throw new InvalidOperationException("App 尚未初始化 VerifierRepo");
        _pages = new UserControl[]
        {
            new MonitorPage(),
            new AccountsPage(dispatcher),
            new RecipientsPage(),
            new ComposePage(dispatcher, senderPool, verifierRepo),
            new SchedulePage(),
            new StatsPage(),
        };

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            // DataContext 在 Loaded 时一定就绪；若 Loaded 之前已就绪，DataContextChanged 已处理过。
            if (PageHost.Content is null && Vm is not null)
            {
                UpdatePage(Vm.SelectedPageIndex);
            }
        };
    }

    /// <summary>
    /// 通过 WM_SETICON 把 HICON 直接写到窗口，绕过 WPF BAML 转换器对 ICO 的解码
    /// （之前用 Window.Icon="app.ico" 在 BAML 解析阶段就抛 XamlParseException）。
    /// 用 LoadImage 直接从文件加载 HICON（不经过 System.Drawing.Icon 的句柄包装），
    /// Windows 内部会 CopyImage 复制 HICON，不必 DestroyIcon 原句柄。
    /// </summary>
    private void LoadWindowIcon()
    {
        SourceInitialized += (_, _) =>
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (!File.Exists(path)) return;
                var hIcon = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (hIcon == IntPtr.Zero) return;

                var hwnd = new WindowInteropHelper(this).Handle;
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIcon);
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIcon);
            }
            catch
            {
                // 加载失败时窗口保留默认图标
            }
        };
    }

    /// <summary>
    /// 关闭按钮：弹出两选项。
    /// - 是 = 最小化到系统托盘（窗口隐藏，托盘常驻）
    /// - 否 = 退出整个应用
    /// 托盘菜单退出时 IsExplicitShutdown 已为 true，跳过弹窗直接关闭。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.IsExplicitShutdown)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        var result = MessageBox.Show(
            this,
            "关闭 MailBot 时希望如何处理？\r\n\r\n选 [是] 最小化到托盘继续在后台运行；选 [否] 退出应用。",
            "关闭 MailBot",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (result == MessageBoxResult.Yes)
        {
            Hide();
        }
        else
        {
            app.ExitApp();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.PropertyChanged -= Vm_PropertyChanged;
        }
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
            UpdatePage(newVm.SelectedPageIndex);
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPageIndex) && sender is MainViewModel vm)
        {
            UpdatePage(vm.SelectedPageIndex);
        }
    }

    private void UpdatePage(int index)
    {
        if (index < 0 || index >= _pages.Length) return;
        if (PageHost.Content == _pages[index]) return;
        PageHost.Content = _pages[index];
        // 切到监控页（index 0）时主动刷新统计与当前展示账号的信息，
        // 避免账号页改了"今日已发"或"配额"后监控页不更新。
        if (index == 0)
        {
            Vm?.Monitor.RefreshStats();
            Vm?.Monitor.RefreshCurrentSenderInfo();
        }
        // 切到统计页（index 5）时刷新汇总指标与柱状图，
        // 让历史日期批次完成、回到此页能立刻看到最新数据。
        if (index == 5)
        {
            Vm?.Stats.RefreshAll();
        }
    }
}