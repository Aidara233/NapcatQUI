using NapcatQUI.Core.Database.Entities;
using NapcatQUI.Core.Database.Repositories;
using NapcatQUI.Core.Models;

namespace NapcatQUI.Core.Services;

/// <summary>
/// 消息历史查询服务
/// </summary>
public class HistoryService
{
    private readonly MessageRepository _messageRepo;

    public HistoryService(MessageRepository messageRepo)
    {
        _messageRepo = messageRepo;
    }

    public async Task<List<MessageEntity>> GetHistoryAsync(
        string accountId, string targetId, int limit = 50, string? beforeTimestamp = null)
    {
        return await _messageRepo.GetHistoryAsync(accountId, targetId, limit, beforeTimestamp);
    }

    public async Task<List<MessageEntity>> SearchAsync(string accountId, string query, int limit = 20)
    {
        return await _messageRepo.SearchAsync(accountId, query, limit);
    }

    public async Task<MessageEntity?> GetMessageAsync(string accountId, string messageId)
    {
        return await _messageRepo.GetByIdAsync(accountId, messageId);
    }

    public async Task<List<ConversationSummary>> GetConversationSummariesAsync(string accountId)
    {
        return await _messageRepo.GetConversationSummariesAsync(accountId);
    }

    /// <summary>每个会话的未读数（未读 = 时间晚于已读标记的消息数）</summary>
    public async Task<Dictionary<(string TargetId, int MessageType), int>> GetUnreadCountsAsync(string accountId)
    {
        return await _messageRepo.GetUnreadCountsAsync(accountId);
    }

    /// <summary>标记某会话已读：把已读标记推进到该会话最新一条消息的时间</summary>
    public async Task MarkConversationReadAsync(string accountId, string targetId, MessageType type)
    {
        var max = await _messageRepo.GetMaxTimestampAsync(accountId, targetId, (int)type);
        if (max is null) return; // 会话还没有任何消息，无需写标记
        await _messageRepo.SetLastReadAsync(accountId, targetId, (int)type, max);
    }
}
