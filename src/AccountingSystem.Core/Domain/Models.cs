namespace AccountingSystem.Core.Domain;

/// <summary>Buy: صرافی ارز را از مشتری می‌خرد (ریال پرداخت می‌شود). Sell: صرافی ارز را به مشتری می‌فروشد (ریال دریافت می‌شود).</summary>
public enum TradeType
{
    Buy,
    Sell,
}

/// <summary>Admin: دسترسی کامل. Cashier: ثبت معاملات و نرخ‌ها.</summary>
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

    public static string ForeignCash(string currencyCode) => "1101-" + currencyCode;
}

public static class SourceTypes
{
    public const string Trade = "TRADE";
    public const string Opening = "OPENING";
}

public sealed record CurrentUser(int Id, string Username, string FullName, UserRole Role);

public sealed record CurrencyInfo(string Code, string Name, int DecimalPlaces, bool IsActive);

public sealed record RateInfo(string CurrencyCode, string CurrencyName, decimal BuyRateIrr, decimal SellRateIrr, DateTime CreatedAt);

public sealed record CashBoxInfo(int Id, string CurrencyCode, string Name, decimal Balance, DateTime UpdatedAt);

public sealed record TradeInfo(
    long Id,
    TradeType Type,
    string CurrencyCode,
    decimal Amount,
    decimal Rate,
    decimal IrrAmount,
    decimal CostIrr,
    decimal ProfitIrr,
    string? CustomerName,
    string? NationalCode,
    string? Note,
    DateTime OccurredAt,
    string CreatedBy);

public sealed record JournalLineInfo(int LineNo, string AccountCode, string AccountName, decimal Debit, decimal Credit);

public sealed record JournalEntryInfo(long Id, DateTime OccurredAt, string Description, string SourceType, IReadOnlyList<JournalLineInfo> Lines);

public sealed record UserInfo(int Id, string Username, string FullName, UserRole Role, bool IsActive, DateTime CreatedAt);

public sealed record UserAccount(int Id, string Username, string FullName, UserRole Role, bool IsActive, string PasswordHash);
