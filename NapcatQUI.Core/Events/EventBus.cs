using System.Collections.Concurrent;

namespace NapcatQUI.Core.Events;

/// <summary>
/// 轻量级事件总线 — 订阅/发布，避免引入重型消息框架
/// </summary>
public class EventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<object, Task>>> _handlers = new();

    public void Subscribe<T>(Func<T, Task> handler) where T : class
    {
        var type = typeof(T);
        _handlers.AddOrUpdate(type,
            _ => new List<Func<object, Task>> { o => handler((T)o) },
            (_, list) => { list.Add(o => handler((T)o)); return list; });
    }

    public async Task PublishAsync<T>(T @event) where T : class
    {
        if (_handlers.TryGetValue(typeof(T), out var handlers))
        {
            foreach (var handler in handlers)
            {
                try { await handler(@event); }
                catch { /* 一个 handler 挂了不影响其他 */ }
            }
        }
    }
}
