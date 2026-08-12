namespace NapcatQUI.Core.Services;

/// <summary>
/// 头像 URL 换算 — 把 QQ 号 / 群号转成腾讯图床头像地址。
/// 好友/成员：q1.qlogo.cn；群头像：p.qlogo.cn。下载缓存走 ImageCacheService。
/// </summary>
public static class AvatarService
{
    public static string UserAvatarUrl(string qq) =>
        $"https://q1.qlogo.cn/g?b=qq&nk={qq}&s=100";

    public static string GroupAvatarUrl(string groupId) =>
        $"https://p.qlogo.cn/gh/{groupId}/{groupId}/100";
}
