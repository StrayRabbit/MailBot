using MailBot.Core.Models;
using MailBot.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MailBot.Tests.Storage;

public class VerifierRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly VerifierRepository _repo;

    public VerifierRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_test_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _repo = new VerifierRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static VerifierAccount MakeVerifier(string email, byte[] pwd) => new()
    {
        Email = email,
        ImapHost = "imap.qq.com",
        ImapPort = 993,
        UseSsl = true,
        Username = email,
        PasswordEncrypted = pwd,
        Enabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Insert_AndGetById_RoundTrips()
    {
        var v = MakeVerifier("a@qq.com", new byte[] { 1, 2, 3 });
        var id = _repo.Insert(v);
        var loaded = _repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Equal("a@qq.com", loaded!.Email);
        Assert.Equal("imap.qq.com", loaded.ImapHost);
        Assert.Equal(993, loaded.ImapPort);
        Assert.True(loaded.UseSsl);
        Assert.True(loaded.Enabled);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.PasswordEncrypted);
    }

    [Fact]
    public void ListEnabled_ReturnsOnlyEnabled()
    {
        _repo.Insert(MakeVerifier("on@qq.com", new byte[] { 1 }));
        _repo.Insert(MakeVerifier("off@qq.com", new byte[] { 1 }));
        var all = _repo.GetAll();
        var off = all.First(v => v.Email == "off@qq.com");
        off.Enabled = false;
        _repo.Update(off);

        var enabled = _repo.ListEnabled();
        Assert.Single(enabled);
        Assert.Equal("on@qq.com", enabled[0].Email);
    }

    [Fact]
    public void UniqueEmail_Throws()
    {
        _repo.Insert(MakeVerifier("dup@qq.com", new byte[] { 1 }));
        Assert.Throws<SqliteException>(() => _repo.Insert(MakeVerifier("dup@qq.com", new byte[] { 1 })));
    }

    [Fact]
    public void UpdateLastSeen_Persists()
    {
        var id = _repo.Insert(MakeVerifier("seen@qq.com", new byte[] { 1 }));
        _repo.UpdateLastSeen(id, 12345u);
        var loaded = _repo.GetById(id);
        Assert.Equal(12345u, loaded!.LastSeenUid);
        Assert.NotNull(loaded.LastPolledAt);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        var id = _repo.Insert(MakeVerifier("del@qq.com", new byte[] { 1 }));
        _repo.Delete(id);
        Assert.Null(_repo.GetById(id));
    }
}