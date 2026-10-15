using MailBot.Core.Models;
using Microsoft.Data.Sqlite;

namespace MailBot.Core.Storage;

/// <summary>
/// 应用配置 KV 仓储。读写 app_config 表，提供强类型 AppConfig 视图。
/// </summary>
public sealed class ConfigRepository
{
    private readonly Database _db;

    public ConfigRepository(Database db) => _db = db;

    public string? Get(string key)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_config WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    public void Set(string key, string value)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO app_config(key, value) VALUES($k, $v)
ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>读取所有 KV，强类型映射回 AppConfig。未识别键保留在 Extra。</summary>
    public AppConfig Load()
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM app_config";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                raw[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var cfg = new AppConfig();
        if (raw.TryGetValue("window_start", out var ws)) cfg.WindowStart = ws;
        if (raw.TryGetValue("window_end", out var we)) cfg.WindowEnd = we;
        if (raw.TryGetValue("send_interval_seconds", out var si)
            && int.TryParse(si, out var sii)) cfg.SendIntervalSeconds = sii;
        if (raw.TryGetValue("batch_size", out var bs)
            && int.TryParse(bs, out var bsi)) cfg.BatchSize = bsi;
        if (raw.TryGetValue("max_retries", out var mr)
            && int.TryParse(mr, out var mri)) cfg.MaxRetries = mri;
        if (raw.TryGetValue("max_send_attempts", out var msa)
            && int.TryParse(msa, out var msai)) cfg.MaxSendAttempts = msai;
        if (raw.TryGetValue("cooling_failures", out var cf)
            && int.TryParse(cf, out var cfi)) cfg.CoolingFailures = cfi;
        if (raw.TryGetValue("cooling_minutes", out var cm)
            && int.TryParse(cm, out var cmi)) cfg.CoolingMinutes = cmi;

        foreach (var (k, v) in raw)
        {
            if (!KnownKeys.Contains(k)) cfg.Extra[k] = v;
        }
        return cfg;
    }

    /// <summary>把强类型 AppConfig 全量写回。未识别键保留。</summary>
    public void Save(AppConfig cfg)
    {
        Set("window_start", cfg.WindowStart);
        Set("window_end", cfg.WindowEnd);
        Set("send_interval_seconds", cfg.SendIntervalSeconds.ToString());
        Set("batch_size", cfg.BatchSize.ToString());
        Set("max_retries", cfg.MaxRetries.ToString());
        Set("max_send_attempts", cfg.MaxSendAttempts.ToString());
        Set("cooling_failures", cfg.CoolingFailures.ToString());
        Set("cooling_minutes", cfg.CoolingMinutes.ToString());
        foreach (var (k, v) in cfg.Extra) Set(k, v);
    }

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "window_start", "window_end", "send_interval_seconds",
        "batch_size", "max_retries", "max_send_attempts",
        "cooling_failures", "cooling_minutes",
    };
}