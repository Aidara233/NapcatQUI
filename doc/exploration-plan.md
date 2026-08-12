# NapcatQUI 探索计划

> 2026-07-28 | 探索阶段 — 暂不实现具体功能，理清方向后动手

---

## 一、NapCat API 能力全景

### 1.1 通信模式

| 模式 | NapCat 角色 | 适用场景 |
|------|------------|---------|
| HTTP Server | 被动接收 API 调用 | 请求-响应（不适合实时消息） |
| HTTP Client | 主动推送事件 | 事件回调（需公网可达端点） |
| **正向 WebSocket** | 双向双工 | 主连接方式：事件推送 + API 调用 |
| **反向 WebSocket** | 双向双工 | NapCat 主动连接客户端 |

**主连接方式**：正向 WebSocket（客户端连接 NapCat），辅以 HTTP API 作为降级通道。单 WebSocket 承载双向通信，简单可靠。

### 1.2 API 分类

#### OneBot v11 标准 API（必实现）

```
消息 — 发/撤/查：
  send_private_msg        — 发送私聊
  send_group_msg          — 发送群聊
  send_msg                — 统一发送
  delete_msg              — 撤回消息
  get_msg                 — 获取单条消息
  get_forward_msg         — 获取合并转发

群管理：
  set_group_kick          — 踢人
  set_group_ban           — 禁言
  set_group_whole_ban     — 全员禁言
  set_group_admin         — 设置管理员
  set_group_card          — 设置群名片
  set_group_name          — 设置群名
  set_group_leave         — 退群
  set_group_special_title — 群头衔 ⭐

信息查询：
  get_login_info, get_stranger_info, get_friend_list
  get_group_info, get_group_list, get_group_member_info, get_group_member_list
  get_group_honor_info
  get_cookies, get_csrf_token, get_credentials
  get_record, get_image
  can_send_image, can_send_record
  get_status, get_version_info

请求处理：
  set_friend_add_request, set_group_add_request

其他：
  send_like
```

#### NapCat 扩展 API（优先支持）

```
消息历史与增强 ⭐⭐⭐：
  get_group_msg_history    — 拉群历史
  get_friend_msg_history   — 拉私聊历史
  mark_msg_as_read         — 标记已读
  set_msg_emoji_like       — 表情回应
  send_forward_msg         — 合并转发（自定义内容）
  group_poke / friend_poke — 戳一戳

群文件：
  upload_group_file, delete_group_file
  create_group_file_folder, delete_group_folder
  get_group_file_system_info, get_group_file_url, get_group_files_by_folder
  get_group_root_files

群扩展：
  get_group_at_all_remain  — @全体剩余次数
  get_group_essence_list, set_essence_msg, delete_essence_msg
  send_group_notice, get_group_notice
  get_group_system_msg, get_group_ban_list
  set_group_sign, set_group_anonymous, set_group_anonymous_ban

个人/系统：
  set_qq_profile, set_qq_avatar, set_online_status
  get_friends_with_category
  upload_private_file
  AiVoiceSend
```

### 1.3 事件分类

```
message:
  private (friend/group/other)  — 私聊消息
  group (normal/anonymous/notice) — 群聊消息

notice:
  group_upload, group_admin, group_decrease, group_increase
  group_ban, group_card, group_recall
  friend_add, friend_recall
  offline_file, client_status
  essence, notify (NapCat 扩展)

request:
  friend — 好友申请
  group  — 加群/邀请

meta_event:
  lifecycle — NapCat 启停/连接状态
  heartbeat — 心跳
```

---

## 二、协议适配层

### 2.1 为什么需要独立适配层

即使不做前后端分离，也值得把 NapCat 通信拆成独立的一层：

1. **API 版本隔离**：NapCat 字段变更、扩展接口增减，只影响适配层，不波及 UI
2. **多账号天然支持**：每个账号独立连接、独立消息流、独立重连。适配层管理这些，UI 只关心"当前展示哪个账号"
3. **数据完整性**：NapCat 本身不持久化消息，客户端必须自己存。适配层收到消息后立即入库，UI 从本地 DB 读

```
NapCat <--WebSocket--> [适配层] <--领域事件--> [Core/UI]
                            │
                      SQLite (持久化)
```

