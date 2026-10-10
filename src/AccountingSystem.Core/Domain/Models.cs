namespace AccountingSystem.Core.Domain;

/// <summary>Buy: صرافی ارز را از مشتری می‌خرد (ریال پرداخت می‌شود). Sell: صرافی ارز را به مشتری می‌فروشد (ریال دریافت می‌شود).</summary>
public enum TradeType
{
    Buy,
    Sell,
}

/// <summary>روش تسویه‌ی معامله: جفت‌ارز، چند دریافت/پرداخت، یا ثبت روی حساب مشتری.</summary>
public enum TradeSettlementMode
{
    Direct,
    Split,
    CustomerAccount,
}

/// <summary>روش تعیین نرخ جفت‌ارز.</summary>
public enum TradeRateMode
{
    Direct,
    Derived,
}

/// <summary>سمت یک سطر تسویه از دید صرافی.</summary>
public enum TradeSettlementDirection
{
    Payment,
    Receipt,
}

/// <summary>نوع سند مستقل دریافت یا پرداخت از/به مشتری.</summary>
public enum CashTransactionDirection
{
    Receipt,
    Payment,
}

/// <summary>Admin: دسترسی کامل به همه‌ی شعبه‌ها. Cashier: وظایف پایه‌ی صندوق در شعبه‌های مجاز.</summary>
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
    public const string CustomerReceivable = "1201";
    public const string CustomerPayable = "2101";

    public static string ForeignCash(string currencyCode) => "1101-" + currencyCode;
}

public static class SourceTypes
{
    public const string Trade = "TRADE";
    public const string Opening = "OPENING";
    public const string Void = "VOID";
    /// <summary>تعدیل بهای تمام‌شده‌ی معامله‌ی فروش که در اثر تغییر تاریخچه‌ی موجودی لازم شده است.</summary>
    public const string Adjust = "ADJUST";
    /// <summary>سند حسابداری دستی (مثلاً هزینه یا تعدیل توسط مدیر).</summary>
    public const string Manual = "MANUAL";
    public const string CashReceipt = "CASH_RECEIPT";
    public const string CashPayment = "CASH_PAYMENT";
    /// <summary>تعدیل بهای تمام‌شده‌ی پرداخت ارزی در اثر تغییر تاریخچه‌ی صندوق ارز.</summary>
    public const string CashAdjustment = "CASH_ADJUST";
    public const string CashTransaction = "CASH_TRANSACTION";
}

/// <summary>کاربر جاری. BranchId برای کاربر صندوق الزامی است؛ مدیر BranchId ندارد و به همه‌ی شعبه‌ها دسترسی دارد.</summary>
public sealed record CurrentUser(int Id, string Username, string FullName, UserRole Role, int? BranchId, string? BranchName);

public sealed record BranchInfo(int Id, string Code, string Name, DateTime CreatedAt);

public sealed record CurrencyInfo(string Code, string Name, int DecimalPlaces, bool IsActive);

/// <summary>مشتری ثبت‌شده‌ی صرافی (فهرست مشترک همه‌ی شعبه‌ها). CustomerCode را سیستم می‌سازد و قابل تغییر نیست.</summary>
public sealed record CustomerInfo(int Id, string CustomerCode, string FullName, string? NationalCode, string? Phone, string? Mobile, string? Address, string? City, string? Sheba1, string? Sheba2, string? CardNumber1, string? CardNumber2, string? Note, DateTime UpdatedAt);

/// <summary>داده‌ی ثبت یا ویرایش مشتری، پیش از اعتبارسنجی.</summary>
public sealed record CustomerInput(string? FullName = null, string? NationalCode = null, string? Phone = null, string? Address = null, string? Note = null, string? Mobile = null, string? City = null, string? Sheba1 = null, string? Sheba2 = null, string? CardNumber1 = null, string? CardNumber2 = null);

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
    string? VoidReason,
    int CustomerId,
    TradeSettlementMode SettlementMode = TradeSettlementMode.Direct,
    TradeRateMode RateMode = TradeRateMode.Derived,
    decimal CrossRate = 0m,
    decimal CustomerOffsetIrr = 0m,
    IReadOnlyList<TradeSettlementInfo>? Settlements = null,
    string? SettlementCurrencyCode = null);

/// <summary>دریافت یا پرداخت ثبت‌شده برای معامله، به‌همراه ارزش ریالی و بهای خروجی صندوق.</summary>
public sealed record TradeSettlementInfo(
    int LineNumber,
    TradeSettlementDirection Direction,
    string CurrencyCode,
    decimal Amount,
    decimal RateIrr,
    decimal IrrAmount,
    decimal CostIrr,
    decimal ProfitIrr);

/// <summary>مانده‌ی خالص مشتری در یک شعبه، به ریال (مثبت: مشتری بدهکار؛ منفی: بستانکار).</summary>
public sealed record CustomerAccountBalance(int BranchId, int CustomerId, decimal ReceivableIrr, decimal PayableIrr);

