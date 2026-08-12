using NapcatQUI.Core.Database.Entities;
using NapcatQUI.Core.Database.Repositories;

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
}
