using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

public static class RoleGuard
{
    public static void RequireAdmin(CurrentUser user)
    {
        if (user.Role != UserRole.Admin)
        {
            throw new BusinessRuleException("این عملیات فقط برای مدیر سیستم مجاز است.");
        }
    }
}
