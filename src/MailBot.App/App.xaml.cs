using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using MailBot.App.Logging;
using MailBot.App.ViewModels;
using MailBot.Core.Logging;
using MailBot.Core.Sending;
using MailBot.Core.Senders;
using MailBot.Core.Storage;
using MailBot.Core.Verification;

namespace MailBot.App;

/// <summary>
/// App 入口：组合根，初始化数据库 + 仓储 + 引擎，启动主窗口。
/// 设计文档 6.1 节定义启动流程。
/// </summary>
public partial class App : Application
{
    // 单实例：命名 Mutex 标记是否已有进程在跑；命名管道用于把"显示窗口"指令从新进程传给旧进程。
    // 名字带版本号：将来 schema 变到不兼容时方便 bump。
    private const string SingleInstanceMutexName = "MailBot.SingleInstance.v1";
    private const string ShowCommandPipeName = "MailBot.ShowCommand.v1";

    public Database? Db { get; private set; }
    public MainViewModel? MainVm { get; private set; }
    /// <summary>发件账号池：MainWindow 构造期间访问不到局部变量,这里暴露给 ComposePage 用(测试发送)。</summary>
    public SenderPool? SenderPool { get; private set; }
    /// <summary>验证邮箱仓储：ComposePage"测试发送"取首个启用的验证邮箱用作收件人。</summary>
    public VerifierRepository? VerifierRepo { get; private set; }

    private MainWindow? _mainWindow;
    private NotifyIcon? _tray;
    private bool _isExplicitShutdown;
    private VerificationWorker? _verificationWorker;

    // 单实例相关
    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeServerCts;

    /// <summary>主窗口：托盘菜单/双击恢复时使用。</summary>
    public new MainWindow? MainWindow => _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0. 单实例检测：已有进程在跑则通过管道通知其显示主窗口，然后退出当前进程。
        //    必须放在 DB / 仓储初始化之前，避免两个进程同时读写同一个 SQLite db。
        if (!AcquireSingleInstance())
        {
            TrySendShowToExistingInstance();
            // 用 Shutdown(0) 走 OnExit，避免触发窗口 Closing 的"是否最小化"弹窗。
            Shutdown(0);
            return;
        }
        StartShowCommandServer();

