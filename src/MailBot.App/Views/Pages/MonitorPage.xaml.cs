using System.Windows.Controls;

namespace MailBot.App.Views.Pages;

/// <summary>监控首页：四桶统计 + 最近收件人 + 日志流。DataContext 继承自 MainViewModel。</summary>
public partial class MonitorPage : UserControl
{
    public MonitorPage()
    {
        InitializeComponent();
    }
}