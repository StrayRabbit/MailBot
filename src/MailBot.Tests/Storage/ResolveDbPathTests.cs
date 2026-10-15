using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Storage;

// 环境变量 + 配置文件场景修改进程级状态，必须串行避免相互污染。
[CollectionDefinition(nameof(ResolveDbPathCollection), DisableParallelization = true)]
public class ResolveDbPathCollection { }

[Collection(nameof(ResolveDbPathCollection))]
public class ResolveDbPathTests : IDisposable
{
    private const string EnvVar = "MAILBOT_DB_PATH";
    private readonly string _configDir;
    private readonly string? _savedEnv;

    public ResolveDbPathTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"mailbot_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configDir);
        _savedEnv = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _savedEnv);
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void NoConfig_ReturnsDefault()
    {
        var resolved = Database.ResolveDbPath(args: null, configDirOverride: _configDir);
        Assert.Equal(Database.DefaultDbPath(), resolved);
    }

    [Fact]
    public void CommandLineFlag_TakesPrecedence()
    {
        var custom = Path.Combine(_configDir, "arg.db");
        var resolved = Database.ResolveDbPath(new[] { "--db", custom }, configDirOverride: _configDir);
        Assert.Equal(Path.GetFullPath(custom), resolved);
        Assert.True(Directory.Exists(Path.GetDirectoryName(resolved!)));
    }

    [Fact]
    public void CommandLineEquals_TakesPrecedence()
    {
        var custom = Path.Combine(_configDir, "arg_equals.db");
        var resolved = Database.ResolveDbPath(new[] { $"--db={custom}" }, configDirOverride: _configDir);
        Assert.Equal(Path.GetFullPath(custom), resolved);
    }

    [Fact]
    public void EnvVar_OverridesConfigFile()
    {
        var fromEnv = Path.Combine(_configDir, "from_env.db");
        var fromFile = Path.Combine(_configDir, "from_file.db");
        WriteConfig(fromFile);
        Environment.SetEnvironmentVariable(EnvVar, fromEnv);

        var resolved = Database.ResolveDbPath(args: null, configDirOverride: _configDir);
        Assert.Equal(Path.GetFullPath(fromEnv), resolved);
    }

    [Fact]
    public void ConfigFile_OverridesDefault()
    {
        var fromFile = Path.Combine(_configDir, "from_file.db");
        WriteConfig(fromFile);

        var resolved = Database.ResolveDbPath(args: null, configDirOverride: _configDir);
        Assert.Equal(Path.GetFullPath(fromFile), resolved);
    }

    [Fact]
    public void DefaultConfigDir_IsExeBaseDirectory()
    {
        // 不传 configDirOverride：默认从 AppContext.BaseDirectory 读 mailbot.config.json
        var cfgPath = Path.Combine(AppContext.BaseDirectory, "mailbot.config.json");
        var custom = Path.Combine(AppContext.BaseDirectory, $"_resolve_test_{Guid.NewGuid():N}.db");
        var backup = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : null;
        File.WriteAllText(cfgPath,
            $$"""{"DatabasePath":"{{custom.Replace("\\", "\\\\")}}"}""");
        try
        {
            var resolved = Database.ResolveDbPath(args: null);
            Assert.Equal(Path.GetFullPath(custom), resolved);
        }
        finally
        {
            if (backup is null) File.Delete(cfgPath);
            else File.WriteAllText(cfgPath, backup);
        }
    }

    [Fact]
    public void EmbeddedConfig_FallbackWhenExternalMissing()
    {
        // _configDir 内无外部配置文件 → 回退到嵌入在 MailBot.Core.dll 里的出厂默认值
        var expected = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MailBot", "mailbot.db"));
        var resolved = Database.ResolveDbPath(args: null, configDirOverride: _configDir);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void EmbeddedConfig_ManifestResourceExists()
    {
        // 验证 csproj 的 EmbeddedResource 行生效，资源真的被编进 dll
        using var stream = typeof(Database).Assembly
            .GetManifestResourceStream("MailBot.Core.Storage.mailbot.config.json");
        Assert.NotNull(stream);
    }

    [Fact]
    public void CommandLine_OverridesEnvAndFile()
    {
        var fromArg = Path.Combine(_configDir, "from_arg.db");
        var fromEnv = Path.Combine(_configDir, "from_env.db");
        var fromFile = Path.Combine(_configDir, "from_file.db");
        WriteConfig(fromFile);
        Environment.SetEnvironmentVariable(EnvVar, fromEnv);

        var resolved = Database.ResolveDbPath(new[] { "--db", fromArg }, configDirOverride: _configDir);
        Assert.Equal(Path.GetFullPath(fromArg), resolved);
    }

    [Fact]
    public void EmptyConfigValue_FallsBackToDefault()
    {
        WriteConfig("");
        var resolved = Database.ResolveDbPath(args: null, configDirOverride: _configDir);
        Assert.Equal(Database.DefaultDbPath(), resolved);
    }

    [Fact]
    public void ConfigFileMissing_FallsBackToDefault()
    {
        // _configDir 内未写入任何文件
        var resolved = Database.ResolveDbPath(args: null, configDirOverride: _configDir);
        Assert.Equal(Database.DefaultDbPath(), resolved);
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        File.WriteAllText(Path.Combine(_configDir, "mailbot.config.json"), "{ not json");
        Assert.Throws<InvalidOperationException>(() =>
            Database.ResolveDbPath(args: null, configDirOverride: _configDir));
    }

    [Fact]
    public void ExpandsEnvironmentVariables()
    {
        // %TEMP% 在 Windows 上保证存在
        var raw = "%TEMP%" + Path.DirectorySeparatorChar + $"mailbot_expand_{Guid.NewGuid():N}.db";
        var resolved = Database.ResolveDbPath(new[] { "--db", raw }, configDirOverride: _configDir);
        Assert.DoesNotContain("%TEMP%", resolved);
        Assert.True(Path.IsPathRooted(resolved));
    }

    private void WriteConfig(string dbPath)
    {
        var escaped = dbPath.Replace("\\", "\\\\");
        var json = $"{{ \"DatabasePath\": \"{escaped}\" }}";
        File.WriteAllText(Path.Combine(_configDir, "mailbot.config.json"), json);
    }
}