适配层负责：连接、协议解析、请求/响应匹配、事件分发、重连。Core 负责：领域模型、数据存储、业务逻辑。UI 负责：展示与交互。

### 2.2 领域模型

```
Account（账号）
  ├── Uin: string
  ├── Nickname: string
  ├── ConnectionState: Disconnected | Connecting | Connected | Reconnecting
  ├── NapCatWsUrl: string
  └── AccessToken: string?

Contact（好友）
  ├── UserId: string
  ├── Nickname, Remark
  ├── Category (好友分组)
  └── AvatarUrl

Group（群）
  ├── GroupId, Name
  ├── MemberCount, MaxMemberCount
  └── SelfRole: Owner | Admin | Member

GroupMember（群成员）
  ├── GroupId, UserId
  ├── Card (群名片), Role
  ├── SpecialTitle (群头衔) ⭐, TitleExpireTime
  └── JoinTime, LastSpeakTime

Message（消息）
  ├── MessageId: string (NapCat 原始 ID)
  ├── AccountId (归属账号)
  ├── Type: Private | Group | System
  ├── SubType: Normal | Anonymous | Notice
  ├── SenderId, TargetId
  ├── Content (纯文本摘要，用于搜索)
  ├── Segments: List<MessageSegment> (富文本结构)
  ├── ReplyToId?, Timestamp, IsSentBySelf
  └── RawJson (原始报文，保留未识别字段)

MessageSegment（消息段）
  ├── Type: Text | Image | At | Reply | Face | Record | Video | File | ...
  └── Data: Dictionary<string, object>
```

### 2.3 连接管理

每个账号一个 `AccountSession`，`AccountManager` 统一调度：

```
AccountSession
  ├── ClientWebSocket (单连接双向复用)
  │     ├── 收：API 响应 (echo 匹配) + 事件推送
  │     └── 发：API 请求 (UUID echo, 20s 超时)
  ├── HTTP 降级 (WebSocket 不可用时)
  ├── Echo 字典 (ConcurrentDictionary, 超时清理)
  ├── 事件路由 (订阅者模式，推给本地消费者)
  └── 状态机：Disconnected → Connecting → Connected → Reconnecting
```

- 每个 session 独立 async loop，互不干扰
- 重连策略：指数退避，1s → 2s → 4s → ... → 30s 上限
- UI 的"切换账号"只改变视图焦点，底层连接保持
- AccountManager 支持运行时增删账号（添加/移除连接）

---

## 三、项目结构

```
NapcatQUI/
├── NapcatQUI.Client/              # Avalonia UI
│   ├── Views/                     # .axaml 页面
│   │   ├── MainWindow
│   │   ├── ChatView               # 聊天会话
│   │   ├── ContactListView        # 好友+群列表
│   │   ├── GroupMemberListView    # 群成员
│   │   ├── SearchView             # 消息搜索
│   │   └── SettingsView           # 设置
│   ├── ViewModels/
│   ├── Controls/                  # 聊天气泡、消息段渲染器等
│   └── Services/                  # IPC 客户端 (连 Core)
│
├── NapcatQUI.Core/                # 核心（无 UI 依赖，可独立运行）
│   ├── Adapter/                   # NapCat 通信层
│   │   ├── NapCatConnection       # WebSocket + HTTP
│   │   ├── AccountSession         # 单账号会话
│   │   ├── AccountManager         # 多账号调度
│   │   └── OneBotMessageParser    # 消息/事件 JSON → 领域模型
│   ├── Models/                    # 领域模型
│   ├── Database/
│   │   ├── DatabaseManager        # SQLite 初始化和迁移
│   │   ├── Repositories/          # 每实体一个 Repository
│   │   └── Entities/              # 数据库实体
│   ├── Services/
│   │   ├── ContactSyncService     # 联系人/群/成员同步
│   │   ├── HistoryService         # 消息历史查询
│   │   └── MediaCacheService      # 图片/文件本地缓存
│   ├── Events/                    # 领域事件
│   └── Host/                      # Generic Host 启动配置
│
├── NapcatQUI.sln
└── doc/
    ├── goal.txt
    └── exploration-plan.md
```

