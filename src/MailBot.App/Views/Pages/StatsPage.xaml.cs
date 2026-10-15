using System.Collections.Specialized;
using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MailBot.App.ViewModels;
using MailBot.Core.Models;
using SkiaSharp;

namespace MailBot.App.Views.Pages;

/// <summary>
/// 统计页：按日期范围渲染每日"验证通过"邮件数单柱状图。
/// 口径：recipients.status='verified'，按 verified_at 本地日聚合。
/// DataContext 继承自 MainViewModel，绑定路径是 <c>Stats.X</c>。
/// </summary>
public partial class StatsPage : UserControl
{
    /// <summary>柱体填充色（蓝色，对齐 AppTheme.xaml 的 Brush.Accent.Default）。</summary>
    private static readonly SKColor BarColor = SKColor.Parse("#2563EB");
    /// <summary>轴标签颜色。</summary>
    private static readonly SKColor AxisLabelColor = SKColor.Parse("#475569");
    /// <summary>分隔线颜色。</summary>
    private static readonly SKColor SeparatorColor = SKColor.Parse("#94A3B8");

    private MainViewModel? _boundMain;
    private bool _collectionHooked;

    public StatsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        _boundMain = main;
        HookCollectionOnce();
        RebuildChart();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_boundMain is not null && _collectionHooked)
        {
            _boundMain.Stats.DailySendCounts.CollectionChanged -= OnDailySendCountsChanged;
            _collectionHooked = false;
        }
    }

    private void HookCollectionOnce()
    {
        if (_boundMain is null || _collectionHooked) return;
        _boundMain.Stats.DailySendCounts.CollectionChanged += OnDailySendCountsChanged;
        _collectionHooked = true;
    }

    private void OnDailySendCountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildChart();

    /// <summary>
    /// 用 VM 里的最新 DailySendCounts 重建单柱状图。
    /// LiveCharts2 在 WPF 下用代码构造 Series 链比 XAML 类型转换器更稳；
    /// 数据源变了就整个重建——365 个点的开销可忽略。
    /// </summary>
    private void RebuildChart()
    {
        if (_boundMain is null) return;
        var items = _boundMain.Stats.DailySendCounts;

        var xAxis = new DateTimeAxis(TimeSpan.FromDays(1), date => date.ToString("MM-dd"))
        {
            LabelsPaint = new SolidColorPaint(AxisLabelColor),
            SeparatorsPaint = new SolidColorPaint(SeparatorColor) { StrokeThickness = 1 },
        };
        var yAxis = new Axis
        {
            MinLimit = 0,
            LabelsPaint = new SolidColorPaint(AxisLabelColor),
            SeparatorsPaint = new SolidColorPaint(SeparatorColor) { StrokeThickness = 1 },
        };

        if (items.Count == 0)
        {
            Chart.Series = System.Array.Empty<ISeries>();
            Chart.XAxes = new ICartesianAxis[] { xAxis };
            Chart.YAxes = new ICartesianAxis[] { yAxis };
            return;
        }

        var points = new List<DateTimePoint>(items.Count);
        foreach (DailySendCount item in items)
        {
            // DailySendCount.Day 是本地 DateOnly；转成 DateTime 给 Ungrid 用。
            var dt = item.Day.ToDateTime(TimeOnly.MinValue);
            points.Add(new DateTimePoint(dt, item.VerifiedCount));
        }

        var series = new ColumnSeries<DateTimePoint>
        {
            Name = "已验证",
            Fill = new SolidColorPaint(BarColor),
            Stroke = null,
            Values = points,
        };

        Chart.Series = new ISeries[] { series };
        Chart.XAxes = new ICartesianAxis[] { xAxis };
        Chart.YAxes = new ICartesianAxis[] { yAxis };
    }
}