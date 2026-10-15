using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MailBot.Core.Storage;

/// <summary>
/// SQLite 连接管理：负责打开连接、维护 schema、暴露 SqliteConnection 给上层仓储。
/// 设计为非线程安全：调用方自行保证不跨线程共享连接；本类对外仅暴露 Open/EnsureSchema 两个动词。
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public string DbPath { get; }

    public Database(string dbPath)
    {
        DbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 不使用 Shared 缓存：测试清理文件时不会被锁住；生产环境单连接使用也够用。
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        // 通知 AttachmentStorage 当前 db 路径，让附件目录跟随。
        AttachmentStorage.TrackDbPath(DbPath);
        // 通知 LogFileWriter 同样跟随。
        LogFileWriter.TrackDbPath(DbPath);
    }

    /// <summary>默认数据库路径：%AppData%\MailBot\mailbot.db。</summary>
    public static string DefaultDbPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MailBot");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "mailbot.db");
    }

    /// <summary>
    /// 按优先级解析数据库路径：命令行 --db > 环境变量 MAILBOT_DB_PATH >
    /// 外部配置文件（与 exe 同目录的 mailbot.config.json）>
    /// 嵌入配置（MailBot.Core.dll 内的出厂默认值）> 硬编码默认路径。
    /// 返回的路径已确保父目录存在。
    /// configDirOverride 仅供测试注入外部配置文件目录；生产代码忽略。
    /// </summary>
    public static string ResolveDbPath(string[]? args = null, string? configDirOverride = null)
    {
        var raw = ReadDbPathFromArgs(args)
               ?? ReadDbPathFromEnv()
               ?? ReadDbPathFromConfigFile(configDirOverride)
               ?? ReadDbPathFromEmbeddedConfig();

        if (string.IsNullOrWhiteSpace(raw))
            return DefaultDbPath();

        var expanded = Environment.ExpandEnvironmentVariables(raw);
        var full = Path.GetFullPath(expanded);
        // 用户自定义路径需确保父目录存在；SQLite 不会自动创建。
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return full;
    }

    private static string? ReadDbPathFromArgs(string[]? args)
    {
        if (args is null) return null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--db", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
            if (a.StartsWith("--db=", StringComparison.OrdinalIgnoreCase))
                return a["--db=".Length..];
        }
        return null;
    }

    private static string? ReadDbPathFromEnv()
    {
        var v = Environment.GetEnvironmentVariable("MAILBOT_DB_PATH");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string? ReadDbPathFromConfigFile(string? dirOverride)
    {
        // 默认与 exe 同目录：开发者改配置无需跳到 %AppData%。
        // 注意：装在 Program Files 时该目录可能没有写权限，但读已存在的文件不受影响。
        var dir = dirOverride ?? AppContext.BaseDirectory;
        var cfgPath = Path.Combine(dir, "mailbot.config.json");
        if (!File.Exists(cfgPath)) return null;

        try
        {
            using var stream = File.OpenRead(cfgPath);
            var cfg = JsonSerializer.Deserialize<DbConfigFile>(stream);
            return string.IsNullOrWhiteSpace(cfg?.DatabasePath) ? null : cfg.DatabasePath;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"配置文件 {cfgPath} 格式无效：{ex.Message}", ex);
        }
    }

    private sealed class DbConfigFile
    {
        public string? DatabasePath { get; set; }
    }

    /// <summary>
    /// 读取嵌入在 MailBot.Core.dll 里的出厂默认配置。
    /// 资源名 = 默认命名空间 + 文件相对路径（点号分隔）。
    /// 资源缺失（开发场景 dll 未嵌入）时返回 null，由外层回退到硬编码默认路径。
    /// </summary>
    private static string? ReadDbPathFromEmbeddedConfig()
    {
        const string resourceName = "MailBot.Core.Storage.mailbot.config.json";
        using var stream = typeof(Database).Assembly
            .GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        try
        {
            var cfg = JsonSerializer.Deserialize<DbConfigFile>(stream);
            return string.IsNullOrWhiteSpace(cfg?.DatabasePath) ? null : cfg.DatabasePath;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"嵌入配置 {resourceName} 格式无效：{ex.Message}", ex);
        }
    }

    /// <summary>打开新连接，使用方负责释放。</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // 启用外键 + WAL（仅在非临时文件场景下启用；测试场景下不要 WAL，避免文件锁）
        if (!DbPath.Contains("/tmp/", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(DbPath).StartsWith("mailbot_test_", StringComparison.OrdinalIgnoreCase))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>确保表结构存在（首次启动时调用，幂等）。</summary>
    public void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        MigrateSenders(conn);
        MigrateRecipients(conn);
        // 退额审计表已废弃（MailBot v? 后去掉自动退额机制,改成用户手动编辑已发），
        // 这里幂等自净：老库里残留的 send_quota_refunds 不影响功能,但占磁盘 + 易让用户误以为还在跑。
        // 无外键引用它,可安全 DROP。
        cmd.CommandText = "DROP TABLE IF EXISTS send_quota_refunds;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 老库迁移：为已存在的 recipients 表补齐 batch_id / verified_at 列。
    /// 新建库走 CREATE TABLE 已有列，无需 ALTER。
    /// </summary>
    private static void MigrateRecipients(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "recipients", "batch_id"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE recipients ADD COLUMN batch_id TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "recipients", "verified_at"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE recipients ADD COLUMN verified_at TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "recipients", "pending_retry_at"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE recipients ADD COLUMN pending_retry_at TEXT";
            alter.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "recipients", "unconfirmed_count"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE recipients ADD COLUMN unconfirmed_count INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 老库迁移：为已存在的 senders 表补齐新增列。
    /// 新建库走 CREATE TABLE 已有列，无需 ALTER。
    /// </summary>
    private static void MigrateSenders(SqliteConnection conn)
    {
        if (ColumnExists(conn, "senders", "trust_invalid_certificate"))
            return;

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE senders ADD COLUMN trust_invalid_certificate INTEGER NOT NULL DEFAULT 0";
        alter.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(reader.GetOrdinal("name")) == column)
                return true;
        }
        return false;
    }

    private const string Schema = @"
