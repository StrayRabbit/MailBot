using System.IO;

namespace MailBot.Core.Storage;

/// <summary>
/// 附件存储：把用户选择的附件复制到 MailBot 自己的目录下，
/// 避免原文件被删/移动/换电脑后附件失效。
/// 目录位置跟随 <see cref="Database.DbPath"/>，由 <see cref="Database"/> 构造时登记。
/// </summary>
public static class AttachmentStorage
{
    private static string? _dbPath;

    /// <summary>
    /// 由 <see cref="Database"/> 构造时调用，登记当前 db 路径。
    /// 后续 <see cref="StorageDir"/> 基于该路径计算，确保附件目录与 db 同根。
    /// 若从未调用（早期启动失败/未走 Database 构造），回退到 <see cref="Database.DefaultDbPath()"/>。
    /// </summary>
    public static void TrackDbPath(string dbPath) => _dbPath = dbPath;

    /// <summary>附件目录：&lt;db 父目录&gt;/attachments。</summary>
    public static string StorageDir =>
        Path.Combine(
            Path.GetDirectoryName(_dbPath ?? Database.DefaultDbPath())!,
            "attachments");

    /// <summary>
    /// 把源文件复制到 <see cref="StorageDir"/>，返回新路径。
    /// 同名时直接覆盖，避免累积 "(1)(2)..." 后缀的重复副本。
    /// </summary>
    public static string CopyToStorage(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("源文件不存在", sourcePath);

        Directory.CreateDirectory(StorageDir);

        var target = Path.Combine(StorageDir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target, overwrite: true);
        return target;
    }
}