**设计要点**：
- Core 层零 UI 依赖，可以在控制台下 headless 运行（方便开发调试）
- Client 通过 IPC（localhost HTTP / named pipe）连 Core，不直连 NapCat
- Core 启动 = 自动连接所有已启用账号，Client 关闭时是否保持连接可配置
- 初期 Client 和 Core 可以合在同一进程运行（减少 IPC 复杂度），但代码上保持分层，以后拆分成本低

---

## 四、数据存储

### 4.1 目录规划

```
%LOCALAPPDATA%\NapcatQUI\
├── napcatqui.db              # 主库（SQLite WAL）
├── accounts\{uin}\
│   ├── media\
│   │   ├── images\           # 图片缓存
│   │   ├── records\          # 语音
│   │   └── files\            # 文件
│   └── logs\                 # 该账号连接日志
├── cache\                    # 跨账号共享（头像等）
└── config.json               # 账号列表、端点、UI 偏好
```

**红线**：不在程序目录、文档目录、桌面、临时目录写文件。数据库只此一份。

### 4.2 核心表

```sql
Account (Id, Uin UNIQUE, Nickname, NapCatWsUrl, AccessToken, IsEnabled, LastConnectedAt)
Contact (Id, AccountId FK, UserId, Nickname, Remark, AvatarLocalPath, Category)
Group_  (Id, AccountId FK, GroupId, Name, MemberCount, MaxMemberCount, AvatarLocalPath)
GroupMember (Id, GroupId FK, UserId, Nickname, Card, Role, SpecialTitle, TitleExpireTime)
Message (Id, AccountId FK, MessageId, Type, SubType, SenderId, TargetId,
         Content, SegmentsJson, ReplyToId, IsSentBySelf, Timestamp,
         UNIQUE(AccountId, MessageId))
FileRecord (Id, AccountId FK, FileId, Name, Size, Url, LocalPath, Source)
```

消息查重靠 `UNIQUE(AccountId, MessageId)`；同一条消息不会重复入库。

### 4.3 全文搜索

```sql
CREATE VIRTUAL TABLE message_fts USING fts5(content, sender_name, target_name,
    content='Message', content_rowid='Id');
```

触发器保持 FTS 索引与 Message 表同步。中文分词初期用 NGRAM 凑合，后期可挂 jieba。

---

## 五、技术选型

| 层 | 选型 | 理由 |
|----|------|------|
| UI | Avalonia 11.x + CommunityToolkit.Mvvm | 跨平台、原生性能、MVVM 支持好 |
| 核心服务 | .NET 8 Generic Host | `IHostedService` 管理长生命周期连接 |
| 数据库 | SQLite (sqlite-net-pcl) + FTS5 | 轻量无服务、WAL 模式读写并发好 |
| WebSocket | `System.Net.WebSockets.ClientWebSocket` | 内置零依赖 |
| JSON | `System.Text.Json` | 原生高性能，自定义 converter 处理 OneBot 混合数组 |
| 图片 | SkiaSharp | 缩略图、头像裁剪 |
| 日志 | `Microsoft.Extensions.Logging` + Serilog | 文件+控制台 |

**不用**：Electron（太重）、MAUI（桌面不如 Avalonia 成熟）、第三方 WS 库（内置足够）、Blazor（这是桌面客户端，不是 Web 管理面板）。

---

## 六、分期路线

### Phase 0 — 协议原型（当前）

```
目标：验证 NapCat 通信在 .NET 下的可行性，确认没有阻塞性技术风险
产出：一个可以连接 NapCat、收发 API、打印事件的控制台程序
```

- [ ] `NapCatConnection` 原型：WebSocket 连接、握手、Echo 请求/响应
- [ ] OneBot 消息/事件 JSON → C# 对象反序列化
- [ ] 验证 NapCat 多客户端并发连接（两路 WebSocket 同时连同一个 NapCat 实例）
- [ ] 验证 `get_group_msg_history` / `get_friend_msg_history` 返回格式
- [ ] 确认 NapCat Docker 端口暴露、access_token 配置

### Phase 1 — 核心骨架

```
目标：消息能收能发能存，多账号连接管理跑通
产出：可独立运行的 Core 服务 + 控制台测试入口
```

- [ ] `AccountSession` 完整状态机 + 重连
- [ ] `AccountManager` 多账号并发
- [ ] 数据库建表 + Message/Contact/Group Repository
- [ ] 消息流水线：收到 → 解析 → 去重 → 入库 → 事件发布
- [ ] 消息发送 + Echo 匹配 + 超时处理
- [ ] 启动时联系人/群列表全量同步

