using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// قواعد دسترسی به شعبه‌ها: مدیر به همه‌ی شعبه‌ها دسترسی دارد؛ کاربر صندوق فقط به شعبه‌ی خودش.
/// </summary>
public static class BranchScope
{
    /// <summary>
    /// شعبه‌ی قابل استفاده برای خواندن گزارش. null یعنی همه‌ی شعبه‌ها (فقط مدیر).
    /// </summary>
    public static int? ResolveForReport(CurrentUser user, int? requestedBranchId)
    {
        if (user.Role == UserRole.Admin)
        {
            return requestedBranchId;
        }

        var ownBranch = user.BranchId ?? throw new BusinessRuleException("برای این کاربر شعبه‌ای تعریف نشده است.");
        if (requestedBranchId.HasValue && requestedBranchId.Value != ownBranch)
        {
            throw new BusinessRuleException("دسترسی به اطلاعات این شعبه برای کاربر جاری مجاز نیست.");
        }
        return ownBranch;
    }

    /// <summary>
    /// شعبه‌ی عملیات ثبتی (معامله، نرخ، موجودی). کاربر صندوق فقط در شعبه‌ی خودش می‌تواند ثبت کند.
    /// </summary>
    public static int RequireBranch(CurrentUser user, int branchId)
    {
        if (branchId <= 0)
        {
            throw new BusinessRuleException("شعبه را انتخاب کنید.");
        }
        if (user.Role == UserRole.Admin)
        {
            return branchId;
        }
        if (user.BranchId != branchId)
        {
            throw new BusinessRuleException("ثبت اطلاعات برای این شعبه برای کاربر جاری مجاز نیست.");
        }
        return branchId;
    }
}
