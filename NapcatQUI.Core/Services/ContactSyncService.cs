using NapcatQUI.Core.Database.Entities;
using NapcatQUI.Core.Database.Repositories;

namespace NapcatQUI.Core.Services;

/// <summary>
/// 联系人/群信息同步服务 — 拉取并本地缓存
/// </summary>
public class ContactSyncService
{
    private readonly ContactRepository _contactRepo;
    private readonly GroupRepository _groupRepo;

    public ContactSyncService(ContactRepository contactRepo, GroupRepository groupRepo)
    {
        _contactRepo = contactRepo;
        _groupRepo = groupRepo;
    }

    public async Task<List<ContactEntity>> GetFriendsAsync(string accountId)
    {
        return await _contactRepo.GetFriendsAsync(accountId);
    }

    public async Task<List<GroupEntity>> GetGroupsAsync(string accountId)
    {
        return await _groupRepo.GetGroupsAsync(accountId);
    }

    public async Task<List<GroupMemberEntity>> GetGroupMembersAsync(string groupId)
    {
        return await _groupRepo.GetMembersAsync(groupId);
    }

    public async Task<ContactEntity?> GetFriendAsync(string accountId, string userId)
    {
        return await _contactRepo.GetAsync(accountId, userId);
    }

    public async Task<GroupEntity?> GetGroupAsync(string accountId, string groupId)
    {
        return await _groupRepo.GetAsync(accountId, groupId);
    }
}
