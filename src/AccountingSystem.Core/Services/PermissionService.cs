using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// مدیریت دسترسی‌های ویرایش و ابطال. اعطای دسترسی فقط توسط مدیر انجام می‌شود.
/// </summary>
public sealed class PermissionService
{
    private readonly IAccountingRepository _repository;

    public PermissionService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    /// <summary>کاربر باید دسترسی را روی سندی از همان شعبه داشته باشد؛ مدیر همیشه مجاز است.</summary>
    public async Task RequireAsync(CurrentUser user, Permission permission, int documentBranchId, CancellationToken ct = default)
    {
        var access = await LoadAccessAsync(user, ct);
        PermissionRules.Require(access, permission, documentBranchId);
    }

    public async Task<bool> HasAsync(CurrentUser user, Permission permission, int documentBranchId, CancellationToken ct = default)
    {
        var access = await LoadAccessAsync(user, ct);
        return PermissionRules.IsAllowed(access, permission, documentBranchId);
    }

    /// <summary>
    /// همه‌ی دسترسی‌های فعال کاربر (برای نمایش دکمه‌ها). مدیر همه‌ی دسترسی‌ها را دارد؛ کاربر غیرفعال هیچ‌کدام را ندارد.
    /// </summary>
    public async Task<IReadOnlySet<Permission>> GetPermissionsAsync(CurrentUser user, CancellationToken ct = default)
    {
        var access = await LoadAccessAsync(user, ct);
        if (!access.IsActive)
        {
            return new HashSet<Permission>();
        }
        return access.Role == UserRole.Admin
            ? new HashSet<Permission>(Enum.GetValues<Permission>())
            : access.Permissions;
    }

    /// <summary>دسترسی‌های اضافی همه‌ی کاربران (فقط مدیر).</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlySet<Permission>>> GetAssignmentsAsync(CurrentUser actor, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return await _repository.GetAllUserPermissionsAsync(ct);
    }

    /// <summary>
    /// تنظیم دسترسی‌های اضافی یک کاربر صندوق (فقط مدیر). مجموعه‌ی داده‌شده جایگزین مجموعه‌ی قبلی می‌شود.
    /// </summary>
    public async Task SetPermissionsAsync(CurrentUser actor, int userId, IEnumerable<Permission> permissions, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var target = await _repository.GetUserAccessAsync(userId, ct)
            ?? throw new BusinessRuleException("کاربر انتخاب‌شده یافت نشد.");
        PermissionRules.EnsureAssignable(target);
        await _repository.SetUserPermissionsAsync(userId, permissions.Distinct().ToList(), actor.Id, now, ct);
    }

    private async Task<UserAccess> LoadAccessAsync(CurrentUser user, CancellationToken ct) =>
        await _repository.GetUserAccessAsync(user.Id, ct)
        ?? throw new BusinessRuleException("حساب کاربری شما یافت نشد.");
}
