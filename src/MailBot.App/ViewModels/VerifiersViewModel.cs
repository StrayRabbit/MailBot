using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBot.Core.Models;
using MailBot.Core.Storage;
using MailBot.Core.Verification;

namespace MailBot.App.ViewModels;

/// <summary>
/// 验证邮箱列表视图模型：用于 UI 上展示、增删改、测试连接。
/// 凭据明文只在 Add/Edit 对话框中临时存在，离开对话框就调 CredentialProtector.Protect 加密落库。
/// </summary>
public sealed partial class VerifiersViewModel : ObservableObject
{
    private readonly VerifierRepository _repo;
    private readonly Action<string> _log;

    [ObservableProperty] private ObservableCollection<VerifierAccount> _verifiers = new();

    public VerifiersViewModel(VerifierRepository repo, Action<string> log)
    {
        _repo = repo;
        _log = log;
        Reload();
    }

    public void Reload()
    {
        Verifiers.Clear();
        foreach (var v in _repo.GetAll()) Verifiers.Add(v);
    }

    public void SaveNew(VerifierAccount v, string passwordPlain)
    {
        v.PasswordEncrypted = MailBot.Core.Security.CredentialProtector.Protect(passwordPlain);
        _repo.Insert(v);
        Reload();
    }

    public void Update(VerifierAccount v, string? passwordPlain)
    {
        if (!string.IsNullOrEmpty(passwordPlain))
            v.PasswordEncrypted = MailBot.Core.Security.CredentialProtector.Protect(passwordPlain);
        _repo.Update(v);
        Reload();
    }

    public void Delete(long id)
    {
        _repo.Delete(id);
        Reload();
    }

    public async Task<(bool ok, string? error)> TestConnectionAsync(VerifierAccount v, string passwordPlain, CancellationToken ct)
    {
        var pwd = MailBot.Core.Security.CredentialProtector.Protect(passwordPlain);
        var probe = new VerifierAccount
        {
            Email = v.Email, ImapHost = v.ImapHost, ImapPort = v.ImapPort,
            UseSsl = v.UseSsl, Username = v.Username, PasswordEncrypted = pwd,
        };
        var poller = new ImapPoller(_log);
        return await poller.TestConnectionAsync(probe, ct);
    }
}