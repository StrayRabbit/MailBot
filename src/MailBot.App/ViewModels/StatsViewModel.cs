using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBot.Core;
using MailBot.Core.Models;
using MailBot.Core.Storage;

namespace MailBot.App.ViewModels;

/// <summary>
/// "统计"页：按日期范围显示每日"验证通过"的邮件数单柱状图。
/// 数据从 <see cref="BatchStatsRepository"/> 拉（recipients.status='verified'，按 verified_at 本地日聚合）；
/// 切换到本页面 / 引擎有新批次完成时主动刷新。
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly BatchStatsRepository _batchStatsRepo;

    /// <summary>首次进入页面时的默认区间跨度（含今天）。</summary>
    public const int DefaultDays = 7;

    [ObservableProperty] private DateOnly _startDate;
    [ObservableProperty] private DateOnly _endDate;

    /// <summary>绑定到 DatePicker.SelectedDate 的 DateTime? 镜像；kind=Unspecified，仅用于日历显示。</summary>
    [ObservableProperty] private DateTime? _startDateUi;
    [ObservableProperty] private DateTime? _endDateUi;

    /// <summary>图表 GroupBox 标题，VM 在每次 RefreshChart 时同步刷新。</summary>
    [ObservableProperty] private string _displayTitle = "每日验证通过邮件";

    public ObservableCollection<DailySendCount> DailySendCounts { get; } = new();

    public StatsViewModel(BatchStatsRepository batchStatsRepo)
    {
        _batchStatsRepo = batchStatsRepo;

        var today = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        _endDate = today;
        _startDate = today.AddDays(-(DefaultDays - 1));
        SyncUiFromDates();

        RefreshChart();
    }

    /// <summary>
    /// 兼容旧路径（<c>MainViewModel.BatchCompleted</c> 与 <c>MainWindow.UpdatePage</c> 仍在调用）。
    /// 内部只刷新图表。
    /// </summary>
    public void RefreshAll() => RefreshChart();

    /// <summary>用当前 StartDate / EndDate 重拉柱状图数据并刷新标题。</summary>
    [RelayCommand]
    public void RefreshChart()
    {
        try
        {
            // 区间校验：UI 控件可能输入反了，调到窗口重排。
            var start = StartDate;
            var end = EndDate;
            if (end < start) (start, end) = (end, start);

            var items = _batchStatsRepo.GetDailySendCounts(start, end);
            DailySendCounts.Clear();
            foreach (var item in items)
            {
                DailySendCounts.Add(item);
            }

            StartDate = start;
            EndDate = end;
            SyncUiFromDates();
            DisplayTitle = $"每日验证通过邮件  {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}";
        }
        catch (Exception ex)
        {
            MailBot.Core.Storage.LogFileWriter.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [ERROR] StatsViewModel.RefreshChart failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public void SetPresetToday()
    {
        var today = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        StartDate = today;
        EndDate = today;
        SyncUiFromDates();
        RefreshChart();
    }

    [RelayCommand]
    public void SetPreset7Days() => SetPresetDays(DefaultDays);

    [RelayCommand]
    public void SetPreset30Days() => SetPresetDays(30);

    private void SetPresetDays(int days)
    {
        var today = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        StartDate = today.AddDays(-(days - 1));
        EndDate = today;
        SyncUiFromDates();
        RefreshChart();
    }

    /// <summary>DatePicker 改了 UI 值 → 同步回本地日。</summary>
    partial void OnStartDateUiChanged(DateTime? value)
    {
        var local = ToLocalDate(value);
        if (local is { } d && d != StartDate) StartDate = d;
    }

    partial void OnEndDateUiChanged(DateTime? value)
    {
        var local = ToLocalDate(value);
        if (local is { } d && d != EndDate) EndDate = d;
    }

    private void SyncUiFromDates()
    {
        StartDateUi = new DateTime(StartDate.Year, StartDate.Month, StartDate.Day);
        EndDateUi = new DateTime(EndDate.Year, EndDate.Month, EndDate.Day);
    }

    private static DateOnly? ToLocalDate(DateTime? value)
    {
        if (value is null) return null;
        // DatePicker.SelectedDate 是本地时间（无时区），直接按它的日期分量取本地日。
        return DateOnly.FromDateTime(value.Value);
    }
}