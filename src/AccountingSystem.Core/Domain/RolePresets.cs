namespace AccountingSystem.Core.Domain;

/// <summary>یک نقش پیش‌فرض با مجموعه‌ی دسترسی‌های اولیه‌اش.</summary>
public sealed record RolePreset(string Name, IReadOnlyList<Permission> Permissions);

/// <summary>
/// نقش‌های پیش‌فرض که برای هر شعبه ساخته می‌شوند. مدیر بعداً هر کدام را می‌تواند تغییر دهد یا نقش تازه بسازد.
/// اسکریپت نصب (شعبه‌ی MAIN) باید با این فهرست یکی باشد؛ آزمون SQL این موضوع را بررسی می‌کند.
/// </summary>
public static class RolePresets
{
    public const string Accountant = "حسابدار";
    public const string BranchManager = "مدیر شعبه";
    public const string Cashier = "کاربر صندوق";

    public static IReadOnlyList<RolePreset> All { get; } = new[]
    {
        new RolePreset(Accountant, new[]
        {
            Permission.TradeEdit, Permission.TradeVoid,
            Permission.OpeningEdit, Permission.OpeningVoid,
            Permission.ManualCreate, Permission.ManualEdit, Permission.ManualVoid,
            Permission.CashTransactionCreate, Permission.CashTransactionEdit, Permission.CashTransactionVoid,
        }),
        new RolePreset(BranchManager, Enum.GetValues<Permission>()),
        new RolePreset(Cashier, new[] { Permission.TradeRecord, Permission.CashTransactionCreate }),
    };
}
