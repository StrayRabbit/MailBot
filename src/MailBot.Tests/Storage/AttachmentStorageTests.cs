using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Storage;

// AttachmentStorage 持有 static 状态（db 路径），必须串行避免跨测试污染。
[CollectionDefinition(nameof(AttachmentStorageCollection), DisableParallelization = true)]
public class AttachmentStorageCollection { }

[Collection(nameof(AttachmentStorageCollection))]
public class AttachmentStorageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public AttachmentStorageTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_test_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void StorageDir_FollowsDatabaseDbPath()
    {
        // new Database(_dbPath) 构造时调用 AttachmentStorage.TrackDbPath(_dbPath)
        var expected = Path.Combine(Path.GetDirectoryName(_dbPath)!, "attachments");
        Assert.Equal(expected, AttachmentStorage.StorageDir);
    }

    [Fact]
    public void NewDatabase_UpdatesStorageDir()
    {
        // 后构造的 Database 应覆盖之前的 db 路径（MailBot 单实例场景下没问题，
        // 但需明确行为，避免日后改成多实例时出意外）
        var newDbPath = Path.Combine(Path.GetTempPath(), $"mailbot_test_{Guid.NewGuid():N}.db");
        var db2 = new Database(newDbPath);
        try
        {
            var expected = Path.Combine(Path.GetDirectoryName(newDbPath)!, "attachments");
            Assert.Equal(expected, AttachmentStorage.StorageDir);
        }
        finally
        {
            if (File.Exists(newDbPath)) File.Delete(newDbPath);
        }
    }
}