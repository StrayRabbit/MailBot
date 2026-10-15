using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailBot.App.Logging;
using MailBot.Core;
using MailBot.Core.Logging;
using MailBot.Core.Models;
using MailBot.Core.Sending;
using MailBot.Core.Storage;

namespace MailBot.App.ViewModels;

/// <summary>
/// 实时监控面板：总数 / 已发 / 失败 / 当前账号配额 / 日志流。
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly RecipientRepository _recipientRepo;
    private readonly SenderRepository _senderRepo;
    private readonly BatchStatsRepository _batchStatsRepo;

    [ObservableProperty] private int _totalRecipients;
    [ObservableProperty] private int _sentRecipients;
    [ObservableProperty] private int _failedRecipients;
    [ObservableProperty] private int _pendingRecipients;
    [ObservableProperty] private int _unverifiedRecipients;
    [ObservableProperty] private int _verifiedRecipients;
    [ObservableProperty] private int _unconfirmedRecipients;
    [ObservableProperty] private string _currentSenderEmail = string.Empty;
    [ObservableProperty] private int _currentSenderQuota;
    [ObservableProperty] private int _currentSenderSent;
    [ObservableProperty] private string _cooldownInfo = "无";
    [ObservableProperty] private string _lastBatchSize = "0";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _engineStateText = "空闲";
    [ObservableProperty] private int _todaySentTotal;
    [ObservableProperty] private string _nextBatchHint = "—";

    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<Recipient> RecentRecipients { get; } = new();

    public MonitorViewModel(RecipientRepository recipientRepo, SenderRepository senderRepo, BatchStatsRepository batchStatsRepo)
    {
        _recipientRepo = recipientRepo;
        _senderRepo = senderRepo;
        _batchStatsRepo = batchStatsRepo;
        RefreshStats();
    }

    public void RefreshStats()
    {
        TotalRecipients = _recipientRepo.Count();
        SentRecipients = _recipientRepo.CountByStatus("sent");
        FailedRecipients = _recipientRepo.CountByStatus("failed");
        PendingRecipients = _recipientRepo.CountByStatus("pending");
        UnverifiedRecipients = _recipientRepo.CountByStatus("unverified");
        VerifiedRecipients = _recipientRepo.CountByStatus("verified");
        UnconfirmedRecipients = _recipientRepo.CountByStatus("unconfirmed");
        TodaySentTotal = _batchStatsRepo.CountTodayVerified();
        RefreshRecent();
        // 进度条跟随"当前账号已发/配额"，由 RefreshSenderInfo 维护。
        // RefreshStats 不主动算 ProgressPercent，避免与 RefreshSenderInfo 互相覆盖。
        // 但启动路径只调 RefreshStats 不调 RefreshSenderInfo，需要兜底显示第一个账号的进度。
        if (string.IsNullOrEmpty(CurrentSenderEmail))
        {
            var first = _senderRepo.GetAll().FirstOrDefault();
            if (first is not null) RefreshSenderInfo(first.Email);
        }
    }

    public void RefreshSenderInfo(string senderEmail)
    {
        CurrentSenderEmail = senderEmail;
        var sender = _senderRepo.GetAll().FirstOrDefault(s => s.Email == senderEmail);
        if (sender is not null)
        {
            CurrentSenderQuota = sender.DailyQuota;
            CurrentSenderSent = sender.SentToday;
            // 进度条 = 当前账号"今日已发 / 今日配额"，表示本账号今日配额剩余进度。
            // Quota=0 时进度条置 0（避免除零；账号页禁用配额时显示空进度条）。
            ProgressPercent = sender.DailyQuota > 0
                ? (double)sender.SentToday / sender.DailyQuota * 100.0
                : 0;
            CooldownInfo = sender.Status switch
            {
                "banned" => "封禁中",
                "cooling" when !string.IsNullOrEmpty(sender.CooldownUntil)
                    => $"冷却至 {DateTime.Parse(sender.CooldownUntil):HH:mm}",
                _ => "无",
            };
        }
        else
        {
            CurrentSenderQuota = 0;
            CurrentSenderSent = 0;
            ProgressPercent = 0;
            CooldownInfo = "无";
        }
    }

    /// <summary>
    /// 重新从库拉取当前展示账号的配额/已发/冷却信息。由"切到监控页"路径调用，
    /// 让用户改完账号页数据后回到监控页能立刻看到最新值（顶部"今日已发"、
    /// 监控页"账号配额"行展示的就是 CurrentSender* 系列字段）。
    /// </summary>
    public void RefreshCurrentSenderInfo()
    {
        if (string.IsNullOrEmpty(CurrentSenderEmail)) return;
        RefreshSenderInfo(CurrentSenderEmail);
    }

    private bool _uiLogFullWarned;
    private int _droppedSinceFull;

    public void AppendLog(string line)
    {
        // 1) 持久化到日志文件（按天分文件，位于 <db 同根>/logs/）
        MailBot.Core.Storage.LogFileWriter.WriteLine(line);

        // 2) UI 内存日志：插入到最前面（最新消息优先）。
        //    兼容老调用点：把纯文本当作 Info 级别入列。
        //    容量上限 5000 行，超过后停止向 UI 添加，但仍持续写入文件。
        const int MaxUiLines = 5000;
        if (Logs.Count >= MaxUiLines)
        {
            if (!_uiLogFullWarned)
            {
                _uiLogFullWarned = true;
                const string hint = "日志 UI 显示已达 5000 行上限，后续日志仅写入文件，不再显示在监控页。点击下方「打开日志文件夹」按钮可查看完整记录。";
                MailBot.Core.Storage.LogFileWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] [信息] {hint}");
                Logs.Insert(0, new LogEntry(DateTime.Now, LogLevel.Info, hint));
            }
            _droppedSinceFull++;
            return;
        }
        Logs.Insert(0, new LogEntry(DateTime.Now, LogLevel.Info, line));
    }

    /// <summary>结构化日志入列——LogBus 事件订阅路径调用。</summary>
    public void AppendLog(LogEntry entry)
    {
        const int MaxUiLines = 5000;
        if (Logs.Count >= MaxUiLines)
        {
            if (!_uiLogFullWarned)
            {
                _uiLogFullWarned = true;
                const string hint = "日志 UI 显示已达 5000 行上限，后续日志仅写入文件，不再显示在监控页。点击下方「打开日志文件夹」按钮可查看完整记录。";
                MailBot.Core.Storage.LogFileWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] [信息] {hint}");
                Logs.Insert(0, new LogEntry(DateTime.Now, LogLevel.Info, hint));
            }
            _droppedSinceFull++;
            return;
        }
        Logs.Insert(0, entry);
    }

    public void OnBatchCompleted(SendBatchReport report)
    {
        LastBatchSize = report.BatchSize.ToString();
        if (report.SuccessCount > 0)
        {
            // 批次刚发完、等待验证——Pending 状态，蓝色显示。
            // 验证邮箱（若有）拼到 Account，让监控页能直接看到 BCC 去向。
            // 同时显示 worker 计划首次拉取 IMAP 的本地时刻（首查 19:24:07），用户能直观看到何时会出验证结果。
            string accountForLog = string.IsNullOrEmpty(report.VerifierEmail)
                ? report.SenderEmail
                : $"{report.SenderEmail} → {report.VerifierEmail}";
            string firstCheck = report.FirstCheckAtUtc is DateTime fc
                ? $"，首查 {DisplayTimeZone.ToDisplay(fc):HH:mm:ss}"
                : string.Empty;
            LogBus.Instance.Emit(LogLevel.Pending,
                $"{accountForLog}{firstCheck}", account: accountForLog);
        }
        else if (report.ErrorMessage is not null)
        {
            LogBus.Instance.Emit(LogLevel.Error,
                $"→ {report.BatchSize} 封 BCC：{report.ErrorMessage}", account: report.SenderEmail);
        }
        RefreshStats();
        RefreshSenderInfo(report.SenderEmail);
    }

    /// <summary>由 MainViewModel 订阅 SendEngine.NextBatchHintChanged 后调用。
    /// hint 为空字符串时回落占位符"—"。</summary>
    public void UpdateNextBatchHint(string hint)
    {
        NextBatchHint = string.IsNullOrEmpty(hint) ? "—" : hint;
    }

    /// <summary>与 SendEngine / VerificationWorker 内部的同名方法同语义，独立实现以保持 UI 层无 Core 引用。</summary>
    private static string BatchIdShort(string batchId)
    {
        if (string.IsNullOrEmpty(batchId)) return "????";
        var idx = batchId.LastIndexOf('-');
        var tail = idx >= 0 ? batchId[(idx + 1)..] : batchId;
        return tail.Length > 4 ? tail[^4..] : tail;
    }

    public void OnEngineStateChanged(EngineState state)
    {
        EngineStateText = state switch
        {
            EngineState.Idle => "空闲",
            EngineState.Running => "运行中",
            EngineState.Paused => "已暂停",
            EngineState.Completed => "已完成",
            EngineState.Error => "错误",
            _ => state.ToString(),
        };
    }

    private void RefreshRecent()
    {
        RecentRecipients.Clear();
        // 按终态时间倒序展示：unconfirmed → failed → verified → unverified（最近一批验证中）
        foreach (var r in _recipientRepo.GetByStatus("unconfirmed", 20))
        {
            RecentRecipients.Add(r);
        }
        foreach (var r in _recipientRepo.GetByStatus("failed", 20))
        {
            RecentRecipients.Add(r);
        }
        foreach (var r in _recipientRepo.GetByStatus("verified", 20))
        {
            RecentRecipients.Add(r);
        }
        foreach (var r in _recipientRepo.GetByStatus("unverified", 20))
        {
            RecentRecipients.Add(r);
        }
    }
}