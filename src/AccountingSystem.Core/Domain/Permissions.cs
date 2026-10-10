namespace AccountingSystem.Core.Domain;

/// <summary>
/// کارهایی که مدیر جدا به هر نقش در هر شعبه می‌دهد. مدیر سیستم همه‌ی آن‌ها را در همه‌ی شعبه‌ها دارد.
/// </summary>
public enum Permission
{
    TradeRecord,
    TradeEdit,
    TradeVoid,
    OpeningCreate,
    OpeningEdit,
    OpeningVoid,
    ManualCreate,
    ManualEdit,
    ManualVoid,
    RateSet,
    CashTransactionCreate,
    CashTransactionEdit,
    CashTransactionVoid,
}

public static class PermissionCodes
{
    public static string ToCode(Permission permission) => permission switch
    {
        Permission.TradeRecord => "TRADE_RECORD",
        Permission.TradeEdit => "TRADE_EDIT",
        Permission.TradeVoid => "TRADE_VOID",
        Permission.OpeningCreate => "OPENING_CREATE",
        Permission.OpeningEdit => "OPENING_EDIT",
        Permission.OpeningVoid => "OPENING_VOID",
        Permission.ManualCreate => "MANUAL_CREATE",
        Permission.ManualEdit => "MANUAL_EDIT",
        Permission.ManualVoid => "MANUAL_VOID",
        Permission.RateSet => "RATE_SET",
        Permission.CashTransactionCreate => "CASH_TRANSACTION_CREATE",
        Permission.CashTransactionEdit => "CASH_TRANSACTION_EDIT",
        Permission.CashTransactionVoid => "CASH_TRANSACTION_VOID",
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
        Permission.TradeRecord => "ثبت معامله",
        Permission.TradeEdit => "ویرایش معامله",
        Permission.TradeVoid => "ابطال معامله",
        Permission.OpeningCreate => "ثبت موجودی افتتاحیه",
        Permission.OpeningEdit => "ویرایش موجودی افتتاحیه",
        Permission.OpeningVoid => "ابطال موجودی افتتاحیه",
        Permission.ManualCreate => "ثبت سند دستی",
        Permission.ManualEdit => "ویرایش سند دستی",
        Permission.ManualVoid => "ابطال سند دستی",
        Permission.RateSet => "تنظیم نرخ خرید و فروش",
        Permission.CashTransactionCreate => "ثبت دریافت/پرداخت",
        Permission.CashTransactionEdit => "ویرایش دریافت/پرداخت",
        Permission.CashTransactionVoid => "ابطال دریافت/پرداخت",
        _ => permission.ToString(),
    };

    /// <summary>گروه نمایش هر کار در فرم‌های دسترسی.</summary>
    public static string Group(Permission permission) => permission switch
    {
        Permission.TradeRecord or Permission.TradeEdit or Permission.TradeVoid => "معاملات",
        Permission.OpeningCreate or Permission.OpeningEdit or Permission.OpeningVoid => "موجودی افتتاحیه",
        Permission.ManualCreate or Permission.ManualEdit or Permission.ManualVoid => "اسناد حسابداری دستی",
        Permission.CashTransactionCreate or Permission.CashTransactionEdit or Permission.CashTransactionVoid => "دریافت و پرداخت",
        _ => "نرخ‌ها",
    };
}

/// <summary>نقش کاربر در یک شعبه، همراه با دسترسی‌های همان نقش.</summary>
public sealed record BranchAccess(int BranchId, string BranchName, int RoleId, string RoleName, IReadOnlySet<Permission> Permissions);

/// <summary>
/// دسترسی کاربر که از دیتابیس خوانده می‌شود. مدیر سیستم همه‌ی شعبه‌ها و کارها را دارد.
/// کاربر دیگر فقط شعبه‌هایی را می‌بیند که عضوش است و فقط کارهایی را انجام می‌دهد که نقشش در آن شعبه می‌دهد.
/// </summary>
public sealed record UserAccess(
    int UserId,
    UserRole Role,
    bool IsActive,
    int? DefaultBranchId,
    IReadOnlyDictionary<int, BranchAccess> Branches);

/// <summary>نقش تعریف‌شده‌ی یک شعبه، با دسترسی‌هایش و تعداد کاربرانی که آن نقش را دارند.</summary>
public sealed record AccessRoleInfo(int Id, int BranchId, string Name, IReadOnlySet<Permission> Permissions, int AssignedUsers);
