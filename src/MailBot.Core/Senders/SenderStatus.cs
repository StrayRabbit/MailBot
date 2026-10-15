namespace MailBot.Core.Senders;

/// <summary>
/// 发件账号状态常量。设计文档 6.5 节定义：normal / cooling / banned。
/// </summary>
public static class SenderStatus
{
    public const string Normal = "normal";
    public const string Cooling = "cooling";
    public const string Banned = "banned";

    public static bool IsValid(string status) =>
        status is Normal or Cooling or Banned;
}