CREATE TABLE IF NOT EXISTS senders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    email TEXT NOT NULL UNIQUE,
    smtp_host TEXT NOT NULL,
    smtp_port INTEGER NOT NULL DEFAULT 465,
    use_ssl INTEGER NOT NULL DEFAULT 1,
    trust_invalid_certificate INTEGER NOT NULL DEFAULT 0,
    username TEXT NOT NULL,
    password_encrypted BLOB NOT NULL,
    daily_quota INTEGER NOT NULL DEFAULT 50,
    sent_today INTEGER NOT NULL DEFAULT 0,
    quota_reset_date TEXT NOT NULL,
    consecutive_failures INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'normal',
    cooldown_until TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS recipients (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    email TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    error_message TEXT,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_attempt_at TEXT,
    sent_at TEXT,
    sent_via_sender_id INTEGER REFERENCES senders(id),
    imported_at TEXT NOT NULL,
    source_file TEXT,
    batch_id TEXT,
    verified_at TEXT,
    pending_retry_at TEXT,
    unconfirmed_count INTEGER NOT NULL DEFAULT 0,
    UNIQUE(email, source_file)
);

CREATE INDEX IF NOT EXISTS idx_recipients_status ON recipients(status);

-- 历史表 send_logs 已移除(2026-09-30 起);老 db 升级时主动清掉,
-- DROP 不存在对象会失败被 catch,所以用 IF EXISTS。
DROP TABLE IF EXISTS send_logs;
DROP INDEX IF EXISTS idx_send_logs_sent_at;

CREATE TABLE IF NOT EXISTS app_config (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS verifier_accounts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    email TEXT NOT NULL UNIQUE,
    imap_host TEXT NOT NULL,
    imap_port INTEGER NOT NULL DEFAULT 993,
    use_ssl INTEGER NOT NULL DEFAULT 1,
    username TEXT NOT NULL,
    password_encrypted BLOB NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    last_polled_at TEXT,
    last_seen_uid INTEGER,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
";
}