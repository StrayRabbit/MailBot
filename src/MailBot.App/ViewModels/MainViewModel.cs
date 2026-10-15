using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBot.App.Logging;
using MailBot.Core.Logging;
using MailBot.Core.Mail;
using MailBot.Core.Models;
using MailBot.Core.Sending;
using MailBot.Core.Storage;

namespace MailBot.App.ViewModels;

/// <summary>侧栏导航项：标签 + Path 图标数据 + 索引。Page 由 MainWindow 按索引注入到 ContentControl。</summary>
public sealed record NavItem(string Label, string IconData, int Index);

/// <summary>
/// 主窗口视图模型：串联各子 VM，持有 MailTemplate，调度 SendEngine 的启停；侧栏导航状态。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SendEngine _engine;
    private readonly ConfigRepository _configRepo;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _subject = string.Empty;
    [ObservableProperty] private string _htmlBody = "<p>请在此处编辑邮件正文...</p>";

    [ObservableProperty] private string _windowStart = "09:00";
    [ObservableProperty] private string _windowEnd = "18:00";
    [ObservableProperty] private int _sendIntervalSeconds = 30;
    [ObservableProperty] private int _batchSize = 20;
    [ObservableProperty] private int _maxRetries = 1;
    [ObservableProperty] private int _coolingFailures = 3;
    [ObservableProperty] private int _coolingMinutes = 60;

    // 送达验证相关时间配置（持久化到 app_config，不进入 AppConfig 强类型模型以保持向后兼容）
    [ObservableProperty] private int _verificationPollIntervalSeconds = 180;
    [ObservableProperty] private int _verificationRecheckIntervalSeconds = 180;
    [ObservableProperty] private int _verificationMaxPollAttempts = 2;
    [ObservableProperty] private int _verificationLookbackHours = 10;
    [ObservableProperty] private int _verificationUnconfirmedCooldownMinutes = 60;
    [ObservableProperty] private int _sendHoldOnUnverifiedSeconds = 300;

    [ObservableProperty] private string _statusBar = "就绪";
    [ObservableProperty] private bool _isEngineRunning;
    [ObservableProperty] private bool _isEnginePaused;

    /// <summary>侧栏导航项。IconData 为 SVG path 的 d 属性（与现有工具栏按钮风格一致）。</summary>
    public ObservableCollection<NavItem> Pages { get; } = new()
    {
        // 监控：心电脉冲 + 右侧闪烁点，表达"实时活动"。
        new NavItem("监控",  "M2,12 L6,12 L8,6 L11,18 L14,9 L17,12 L22,12", 0),
        new NavItem("账号",  "M12,3 a4,4 0 1,0 0.001,0 Z M4,20 v-1 a6,6 0 0,1 6,-6 h4 a6,6 0 0,1 6,6 v1 H4 Z", 1),
        new NavItem("收件人","M16,11 a3,3 0 1,0 0.001,0 Z M8,11 a3,3 0 1,0 0.001,0 Z M2,20 v-1 a5,5 0 0,1 5,-5 h2 a5,5 0 0,1 5,5 v1 H2 Z M14,20 v-1 a5,5 0 0,1 5,-5 h2 a5,5 0 0,1 5,5 v1 H14 Z", 2),
        new NavItem("邮件",  "M3,5 L21,5 L21,19 L3,19 Z M3,5 L12,13 L21,5", 3),
        new NavItem("调度",  "M12,7 v5 l4,2 M12,21 a9,9 0 1,0 0.001,0 Z", 4),
        // 统计：横轴 + 三根柱 + 顶部一根趋势折线，表达"汇总数据 / 趋势"。
        new NavItem("统计",  "M2,20 L22,20 M5,20 L5,16 L8,16 L8,20 Z M11,20 L11,11 L14,11 L14,20 Z M17,20 L17,7 L20,7 L20,20 Z M5,8 L11,5 L17,2", 5),
    };

    /// <summary>当前选中的导航项（默认 0 = 监控首页）。</summary>
    [ObservableProperty] private int _selectedPageIndex = 0;

    public SendersViewModel Senders { get; }
    public RecipientsViewModel Recipients { get; }
    public MonitorViewModel Monitor { get; }
    public VerifiersViewModel Verifiers { get; }
    public StatsViewModel Stats { get; }

    public ObservableCollection<string> AttachmentPaths { get; } = new();

    public MainViewModel(
        SendEngine engine,
        SendersViewModel senders,
        RecipientsViewModel recipients,
        MonitorViewModel monitor,
        VerifiersViewModel verifiers,
        StatsViewModel stats,
        ConfigRepository configRepo)
    {
        _engine = engine;
        _configRepo = configRepo;
        Senders = senders;
        Recipients = recipients;
        Monitor = monitor;
        Verifiers = verifiers;
        Stats = stats;

        LoadConfig();

        _engine.BatchCompleted += (_, report) =>
            Dispatch(() =>
            {
                Monitor.OnBatchCompleted(report);
                Monitor.RefreshSenderInfo(_engine.CurrentSenderEmail ?? string.Empty);
                Stats.RefreshAll();
                StatusBar = $"最后一批：{report.SenderEmail}，成功 {report.SuccessCount}，失败 {report.FailedCount}";
            });
        _engine.StateChanged += (_, state) =>
            Dispatch(() =>
            {
                Monitor.OnEngineStateChanged(state);
                IsEngineRunning = state == EngineState.Running;
                IsEnginePaused = state == EngineState.Paused;
                // 引擎状态变化时刷新 Start/Stop 按钮 CanExecute。
                // 此处调用安全：RelayCommand 属性已在构造时生成完毕。
                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
            });
        // 不再订阅 _engine.Logged——精简后的日志由 LogBus 走 ILogger 通道送达。
        // Logged 事件仍保留并由 SendEngine 触发，只用于写日志文件（LogFileWriter）。
        LogBus.Instance.LogEmitted += (_, entry) =>
            Dispatch(() => Monitor.AppendLog(entry));
        _engine.NextBatchHintChanged += (_, hint) =>
            Dispatch(() => Monitor.UpdateNextBatchHint(hint));
    }

    private void LoadConfig()
    {
        var cfg = _configRepo.Load();
        WindowStart = cfg.WindowStart;
        WindowEnd = cfg.WindowEnd;
        SendIntervalSeconds = cfg.SendIntervalSeconds;
        BatchSize = cfg.BatchSize;
        MaxRetries = cfg.MaxRetries;
        CoolingFailures = cfg.CoolingFailures;
        CoolingMinutes = cfg.CoolingMinutes;

        // 送达验证时间配置（通过 VerifierConfig.Load，不污染 AppConfig）
        var v = MailBot.Core.Verification.VerifierConfig.Load(_configRepo);
        VerificationPollIntervalSeconds = v.PollIntervalSeconds;
        VerificationRecheckIntervalSeconds = v.RecheckIntervalSeconds;
        VerificationMaxPollAttempts = v.MaxPollAttempts;
        VerificationLookbackHours = v.LookbackHours;
        VerificationUnconfirmedCooldownMinutes = v.UnconfirmedCooldownMinutes;
        SendHoldOnUnverifiedSeconds = v.SendHoldOnUnverifiedSeconds;

        // 邮件内容：复用 app_config KV，避免新建表/新仓储
        Subject = _configRepo.Get("mail_subject") ?? string.Empty;
        HtmlBody = _configRepo.Get("mail_html_body") ?? "<p>请在此处编辑邮件正文...</p>";
        var pathsJson = _configRepo.Get("mail_attachment_paths");
        if (!string.IsNullOrEmpty(pathsJson))
        {
            try
            {
                var paths = JsonSerializer.Deserialize<List<string>>(pathsJson);
                if (paths is not null)
                {
                    AttachmentPaths.Clear();
                    foreach (var p in paths) AttachmentPaths.Add(p);
                }
            }
            catch
            {
                // JSON 损坏时忽略，保留空列表
            }
        }
    }

    [RelayCommand]
    public void SaveConfig()
    {
        var cfg = new AppConfig
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            SendIntervalSeconds = SendIntervalSeconds,
            BatchSize = BatchSize,
            MaxRetries = MaxRetries,
            CoolingFailures = CoolingFailures,
            CoolingMinutes = CoolingMinutes,
        };
        if (!cfg.Validate(out var errors))
        {
            // 校验失败：不写库（避免非法值落库），把错误汇总到状态栏。
            StatusBar = "配置未保存：" + string.Join("；", errors);
            return;
        }
        _configRepo.Save(cfg);
        // 送达验证时间配置直接 KV 落库（不经过 AppConfig）
        _configRepo.Set("verification_poll_interval_seconds", VerificationPollIntervalSeconds.ToString());
        _configRepo.Set("verification_recheck_interval_seconds", VerificationRecheckIntervalSeconds.ToString());
        _configRepo.Set("verification_max_poll_attempts", VerificationMaxPollAttempts.ToString());
        _configRepo.Set("verification_lookback_hours", VerificationLookbackHours.ToString());
        _configRepo.Set("verification_unconfirmed_cooldown_minutes", VerificationUnconfirmedCooldownMinutes.ToString());
        _configRepo.Set("send_hold_on_unverified_seconds", SendHoldOnUnverifiedSeconds.ToString());
        // 邮件内容一并持久化（KV）
        _configRepo.Set("mail_subject", Subject);
        _configRepo.Set("mail_html_body", HtmlBody);
        _configRepo.Set("mail_attachment_paths", JsonSerializer.Serialize(AttachmentPaths.ToList()));
        StatusBar = "配置已保存";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (_cts is { IsCancellationRequested: false }) return;
        _cts = new CancellationTokenSource();
        IsEngineRunning = true;
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        var template = new MailTemplate
        {
            Subject = Subject,
            HtmlBody = HtmlBody,
            AttachmentPaths = AttachmentPaths.ToList(),
        };
        if (!template.IsValid(out var error))
        {
            StatusBar = $"无法启动：{error}";
            CleanupCts();
            IsEngineRunning = false;
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            return;
        }
        // 校验持久化配置（防御性：DB 旧值或手动改库引入的越界值）
        var persisted = _configRepo.Load();
        if (!persisted.Validate(out var cfgErrors))
        {
            StatusBar = $"无法启动：配置非法 — {string.Join("；", cfgErrors)}";
            CleanupCts();
            IsEngineRunning = false;
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            return;
        }
        SaveConfig();
        StatusBar = "运行中";
        try
        {
            await _engine.RunAsync(template, persisted, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusBar = "已停止";
        }
        catch (Exception ex)
        {
            // 引擎异常不应静默吞掉 — 暴露给 UI
            StatusBar = $"引擎异常：{ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[engine exception] {ex}");
        }
        finally
        {
            CleanupCts();
            IsEngineRunning = false;
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStart() => !IsEngineRunning;

    private void CleanupCts()
    {
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    [RelayCommand]
    public void Pause()
    {
        _engine.Pause();
        StatusBar = "已暂停";
    }

    [RelayCommand]
    public void Resume()
    {
        _engine.Resume();
        StatusBar = "运行中";
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    public void Stop()
    {
        _cts?.Cancel();
        StatusBar = "正在停止...";
    }

    private bool CanStop() => IsEngineRunning;

    [RelayCommand]
    public void AddAttachment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            // 复制到 MailBot 自己的 attachments 目录，存副本路径，避免原文件被删后附件失效
            var stored = MailBot.Core.Storage.AttachmentStorage.CopyToStorage(path);
            AttachmentPaths.Add(stored);
        }
        catch
        {
            // 复制失败（权限/源被锁）回退到原路径，MailBuilder 会 File.Exists 跳过失效文件
            AttachmentPaths.Add(path);
        }
    }

    [RelayCommand]
    public void RemoveAttachment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        AttachmentPaths.Remove(path);
    }

    private static void Dispatch(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is null || app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.Invoke(action);
        }
    }
}