using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// قواعد دسترسی به‌صورت تابع خالص. مدیر سیستم همه‌چیز را دارد. کاربر دیگر فقط در شعبه‌ای که عضوش است
/// و فقط برای کارهایی که نقشش در آن شعبه می‌دهد مجاز است. کاربر غیرفعال هیچ‌کاری نمی‌تواند.
/// </summary>
public static class PermissionRules
{
    private const string NotMemberMessage = "کاربر جاری عضو این شعبه نیست و به اطلاعات و کارهای آن دسترسی ندارد.";

    /// <summary>آیا کاربر اطلاعات این شعبه را می‌بیند؟ (عضو شعبه یا مدیر سیستم)</summary>
    public static bool CanRead(UserAccess access, int branchId)
    {
        if (!access.IsActive)
        {
            return false;
        }
        return access.Role == UserRole.Admin || access.Branches.ContainsKey(branchId);
    }

    /// <summary>آیا کاربر این کار را در این شعبه می‌تواند انجام دهد؟</summary>
    public static bool IsAllowed(UserAccess access, Permission permission, int branchId)
    {
        if (!access.IsActive)
        {
            return false;
        }
        if (access.Role == UserRole.Admin)
        {
            return true;
        }
        return access.Branches.TryGetValue(branchId, out var branch) && branch.Permissions.Contains(permission);
    }

    /// <summary>آیا کاربر این کار را در حداقل یک شعبه دارد؟ (برای نشان دادن فرم‌ها و دکمه‌ها)</summary>
    public static bool IsAllowedSomewhere(UserAccess access, Permission permission)
    {
        if (!access.IsActive)
        {
            return false;
        }
        if (access.Role == UserRole.Admin)
        {
            return true;
        }
        return access.Branches.Values.Any(branch => branch.Permissions.Contains(permission));
    }

    public static void RequireRead(UserAccess access, int branchId)
    {
        EnsureActive(access);
        if (!CanRead(access, branchId))
        {
            throw new BusinessRuleException(NotMemberMessage);
        }
    }

    public static void Require(UserAccess access, Permission permission, int branchId)
    {
        EnsureActive(access);
        if (access.Role == UserRole.Admin)
        {
            return;
        }
        if (!access.Branches.ContainsKey(branchId))
        {
            throw new BusinessRuleException(NotMemberMessage);
        }
        if (!IsAllowed(access, permission, branchId))
        {
            throw new BusinessRuleException(
                $"برای این کار دسترسی «{PermissionCodes.DisplayName(permission)}» در این شعبه لازم است. از مدیر سیستم بخواهید نقش شما را در این شعبه تنظیم کند.");
        }
    }

    /// <summary>
    /// شعبه‌ی قابل استفاده برای گزارش. مدیر سیستم می‌تواند null (همه‌ی شعبه‌ها) بخواهد.
    /// کاربر دیگر اگر شعبه‌ای نخواهد، شعبه‌ی اصلی‌اش را می‌بیند؛ اگر شعبه‌ای بخواهد که عضوش نیست، رد می‌شود.
    /// </summary>
    public static int? ResolveReadBranch(UserAccess access, int? requested)
    {
        EnsureActive(access);
        if (access.Role == UserRole.Admin)
        {
            return requested;
        }
        if (requested is { } branchId)
        {
            RequireRead(access, branchId);
            return branchId;
        }
        if (access.DefaultBranchId is { } defaultBranch && access.Branches.ContainsKey(defaultBranch))
        {
            return defaultBranch;
        }
        if (access.Branches.Count == 0)
        {
            throw new BusinessRuleException("برای این کاربر هیچ شعبه‌ای تعریف نشده است.");
        }
        return access.Branches.Keys.Min();
    }

    /// <summary>مدیر سیستم به همه‌ی شعبه‌ها دسترسی دارد؛ برای او نقش شعبه‌ای تعریف نمی‌شود.</summary>
    public static void EnsureAssignable(UserAccess target)
    {
        if (target.Role == UserRole.Admin)
        {
            throw new BusinessRuleException("مدیر سیستم به همه‌ی شعبه‌ها و کارها دسترسی دارد و برایش نقش شعبه‌ای تعریف نمی‌شود.");
        }
    }

    public static string ValidateRoleName(string? name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length < 2 || clean.Length > 60)
        {
            throw new BusinessRuleException("نام نقش باید بین ۲ تا ۶۰ کاراکتر باشد.");
        }
        return clean;
    }

    private static void EnsureActive(UserAccess access)
    {
        if (!access.IsActive)
        {
            throw new BusinessRuleException("حساب کاربری شما غیرفعال است.");
        }
    }
}
