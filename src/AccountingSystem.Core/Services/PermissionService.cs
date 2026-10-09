using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// دسترسی کاربران به شعبه‌ها، نقش‌ها و کارها. هر بررسی وضعیت تازه‌ی دیتابیس را می‌خواند،
/// پس تغییر عضویت یا نقش بلافاصله اعمال می‌شود.
/// </summary>
public sealed class PermissionService
{
    private readonly IAccountingRepository _repository;

    public PermissionService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    /// <summary>دسترسی تازه‌ی کاربر جاری از دیتابیس.</summary>
    public async Task<UserAccess> GetAccessAsync(CurrentUser user, CancellationToken ct = default)
    {
        var access = await _repository.GetUserAccessAsync(user.Id, ct);
        if (access is null)
        {
            throw new BusinessRuleException("کاربر جاری در دیتابیس پیدا نشد. دوباره وارد شوید.");
        }
        return access;
    }

    /// <summary>کار را در شعبه‌ی داده‌شده انجام می‌دهد یا خطای فارسی می‌دهد.</summary>
    public async Task RequireAsync(CurrentUser user, Permission permission, int branchId, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(user, ct);
        PermissionRules.Require(access, permission, branchId);
    }

    public async Task<bool> HasAsync(CurrentUser user, Permission permission, int branchId, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(user, ct);
        return PermissionRules.IsAllowed(access, permission, branchId);
    }

    /// <summary>آیا کاربر این کار را در حداقل یک شعبه دارد؟ (برای نمایش فرم‌ها)</summary>
    public async Task<bool> HasAnyAsync(CurrentUser user, Permission permission, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(user, ct);
        return PermissionRules.IsAllowedSomewhere(access, permission);
    }

    public async Task RequireReadAsync(CurrentUser user, int branchId, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(user, ct);
        PermissionRules.RequireRead(access, branchId);
    }

    /// <summary>شعبه‌ای که گزارش باید برای آن ساخته شود؛ برای مدیر null یعنی همه‌ی شعبه‌ها.</summary>
    public async Task<int?> ResolveReadBranchAsync(CurrentUser user, int? requested, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(user, ct);
        return PermissionRules.ResolveReadBranch(access, requested);
    }

    /// <summary>
    /// شعبه‌هایی که کاربر در فهرست‌ها می‌بیند. اگر permission داده شود، فقط شعبه‌هایی که آن کار را در آن‌ها دارد.
    /// </summary>
    public async Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CurrentUser user, Permission? permission = null, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(user, ct);
        var branches = await _repository.GetBranchesAsync(ct);
        return branches
            .Where(b => permission is { } p
                ? PermissionRules.IsAllowed(access, p, b.Id)
                : PermissionRules.CanRead(access, b.Id))
            .ToList();
    }

    /// <summary>دسترسی همه‌ی کاربران (فقط مدیر).</summary>
    public async Task<IReadOnlyDictionary<int, UserAccess>> GetAllAccessAsync(CurrentUser actor, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return await _repository.GetAllUserAccessAsync(ct);
    }

    /// <summary>نقش‌های یک شعبه (فقط مدیر).</summary>
    public async Task<IReadOnlyList<AccessRoleInfo>> GetRolesAsync(CurrentUser actor, int branchId, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return await _repository.GetRolesAsync(branchId, ct);
    }

    /// <summary>
    /// عضویت‌ها و شعبه‌ی اصلی کاربر را یک‌جا تنظیم می‌کند (فقط مدیر).
    /// roleByBranch: برای هر شعبه، نقش آن شعبه یا null (یعنی عضو نباشد).
    /// ترتیب اعمال: ابتدا عضویت‌های جدید و تغییرکرده، بعد شعبه‌ی اصلی، و در آخر حذف عضویت‌ها.
    /// </summary>
    public async Task ApplyMembershipsAsync(
        CurrentUser actor,
        int userId,
        IReadOnlyDictionary<int, int?> roleByBranch,
        int? defaultBranchId,
        DateTime now,
        CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var target = await _repository.GetUserAccessAsync(userId, ct)
            ?? throw new BusinessRuleException("کاربر انتخاب‌شده پیدا نشد.");
        PermissionRules.EnsureAssignable(target);

        var finalBranches = new HashSet<int>(target.Branches.Keys);
        foreach (var entry in roleByBranch)
        {
            if (entry.Value is null)
            {
                finalBranches.Remove(entry.Key);
            }
            else
            {
                finalBranches.Add(entry.Key);
            }
        }

        var effectiveDefault = defaultBranchId ?? target.DefaultBranchId;
        if (effectiveDefault is { } chosenDefault && !finalBranches.Contains(chosenDefault))
        {
            throw new BusinessRuleException("شعبه‌ی اصلی کاربر را حذف کرده‌اید. اول شعبه‌ی اصلی دیگری انتخاب کنید.");
        }

        foreach (var entry in roleByBranch)
        {
            if (entry.Value is not { } roleId)
            {
                continue;
            }
            var current = target.Branches.TryGetValue(entry.Key, out var existing) ? existing.RoleId : (int?)null;
            if (current != roleId)
            {
                await _repository.SetMembershipAsync(userId, entry.Key, roleId, actor.Id, now, ct);
            }
        }

        if (defaultBranchId is { } newDefault && newDefault != target.DefaultBranchId)
        {
            await _repository.SetDefaultBranchAsync(userId, newDefault, actor.Id, now, ct);
        }

        foreach (var entry in roleByBranch)
        {
            if (entry.Value is null && target.Branches.ContainsKey(entry.Key))
            {
                await _repository.SetMembershipAsync(userId, entry.Key, null, actor.Id, now, ct);
            }
        }
    }

    /// <summary>نقش تازه برای یک شعبه با فهرست دسترسی‌ها (فقط مدیر). فهرست خالی یعنی نقش فقط‌خواندنی.</summary>
    public async Task<int> CreateRoleAsync(
        CurrentUser actor,
        int branchId,
        string name,
        IReadOnlyCollection<Permission> permissions,
        DateTime now,
        CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        if (branchId <= 0)
        {
            throw new BusinessRuleException("شعبه را انتخاب کنید.");
        }
        var cleanName = PermissionRules.ValidateRoleName(name);
        return await _repository.CreateRoleAsync(branchId, cleanName, NormalizePermissions(permissions), actor.Id, now, ct);
    }

    public async Task SetRolePermissionsAsync(
        CurrentUser actor,
        int roleId,
        IReadOnlyCollection<Permission> permissions,
        DateTime now,
        CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        await _repository.SetRolePermissionsAsync(roleId, NormalizePermissions(permissions), actor.Id, now, ct);
    }

    /// <summary>حذف نقش؛ اگر به کاربری داده شده باشد، مخزن خطا می‌دهد.</summary>
    public async Task DeleteRoleAsync(CurrentUser actor, int roleId, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        await _repository.DeleteRoleAsync(roleId, actor.Id, now, ct);
    }

    private static IReadOnlyCollection<Permission> NormalizePermissions(IEnumerable<Permission> permissions) =>
        permissions.Distinct().OrderBy(p => (int)p).ToArray();
}