### Phase 2 — 基础 UI

```
目标：能看消息、能发消息，基础的 QQ 体验跑通
产出：Avalonia 桌面客户端，私聊 + 群聊可用
```

- [ ] Avalonia 项目搭建 + MVVM + IPC 连接 Core
- [ ] 账号管理界面（添加/启用/禁用/连接状态）
- [ ] 联系人列表（好友+群，搜索过滤）
- [ ] 私聊会话（文本+图片气泡、时间线、未读标记）
- [ ] 群聊会话（@提及高亮、群名片显示）
- [ ] 消息输入 + 发送（文本 + 图片粘贴/拖拽）

### Phase 3 — 消息完整性

```
目标：接近原生 QQ 的消息体验
```

- [ ] 启动/切会话时拉历史消息补全
- [ ] 消息段完整渲染（回复引用、表情、语音、文件、合并转发）
- [ ] 图片/文件下载 + 缩略图 + 缓存淘汰
- [ ] FTS5 全文搜索（搜索消息内容/发送者）
- [ ] 消息撤回处理
- [ ] 右键菜单（复制、撤回、回复、转发）

### Phase 4 — 群功能

```
目标：群管理的常用操作全部覆盖
```

- [ ] 群成员列表 + 角色图标 + 群头衔显示 ⭐
- [ ] 群名片查看/修改
- [ ] 群禁言/全员禁言/踢人
- [ ] 群管理员设置
- [ ] 群公告查看
- [ ] 群文件浏览/下载
- [ ] @全体成员（含次数限制提示）
- [ ] 群荣誉显示（龙王/群聊之火等）

### Phase 5 — 完善与打磨

- [ ] 通知系统（好友申请、加群邀请、文件上传提醒）
- [ ] 头像缓存与更新
- [ ] 设置页面（账号、通知、缓存、快捷键、外观）
- [ ] 消息通知（系统托盘、任务栏闪烁）
- [ ] 性能优化（虚拟化列表、图片懒加载、DB 索引）
- [ ] 打包与自动更新

---

## 七、待验证的风险点

| 风险 | 验证方式 | 影响 |
|------|---------|------|
| NapCat 多 WebSocket 客户端并发 | Phase 0 实际测试 | 影响是否需要实现代理模式 |
| OneBot `message` 数组序列化 | S.T.Json 自定义 converter 原型 | 混合类型数组（文本+图片+At 交替）需要特殊处理 |
| ClientWebSocket 长连接稳定性 | 24h+ 压测 | 可能需要 KeepAlive 心跳 |
| Avalonia 虚拟化聊天气泡性能 | 虚拟化列表原型 | 大量消息时滚动流畅度 |
| FTS5 中文分词 | 中文搜索测试 | 可能需 NGRAM 或 jieba |
| NapCat API 字段稳定性 | 查阅版本 changelog | 适配层需要版本兼容逻辑 |
| 多账号内存占用 | 2-3 账号 1h 监控 | 预估每账号 5-15MB 常驻 |
| `.axaml` 与普通控件的复杂消息段渲染 | 聊天气泡原型 | 图文混排可能比预期复杂 |

---

## 八、暂不纳入本轮的问题

这些是设计时有意识地放下的，记录下来防止以后忘掉：

- **AgentLilara 集成**：方向是为 NapcatQUI 预留一个可选的 IPC/事件流接口，让外部程序能订阅消息流和发送消息。具体协议、认证、能力边界等 Lilara 那边有明确需求再说。目前 NapcatQUI 只做自己的 QQ 客户端。
- **语音/视频通话**：NapCat 没有这类接口，不在此项目范围。
- **QQ 空间/动态/钱包/游戏**：无接口或非核心需求，不做。
- **Linux 适配**：.NET 8 + Avalonia 在技术上是跨平台的，但初期只在 Windows 上验证。Linux 适配放到后面。
- **插件系统**：初期不需要，但 Core 层的事件总线架构天然支持后续扩展。
- **端到端加密**：NapCat 到 QQ 服务器的通道由 Tencent 协议保护，本地数据存明文（个人机器，不过度设计）。
