namespace NapcatQUI.Core.Events;

/// <summary>
/// 领域事件 — 新消息到达
/// </summary>
public class MessageReceivedEvent
{
    public Models.Message Message { get; init; } = null!;
}

/// <summary>
/// 领域事件 — 账号连接状态变更
/// </summary>
public class AccountStateChangedEvent
{
    public string AccountId { get; init; } = null!;
    public Models.ConnectionState State { get; init; }
}

/// <summary>
/// 领域事件 — 联系人信息更新
/// </summary>
public class ContactUpdatedEvent
{
    public string AccountId { get; init; } = null!;
    public string UserId { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
}
