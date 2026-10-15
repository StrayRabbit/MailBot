using MailBot.Core.Models;
using MailBot.Core.Recipients;
using MailBot.Core.Storage;

namespace MailBot.Core.Sending;

/// <summary>
/// 从 recipients 表拉取 pending 收件人，按 batch_size 切片。设计文档 6.2 节：
///   batch = RecipientRepository.PickPending(batchSize)
/// </summary>
public sealed class BatchAssembler
{
    private readonly RecipientRepository _repo;
    private readonly int _batchSize;

    public BatchAssembler(RecipientRepository repo, int batchSize)
    {
        _repo = repo;
        _batchSize = batchSize;
    }

    /// <summary>从 pending 中取一批；为空表示所有收件人都已处理完。</summary>
    public List<Recipient> NextBatch() => _repo.PickPending(_batchSize);

    /// <summary>把一批收件人按 batchSize 二次切片，便于大 batch 拆分（保留顺序）。</summary>
    public static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var bucket = new List<T>(size);
        foreach (var item in source)
        {
            bucket.Add(item);
            if (bucket.Count >= size)
            {
                yield return bucket;
                bucket = new List<T>(size);
            }
        }
        if (bucket.Count > 0) yield return bucket;
    }
}