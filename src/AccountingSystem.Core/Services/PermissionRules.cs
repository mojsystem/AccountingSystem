using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// قواعد دسترسی به‌صورت تابع خالص: مدیر همیشه مجاز است؛ کاربر غیرفعال هیچ‌کاری نمی‌کند؛
/// کاربر صندوق فقط روی اسناد شعبه‌ی خودش و فقط با دسترسی داده‌شده.
/// </summary>
public static class PermissionRules
{
    public static bool IsAllowed(UserAccess access, Permission permission, int documentBranchId)
    {
        if (!access.IsActive)
        {
            return false;
        }
        if (access.Role == UserRole.Admin)
        {
            return true;
        }
        return access.BranchId == documentBranchId && access.Permissions.Contains(permission);
    }

    public static void Require(UserAccess access, Permission permission, int documentBranchId)
    {
        if (!access.IsActive)
        {
            throw new BusinessRuleException("حساب کاربری شما غیرفعال است.");
        }
        if (access.Role == UserRole.Admin)
        {
            return;
        }
        if (access.BranchId != documentBranchId)
        {
            throw new BusinessRuleException("این سند متعلق به شعبه‌ی دیگری است و برای کاربر جاری قابل تغییر نیست.");
        }
        if (!access.Permissions.Contains(permission))
        {
            throw new BusinessRuleException(
                $"برای این کار دسترسی «{PermissionCodes.DisplayName(permission)}» لازم است. از مدیر سیستم بخواهید این دسترسی را به حساب شما بدهد.");
        }
    }

    /// <summary>مدیر به همه‌ی دسترسی‌ها دسترسی دارد؛ برای او دسترسی اضافی تنظیم نمی‌شود.</summary>
    public static void EnsureAssignable(UserAccess target)
    {
        if (target.Role == UserRole.Admin)
        {
            throw new BusinessRuleException("مدیر به همه‌ی دسترسی‌ها دسترسی دارد و نیازی به تنظیم ندارد.");
        }
    }
}
