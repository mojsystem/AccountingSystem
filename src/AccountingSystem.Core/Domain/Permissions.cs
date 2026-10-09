namespace AccountingSystem.Core.Domain;

/// <summary>
/// دسترسی‌های ویرایش و ابطال. مدیر همه‌ی آن‌ها را دارد. مدیر به کاربران صندوق دسترسی می‌دهد.
/// کاربر صندوق فقط دسترسی‌های داده‌شده را دارد و فقط روی اسناد شعبه‌ی خودش.
/// </summary>
public enum Permission
{
    TradeEdit,
    TradeVoid,
    OpeningEdit,
    OpeningVoid,
    ManualEdit,
    ManualVoid,
}

public static class PermissionCodes
{
    public static string ToCode(Permission permission) => permission switch
    {
        Permission.TradeEdit => "TRADE_EDIT",
        Permission.TradeVoid => "TRADE_VOID",
        Permission.OpeningEdit => "OPENING_EDIT",
        Permission.OpeningVoid => "OPENING_VOID",
        Permission.ManualEdit => "MANUAL_EDIT",
        Permission.ManualVoid => "MANUAL_VOID",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null),
    };

    /// <summary>کد ذخیره‌شده‌ی یک دسترسی را به مقدار enum تبدیل می‌کند؛ کد ناشناخته پذیرفته نمی‌شود.</summary>
    public static bool TryParse(string? code, out Permission permission)
    {
        foreach (var candidate in Enum.GetValues<Permission>())
        {
            if (string.Equals(ToCode(candidate), code, StringComparison.Ordinal))
            {
                permission = candidate;
                return true;
            }
        }
        permission = default;
        return false;
    }

    public static string DisplayName(Permission permission) => permission switch
    {
        Permission.TradeEdit => "ویرایش معامله",
        Permission.TradeVoid => "ابطال معامله",
        Permission.OpeningEdit => "ویرایش موجودی افتتاحیه",
        Permission.OpeningVoid => "ابطال موجودی افتتاحیه",
        Permission.ManualEdit => "ویرایش سند دستی",
        Permission.ManualVoid => "ابطال سند دستی",
        _ => permission.ToString(),
    };
}

/// <summary>
/// وضعیت دسترسی کاربر از دیتابیس. نقش، فعال بودن و شعبه همان لحظه خوانده می‌شوند،
/// پس تغییر دسترسی یا غیرفعال شدن کاربر بلافاصله اعمال می‌شود (نه فقط در ورود بعدی).
/// </summary>
public sealed record UserAccess(int UserId, UserRole Role, bool IsActive, int? BranchId, IReadOnlySet<Permission> Permissions);