/// <summary>مانده‌ی امضاشده‌ی مشتری به تفکیک ارز (مثبت: مشتری بدهکار؛ منفی: بستانکار).</summary>
public sealed record CustomerCurrencyBalance(
    int BranchId,
    int CustomerId,
    string CurrencyCode,
    string CurrencyName,
    int DecimalPlaces,
    decimal BalanceAmount);

/// <summary>رسید دریافت یا پرداخت مستقل از معامله، ثبت‌شده روی صندوق و حساب مشتری.</summary>
public sealed record CashTransactionInfo(
    long Id,
    int BranchId,
    string BranchCode,
    string BranchName,
    CashTransactionDirection Direction,
    int CustomerId,
    string CustomerCode,
    string CustomerName,
    string CurrencyCode,
    string CurrencyName,
    int DecimalPlaces,
    decimal Amount,
    string BalanceCurrencyCode,
    decimal BalanceAmount,
    TradeRateMode RateMode,
    decimal RateIrr,
    decimal IrrAmount,
    decimal CostIrr,
    decimal ProfitIrr,
    string? Note,
    DateTime OccurredAt,
    string CreatedBy,
    bool IsVoided,
    DateTime? VoidedAt,
    string? VoidedBy,
    string? VoidReason);

/// <summary>مانده‌ی خالص هر شخص در گزارش معین؛ مثبت یعنی بدهکار و منفی یعنی بستانکار.</summary>
public sealed record CustomerBalanceReportRow(int CustomerId, string CustomerCode, string FullName, decimal BalanceIrr)
{
    public decimal DebitBalanceIrr => Math.Max(0m, BalanceIrr);

    public decimal CreditBalanceIrr => Math.Max(0m, -BalanceIrr);

    public string BalanceSide => BalanceIrr > 0m ? "بدهکار" : BalanceIrr < 0m ? "بستانکار" : "تسویه";
}

/// <summary>یک گردش حساب تفصیلی شخص از روی سطرهای واقعی دفتر روزنامه.</summary>
public sealed record CustomerLedgerLineInfo(
    long JournalEntryId,
    long? SourceId,
    DateTime OccurredAt,
    int BranchId,
    string BranchName,
    string SourceType,
    string Description,
    int LineNo,
    string AccountCode,
    decimal Debit,
    decimal Credit,
    bool IsVoided,
    decimal BalanceIrr);

/// <summary>داده‌ی خام گردش شخص شامل مانده‌ی ابتدای دوره و ریز گردش‌های دوره.</summary>
public sealed record CustomerLedgerData(decimal OpeningBalanceIrr, IReadOnlyList<CustomerLedgerLineInfo> Lines);

/// <summary>گزارش کامل معین شخص؛ مانده‌ی پایانی بر پایه‌ی همه‌ی سطرهای دفتر در بازه است.</summary>
public sealed record CustomerLedgerReport(
    CustomerInfo Customer,
    decimal OpeningBalanceIrr,
    IReadOnlyList<CustomerLedgerLineInfo> Lines,
    decimal ClosingBalanceIrr);

public sealed record JournalLineInfo(int LineNo, string AccountCode, string AccountName, decimal Debit, decimal Credit);

/// <summary>
/// سند حسابداری. SourceId شناسه‌ی سندی است که این سطرها از آن ساخته شده‌اند (معامله، افتتاحیه یا خود سند دستی).
/// IsVoided یعنی سند (یا سطرهای آن) با سند معکوس باطل شده است.
/// </summary>
public sealed record JournalEntryInfo(
    long Id,
    DateTime OccurredAt,
    string Description,
    string SourceType,
    int BranchId,
    string BranchName,
    IReadOnlyList<JournalLineInfo> Lines,
    long? SourceId = null,
    bool IsVoided = false);

/// <summary>
/// حساب دفتر کل با سطح (۱ گروه، ۲ کل، ۳ معین، ۴ تفصیلی) و حساب پدر.
/// فقط حساب «قابل سند» (فعال و بی‌زیرمجموعه) در سند دستی پذیرفته می‌شود.
/// </summary>
public sealed record AccountInfo(
    string Code,
    string Name,
    string AccountType,
    int Level,
    string? ParentCode,
    bool IsSystem,
    bool IsActive,
    bool HasChildren,
    bool HasPostings)
{
    /// <summary>حساب فعال و بدون زیرمجموعه؛ فقط چنین حسابی سند می‌گیرد.</summary>
    public bool IsPostable => IsActive && !HasChildren;
}

/// <summary>داده‌ی ذخیره‌ی یک حساب (ساخت یا ویرایش). سطح را سرویس از روی حساب پدر محاسبه می‌کند.</summary>
public sealed record AccountRecord(string Code, string Name, int Level, string? ParentCode, string AccountType, bool IsActive);

/// <summary>
/// موجودی افتتاحیه‌ی یک شعبه. برای ریال، Quantity همان مبلغ ریال و RateIrr خالی است.
/// </summary>
public sealed record OpeningInfo(
    long Id,
    int BranchId,
    string BranchName,
    string CurrencyCode,
    decimal Quantity,
    decimal? RateIrr,
    decimal CostIrr,
    DateTime OccurredAt,
    string CreatedBy,
    bool IsVoided,
    string? VoidReason);

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
