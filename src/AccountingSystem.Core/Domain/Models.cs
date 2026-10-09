namespace AccountingSystem.Core.Domain;

/// <summary>Buy: صرافی ارز را از مشتری می‌خرد (ریال پرداخت می‌شود). Sell: صرافی ارز را به مشتری می‌فروشد (ریال دریافت می‌شود).</summary>
public enum TradeType
{
    Buy,
    Sell,
}

/// <summary>Admin: دسترسی کامل به همه‌ی شعبه‌ها. Cashier: ثبت معاملات و نرخ‌ها در شعبه‌ی خودش.</summary>
public enum UserRole
{
    Admin,
    Cashier,
}

public static class CurrencyCodes
{
    public const string Irr = "IRR";
}

public static class AccountCodes
{
    public const string IrrCash = "1001";
    public const string OpeningCapital = "3001";
    public const string FxProfit = "4001";
    public const string FxLoss = "5001";
    public const string FeeIncome = "4101";

    public static string ForeignCash(string currencyCode) => "1101-" + currencyCode;
}

public static class SourceTypes
{
    public const string Trade = "TRADE";
    public const string Opening = "OPENING";
    public const string Void = "VOID";
}

/// <summary>کاربر جاری. BranchId برای کاربر صندوق الزامی است؛ مدیر BranchId ندارد و به همه‌ی شعبه‌ها دسترسی دارد.</summary>
public sealed record CurrentUser(int Id, string Username, string FullName, UserRole Role, int? BranchId, string? BranchName);

public sealed record BranchInfo(int Id, string Code, string Name, DateTime CreatedAt);

public sealed record CurrencyInfo(string Code, string Name, int DecimalPlaces, bool IsActive);

public sealed record RateInfo(
    int BranchId,
    string BranchName,
    string CurrencyCode,
    string CurrencyName,
    decimal BuyRateIrr,
    decimal SellRateIrr,
    DateTime CreatedAt);

public sealed record CashBoxInfo(
    int Id,
    int BranchId,
    string BranchName,
    string CurrencyCode,
    string Name,
    decimal Balance,
    DateTime UpdatedAt);

/// <summary>بهای تمام‌شده‌ی موجودی یک ارز در یک شعبه.</summary>
public sealed record InventoryInfo(int BranchId, string CurrencyCode, decimal TotalCostIrr);

public sealed record TradeInfo(
    long Id,
    int BranchId,
    string BranchCode,
    string BranchName,
    TradeType Type,
    string CurrencyCode,
    decimal Amount,
    decimal Rate,
    decimal IrrAmount,
    decimal CostIrr,
    decimal ProfitIrr,
    decimal FeeIrr,
    string? CustomerName,
    string? NationalCode,
    string? Note,
    DateTime OccurredAt,
    string CreatedBy,
    bool IsVoided,
    DateTime? VoidedAt,
    string? VoidedBy,
    string? VoidReason);

public sealed record JournalLineInfo(int LineNo, string AccountCode, string AccountName, decimal Debit, decimal Credit);

public sealed record JournalEntryInfo(
    long Id,
    DateTime OccurredAt,
    string Description,
    string SourceType,
    int BranchId,
    string BranchName,
    IReadOnlyList<JournalLineInfo> Lines);

public sealed record UserInfo(
    int Id,
    string Username,
    string FullName,
    UserRole Role,
    bool IsActive,
    DateTime CreatedAt,
    int? BranchId,
    string? BranchName);

public sealed record UserAccount(
    int Id,
    string Username,
    string FullName,
    UserRole Role,
    bool IsActive,
    string PasswordHash,
    int? BranchId,
    string? BranchName);
