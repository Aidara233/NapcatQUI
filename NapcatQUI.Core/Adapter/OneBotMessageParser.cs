using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NapcatQUI.Core.Models;

namespace NapcatQUI.Core.Adapter;

/// <summary>
/// OneBot v11 消息/事件解析器 — 把 NapCat 推过来的 JSON 转为领域模型
/// </summary>
public class OneBotMessageParser
{
    private readonly ILogger<OneBotMessageParser> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public OneBotMessageParser(ILogger<OneBotMessageParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 解析 WebSocket 推送的一条完整 JSON
    /// 返回 null 表示该条不需要处理（如心跳、生命周期等纯元事件）
    /// </summary>
    public Message? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 区分 post_type
            if (!root.TryGetProperty("post_type", out var postTypeEl))
            {
                // 可能是 API 响应（含 echo 字段），跳过
                if (root.TryGetProperty("echo", out _))
                    return null;

                _logger.LogWarning("Unknown JSON structure: {Json}", json[..Math.Min(200, json.Length)]);
                return null;
            }

            var postType = postTypeEl.GetString();

            return postType switch
            {
                "message" => ParseMessage(root),
                "notice" => ParseNotice(root),
                "request" => ParseRequest(root),
                "meta_event" => ParseMetaEvent(root),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse OneBot message: {Json}", json[..Math.Min(500, json.Length)]);
            return null;
        }
    }

    private Message ParseMessage(JsonElement root)
    {
        var msg = new Message
        {
            RawJson = root.GetRawText()
        };

        // 基础字段（message_id 等 ID 在 OneBot v11 里是数字，也有扩展端返回字符串）
        msg.MessageId = GetId(root, "message_id") ?? "";
        msg.Timestamp = DateTimeOffset.FromUnixTimeSeconds(
            GetInt64(root, "time") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // 消息类型
        var msgType = GetString(root, "message_type");
        msg.Type = msgType switch
        {
            "private" => MessageType.Private,
            "group" => MessageType.Group,
            _ => MessageType.System
        };

        // 子类型
        var subType = GetString(root, "sub_type");
        msg.SubType = subType switch
        {
            "anonymous" => MessageSubType.Anonymous,
            "notice" => MessageSubType.Notice,
            _ => MessageSubType.Normal
        };

        // 发送者
        var sender = root.TryGetProperty("sender", out var s) ? s : default;
        msg.SenderId = GetId(sender, "user_id") ?? GetId(root, "user_id") ?? "";
        msg.SenderName = GetString(sender, "nickname") ?? GetString(sender, "card") ?? "";

        // 目标
        if (msg.Type == MessageType.Group)
            msg.TargetId = GetId(root, "group_id") ?? "";
        else if (msg.Type == MessageType.Private)
            // 私聊 target = 对方。自发消息的回声 user_id 是自己，NapCat 有时会带 target_id 指明接收者
            msg.TargetId = GetId(root, "target_id") ?? msg.SenderId;

        // self_id → accountId（用于多账号区分消息归属）
        msg.AccountId = GetId(root, "self_id") ?? "";

        // 自己发出的消息 NapCat 也会推一条 message 事件回来（user_id == self_id），
        // 据此判定而不是恒为 false，否则自己发的消息会被当成"别人发的"
        msg.IsSentBySelf = !string.IsNullOrEmpty(msg.SenderId) && msg.SenderId == msg.AccountId;

        // 解析消息段
        if (root.TryGetProperty("message", out var messageArray) && messageArray.ValueKind == JsonValueKind.Array)
        {
            msg.Segments = ParseMessageArray(messageArray, out var replyToId);
            msg.ReplyToId = replyToId;
        }
        else
        {
            // 纯文本降级
            var rawText = GetString(root, "raw_message") ?? "";
            msg.Segments = new List<MessageSegment> { MessageSegment.CreateText(rawText) };
        }

        msg.Content = msg.BuildSearchableContent();

        return msg;
    }

    private Message ParseNotice(JsonElement root)
    {
        var noticeType = GetString(root, "notice_type") ?? "";
        var msg = new Message
        {
            RawJson = root.GetRawText(),
            MessageId = $"notice_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            AccountId = GetId(root, "self_id") ?? "",
            Type = MessageType.System,
            SubType = MessageSubType.Notice,
            IsSystemEvent = true,
            NoticeType = noticeType,
            Timestamp = DateTimeOffset.UtcNow,
            Content = $"[系统通知: {noticeType}]"
        };

        // 提取通知涉及的 ID
        msg.NoticeUserId = GetId(root, "user_id");
        msg.TargetId = GetId(root, "group_id") ?? GetId(root, "user_id") ?? "";

        // 群名片变更
        if (noticeType == "group_card")
        {
            var cardNew = GetString(root, "card_new") ?? "";
            var cardOld = GetString(root, "card_old") ?? "";
            msg.Content = $"[群名片变更] → {cardNew}";
        }
        // 群成员增减
        else if (noticeType == "group_increase")
        {
            msg.Content = $"[新成员加入] {msg.NoticeUserId}";
        }
        else if (noticeType == "group_decrease")
        {
            var subType = GetString(root, "sub_type");
            var reason = subType switch
            {
                "kick" => "被踢出",
                "kick_me" => "主动退出",
                _ => "离开"
            };
            msg.Content = $"[成员{reason}] {msg.NoticeUserId}";
        }
        // 群禁言
        else if (noticeType == "group_ban")
        {
            var duration = GetInt64(root, "duration") ?? 0;
            msg.Content = duration > 0
                ? $"[禁言] {msg.NoticeUserId} {duration}秒"
                : $"[解除禁言] {msg.NoticeUserId}";
        }
        // 管理员变更
        else if (noticeType == "group_admin")
        {
            var set = GetString(root, "sub_type") == "set";
            msg.Content = set ? $"[设为管理员] {msg.NoticeUserId}" : $"[取消管理员] {msg.NoticeUserId}";
        }
        // 撤回
        else if (noticeType == "group_recall" || noticeType == "friend_recall")
        {
            var recalledMsgId = GetId(root, "message_id");
            msg.Content = $"[消息撤回] 消息ID: {recalledMsgId}";
        }
        // 戳一戳
        else if (noticeType == "notify")
        {
            var subType = GetString(root, "sub_type");
            if (subType == "poke")
            {
                var targetId = GetId(root, "target_id");
                msg.Content = $"[戳一戳] {msg.NoticeUserId} → {targetId}";
            }
        }
        // 群文件上传
        else if (noticeType == "group_upload")
        {
            if (root.TryGetProperty("file", out var file))
            {
                var fileName = GetString(file, "name") ?? "未知文件";
                var fileSize = GetInt64(file, "size") ?? 0;
                msg.Content = $"[群文件上传] {fileName} ({FormatFileSize(fileSize)})";
            }
        }

        return msg;
    }

    private Message? ParseRequest(JsonElement root)
    {
        var requestType = GetString(root, "request_type");
        var msg = new Message
        {
            RawJson = root.GetRawText(),
            MessageId = $"request_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            AccountId = GetId(root, "self_id") ?? "",
            Type = MessageType.System,
            SubType = MessageSubType.Notice,
            IsSystemEvent = true,
            NoticeType = $"request_{requestType}",
            Timestamp = DateTimeOffset.UtcNow
        };

        if (requestType == "friend")
        {
            msg.SenderId = GetId(root, "user_id") ?? "";
            msg.Content = $"[好友请求] {msg.SenderId}: {GetString(root, "comment") ?? ""}";
        }
        else if (requestType == "group")
        {
            msg.SenderId = GetId(root, "user_id") ?? "";
            msg.TargetId = GetId(root, "group_id") ?? "";
            var subType = GetString(root, "sub_type");
            msg.Content = subType == "invite"
                ? $"[邀请加群] {msg.SenderId} 邀请加入 {msg.TargetId}"
                : $"[加群请求] {msg.SenderId} 申请加入 {msg.TargetId}";
        }

        return msg;
    }

    private Message? ParseMetaEvent(JsonElement root)
    {
        var metaType = GetString(root, "meta_event_type");
        // 心跳 — 不生成消息
        if (metaType == "heartbeat") return null;

        // 生命周期 — 可记录但当前不生成消息
        _logger.LogDebug("Meta event: {Type}", metaType);
        return null;
    }

    // ---- 消息段解析 ----

    private List<MessageSegment> ParseMessageArray(JsonElement array, out string? replyToId)
    {
        replyToId = null;
        var segments = new List<MessageSegment>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var type = GetString(element, "type") ?? "unknown";
            var data = new Dictionary<string, object?>();

            if (element.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dataEl.EnumerateObject())
                {
                    data[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.GetRawText()
                    };
                }
            }

            var segmentType = ParseSegmentType(type);
            var segment = new MessageSegment { Type = segmentType, Data = data };

            // 提取回复 ID
            if (segmentType == MessageSegmentType.Reply && data.TryGetValue("id", out var rid))
                replyToId = rid?.ToString();

            segments.Add(segment);
        }

