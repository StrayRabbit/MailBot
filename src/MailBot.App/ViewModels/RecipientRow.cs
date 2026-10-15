using CommunityToolkit.Mvvm.ComponentModel;
using MailBot.Core.Models;

namespace MailBot.App.ViewModels;

/// <summary>
/// DataGrid 行 VM：包装 <see cref="Recipient"/> + UI 选中状态。
/// 只在 UI 层使用，不进入 Core；[ObservableProperty] 让 IsSelected 双向通知 DataGrid 复选框。
/// </summary>
public sealed partial class RecipientRow : ObservableObject
{
    public Recipient Model { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>全局序号（跨页连续 1-N），由 RecipientsViewModel 在创建时设置。</summary>
    public int RowNumber { get; init; }

    public RecipientRow(Recipient model) => Model = model;

    // 透传字段给 XAML 绑定，避免在 XAML 里写 Model.Email 这种深度路径。
    public long Id => Model.Id;
    public string Email => Model.Email;
    public string Status => Model.Status;
    public string? ErrorMessage => Model.ErrorMessage;
    public string? BatchId => Model.BatchId;
    public int AttemptCount => Model.AttemptCount;

    /// <summary>"最后活动时间"：优先送达时间（IMAP 命中），其次最近一次尝试时间。
    /// 让 UI 不必关心优先用哪个字段，避免在 XAML 写复杂的多层 binding。</summary>
    public DateTime? LastActivityAt => Model.VerifiedAt ?? Model.LastAttemptAt;
}