        try
        {

        // 1. 初始化数据库（路径：命令行 --db > 环境变量 MAILBOT_DB_PATH > 配置文件 > 默认 %AppData%\MailBot\mailbot.db）
        Db = new Database(Database.ResolveDbPath(e.Args));
        Db.EnsureSchema();

        // 2. 仓储
        var senderRepo = new SenderRepository(Db);
        var recipientRepo = new RecipientRepository(Db);
        var batchStatsRepo = new BatchStatsRepository(Db);
        var configRepo = new ConfigRepository(Db);
        var verifierRepo = new VerifierRepository(Db);
        VerifierRepo = verifierRepo;

        // 3. 业务模块
        var senderPool = new SenderPool(senderRepo);
        SenderPool = senderPool;
        var dispatcher = new SmtpDispatcher();
        var verifierConfig = VerifierConfig.Load(configRepo);
        verifierConfig.Validate();
        Action<string> log = msg =>
        {
            System.Diagnostics.Debug.WriteLine($"[verify] {msg}");
            // 把 worker 日志也推到 UI 日志面板
            var app = System.Windows.Application.Current;
            app?.Dispatcher.BeginInvoke(() =>
                MainVm?.Monitor.AppendLog($"[{DateTime.Now:HH:mm:ss}] {msg}"));
        };
        var imapPoller = new ImapPoller(log);
        var verificationWorker = new VerificationWorker(
            verifierRepo, recipientRepo, senderRepo, senderPool, verifierConfig, imapPoller, log,
            resendHandler: null,  // 延后注入：engine 建好后调用 AttachResendHandler
            uiLogger: LogBus.Instance);
        _verificationWorker = verificationWorker;

        // worker 两类状态推进事件 → 触发 UI 实时刷新 + 日志
        verificationWorker.BatchVerified += (_, batch) => OnBatchVerified(batch.BatchId, "IMAP 命中");
        verificationWorker.BatchUnconfirmed += (_, batch) => OnBatchUnconfirmed(batch.BatchId);

        // 单批执行器 + 失败分类器(从 SendEngine 抽出,各自单一职责、可独立测试)
        var batchExecutor = new BatchExecutor(
            dispatcher, recipientRepo, senderRepo, verifierRepo, verifierConfig,
            uiLogger: LogBus.Instance);
        var failureClassifier = new SenderFailureClassifier();

        var engine = new SendEngine(
            Db, senderRepo, recipientRepo, senderPool, dispatcher,
            verifierRepo, verificationWorker, verifierConfig, batchExecutor, failureClassifier,
            uiLogger: LogBus.Instance);

        // 把 engine 作为 IResendHandler 注入 worker（解决 worker/engine 构造顺序的循环依赖）
        verificationWorker.AttachResendHandler(engine);

        // 4. ViewModels
        MainVm = new MainViewModel(
            engine,
            new SendersViewModel(senderRepo),
            new RecipientsViewModel(recipientRepo),
            new MonitorViewModel(recipientRepo, senderRepo, batchStatsRepo),
            new VerifiersViewModel(verifierRepo, log),
            new StatsViewModel(batchStatsRepo),
            configRepo);

        // 5. 主窗口
        _mainWindow = new MainWindow { DataContext = MainVm };
        MainVm.Monitor.RefreshStats();
        _mainWindow.Show();

        // 6. 启动验证 worker（在主窗口已显示后启动，避免冷启动 I/O 阻塞 UI）
        await verificationWorker.StartAsync(CancellationToken.None);

        // 7. 系统托盘
        InitTray();

        }
        catch (Exception ex)
        {
            // 启动期任何异常都不应让进程直接挂掉 — 至少展示错误对话框并优雅退出。
            // 常见原因：DB 路径无写权限、schema 迁移冲突、DPAPI scope 错误。
            System.Diagnostics.Debug.WriteLine($"[startup error] {ex}");
            try
            {
                System.Windows.MessageBox.Show(
                    $"启动失败：{ex.GetType().Name}\n{ex.Message}\n\n请检查数据库路径、文件权限或重置 app_config。",
                    "MailBot 启动错误",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch
            {
                // MessageBox 在极早期阶段（Dispatcher 不可用）会失败 — 静默兜底。
            }
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Application 退出阶段托盘 native handle 可能已被系统回收，Dispose 抛 E_HANDLE，
        // 这里吞掉以保证退出流程不被中断。
        try
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
        catch
        {
            // ignored
        }
        _tray = null;
        try { _verificationWorker?.Dispose(); }
        catch { /* worker 已在进程退出阶段被强制终结 */ }

        // 释放单实例资源：管道 server 停止监听 + Mutex 释放。
        // 顺序必须先 stop server 再 release mutex，否则 server 还在监听时 mutex 已释放
        // 会出现"拿到 mutex 的进程还没接管 server、但 server 已停"的窗口期。
        try { _pipeServerCts?.Cancel(); }
        catch { /* ignored */ }
        try
        {
            if (_singleInstanceMutex is not null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
        }
        catch
        {
            // 当前进程没拿到 mutex（Application Exit 走 Shutdown(0) 短路路径）时 ReleaseMutex
            // 会抛 — 吞掉以保证退出流程不被中断。
        }
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    /// <summary>
    /// worker 推进到 verified（IMAP 命中）→ UI 线程刷新 Monitor 四桶统计。
    /// </summary>
    private void OnBatchVerified(string batchId, string reason)
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            MainVm?.Monitor.RefreshStats();
            // 进度条依赖"当前账号已发"，IMAP 命中后 sent_today 变了，必须刷新。
            MainVm?.Monitor.RefreshCurrentSenderInfo();
            MainVm?.Monitor.AppendLog($"[{DateTime.Now:HH:mm:ss}] [verified] batch={batchId} ({reason})");
            if (MainVm is not null)
            {
                MainVm.StatusBar = $"批次 {batchId} 已验证 ({reason})";
            }
        });
    }

    /// <summary>
    /// worker 推进到 unconfirmed → UI 线程刷新 + 状态栏提示。
    /// </summary>
    private void OnBatchUnconfirmed(string batchId)
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            MainVm?.Monitor.RefreshStats();
            MainVm?.Monitor.RefreshCurrentSenderInfo();
            if (MainVm is not null)
            {
                MainVm.StatusBar = $"批次 {batchId} 验证失败，账号已冷却";
            }
        });
    }

    private void InitTray()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            Icon trayIcon;
            try
            {
                trayIcon = new Icon(iconPath);
            }
            catch
            {
                // ico 加载失败时退回到系统默认 application 图标，确保托盘至少可见
                trayIcon = SystemIcons.Application;
            }

            var menu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("显示主窗口");
            showItem.Click += (_, _) => ShowMainWindow();
            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => ExitApp();
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray = new NotifyIcon
            {
                Icon = trayIcon,
                Visible = true,
                Text = "MailBot 批量邮件发送",
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => ShowMainWindow();
        }
        catch (Exception ex)
        {
            // 托盘初始化失败不能阻塞主程序；常见于 WPF 上下文里 WinForms 句柄还没就绪。
            System.Diagnostics.Debug.WriteLine($"[tray init failed] {ex}");
            _tray?.Dispose();
            _tray = null;
        }
    }

    /// <summary>从托盘恢复主窗口。</summary>
    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    /// <summary>真正的退出：标记 _isExplicitShutdown 后 Shutdown，托盘会随之释放。</summary>
    public void ExitApp()
    {
        _isExplicitShutdown = true;
        Shutdown();
    }

    /// <summary>MainWindow.OnClosing 调用，避免关闭被误当成"最小化到托盘"。</summary>
    public bool IsExplicitShutdown => _isExplicitShutdown;

    /// <summary>
    /// 抢占单实例 Mutex。返回 true 表示当前进程是唯一实例；false 表示已有实例在跑。
    /// 必须放在任何 DB / 文件 IO 之前，避免多进程并发踩同一份 SQLite db。
    /// </summary>
    private bool AcquireSingleInstance()
    {
        // 快速路径：mutex 不存在就当前进程创建并持有。
        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        if (createdNew) return true;

        // 已存在：先释放本次构造时的引用（initiallyOwned=true 但 mutex 已存在时我们并未真正拿到所有权），
        // 再用 OpenExisting + WaitOne(0) 探测；兼容 abandoned mutex（旧实例崩溃）情况。
        try { _singleInstanceMutex.Dispose(); }
        catch { /* ignored */ }
        _singleInstanceMutex = null;

        try
        {
            var existing = Mutex.OpenExisting(SingleInstanceMutexName);
            try
            {
                // WaitOne(0) 不阻塞：拿到所有权立即返回 true，否则 false。
                if (existing.WaitOne(0))
                {
                    _singleInstanceMutex = existing;
                    return true;
                }
                existing.Dispose();
                return false;
            }
            catch (AbandonedMutexException)
            {
                // 旧实例崩溃遗留：当前进程接管 mutex，旧实例的进程/窗口一定已死，
                // 直接继续启动当前实例即可。
                _singleInstanceMutex = existing;
                return true;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // mutex 已被释放：当作没人持有，当前进程启动。
            return true;
        }
        catch
        {
            // 其他异常（权限不足等）：保守当作已有实例在跑。
            return false;
        }
    }

    /// <summary>
    /// 启动管道 server 后台循环：监听"SHOW"指令并 marshal 到 UI 线程显示主窗口。
    /// 单连接处理一次后立刻重启 NamedPipeServerStream，让新一次启动能继续连上。
    /// </summary>
    private void StartShowCommandServer()
    {
        _pipeServerCts = new CancellationTokenSource();
        var ct = _pipeServerCts.Token;
        var thread = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        ShowCommandPipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    // WaitForConnectionAsync 在取消时会抛 OperationCanceledException，循环退出。
                    var waitTask = server.WaitForConnectionAsync(ct);
                    waitTask.GetAwaiter().GetResult();
                    using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true))
                    {
                        var cmd = reader.ReadToEnd();
                        if (cmd.Trim().Equals("SHOW", StringComparison.OrdinalIgnoreCase))
                        {
                            Dispatcher.BeginInvoke(() => ShowMainWindow());
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    return;
                }
                catch
                {
                    // 偶发的管道异常（命名解析失败、客户端异常断开）：吞掉，继续监听。
                }
                finally
                {
                    try { server?.Dispose(); }
                    catch { /* ignored */ }
                }
            }
        }) { IsBackground = true, Name = "MailBot.ShowCommandServer" };
        thread.Start();
    }

    /// <summary>
    /// 通过命名管道通知已存在的实例显示主窗口。本进程随即退出。
    /// 用短超时（500ms）：旧实例若已僵死（比如被卡 IO），新实例不应该无限等。
    /// </summary>
    private static void TrySendShowToExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ShowCommandPipeName, PipeDirection.Out);
            client.Connect(timeout: 500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write("SHOW");
            writer.Flush();
        }
        catch
        {
            // 旧实例没有监听 server / 已僵死 — 静默吞掉，新实例退出即可。
        }
    }
}