        return segments;
    }

    private static MessageSegmentType ParseSegmentType(string type) => type switch
    {
        "text" => MessageSegmentType.Text,
        "image" => MessageSegmentType.Image,
        "at" => MessageSegmentType.At,
        "reply" => MessageSegmentType.Reply,
        "face" => MessageSegmentType.Face,
        "record" => MessageSegmentType.Record,
        "video" => MessageSegmentType.Video,
        "file" => MessageSegmentType.File,
        "json" => MessageSegmentType.Json,
        "xml" => MessageSegmentType.Xml,
        "markdown" => MessageSegmentType.Markdown,
        "mention" => MessageSegmentType.Mention,
        "location" => MessageSegmentType.Location,
        "share" => MessageSegmentType.Share,
        "contact" => MessageSegmentType.Contact,
        "forward" => MessageSegmentType.Forward,
        "node" => MessageSegmentType.Node,
        "music" => MessageSegmentType.Music,
        "custom" => MessageSegmentType.Custom,
        _ => MessageSegmentType.Unknown
    };

    // ---- 工具方法 ----

    private static string? GetString(JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() : null;
    }

    /// <summary>读取 ID 类字段（QQ 号/群号/message_id 等）：OneBot v11 是数字，扩展实现可能是字符串</summary>
    private static string? GetId(JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(property, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetInt64().ToString();
            if (val.ValueKind == JsonValueKind.String) return val.GetString();
        }
        return null;
    }

    private static long? GetInt64(JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(property, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetInt64();
            if (val.ValueKind == JsonValueKind.String && long.TryParse(val.GetString(), out var n)) return n;
        }
        return null;
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2}GB"
    };
}
