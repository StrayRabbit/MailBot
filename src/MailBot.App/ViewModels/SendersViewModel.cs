using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailBot.Core.Models;
using MailBot.Core.Storage;

namespace MailBot.App.ViewModels;

/// <summary>
/// 发件账号列表视图模型：列表展示 + 增删改 + 解封。
/// 列表通过 Refresh() 从仓储重建；CRUD 后必须刷新 UI 集合。
/// </summary>
public sealed partial class SendersViewModel : ObservableObject
{
    private readonly SenderRepository _repo;

    [ObservableProperty] private ObservableCollection<Sender> _senders = new();

    public SendersViewModel(SenderRepository repo)
    {
        _repo = repo;
        Refresh();
    }

    public void Refresh()
    {
        Senders.Clear();
        foreach (var s in _repo.GetAll())
        {
            Senders.Add(s);
        }
    }

    public void Add(Sender sender)
    {
        _repo.Insert(sender);
        Refresh();
    }

    public void Update(Sender sender)
    {
        _repo.Update(sender);
        Refresh();
    }

    public void Delete(Sender sender)
    {
        _repo.Delete(sender.Id);
        Refresh();
    }

    /// <summary>把账号状态置回 normal，清空冷却截止时间。</summary>
    public void Unban(Sender sender)
    {
        _repo.UpdateStatus(sender.Id, "normal", 0, null);
        Refresh();
    }

    /// <summary>把账号当日已发数设为指定值（用户手动调整）。负值由仓储裁剪到 0。</summary>
    public void SetSentToday(Sender sender, int value)
    {
        _repo.SetSentToday(sender.Id, value, DateTime.Now.ToString("yyyy-MM-dd"));
        Refresh();
    }
}