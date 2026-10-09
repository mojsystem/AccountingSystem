using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// ورودی کاربر برای یک معامله ارزی. FeeIrr کارمزد به ریال است (صفر یعنی بدون کارمزد).
/// </summary>
public sealed record TradeInput(
    int BranchId,
    string CurrencyCode,
    decimal Amount,
    decimal Rate,
    string? CustomerName,
    string? NationalCode,
    string? Note,
    decimal FeeIrr = 0m,
    int? CustomerId = null,
    TradeSettlementMode? SettlementMode = null,
    TradeRateMode RateMode = TradeRateMode.Derived,
    string? SettlementCurrencyCode = null,
    decimal? CrossRate = null,
    IReadOnlyList<TradeSettlementInput>? SettlementLines = null,
    bool ApplyCustomerOffset = false,
    decimal? ValuationIrr = null,
    decimal CustomerOffsetIrr = 0m);

/// <summary>یک سطر تسویه‌ی پولی که کاربر در فرم دریافت/پرداخت وارد می‌کند.</summary>
public sealed record TradeSettlementInput(string CurrencyCode, decimal Amount, decimal RateIrr = 1m);

/// <summary>وضعیت فعلی صندوق و موجودی یک ارز در یک شعبه (برای نمایش و آزمون‌های ساده).</summary>
public sealed record TradeSnapshot(int BranchId, CurrencyInfo Currency, decimal IrrBalance, decimal ForeignBalance, decimal ForeignCostIrr);

/// <summary>معامله‌ی آماده‌ی ذخیره. ReplacesId شناسه‌ی معامله‌ای است که این معامله جای آن را گرفته است.</summary>
public sealed record TradeDraft(
    TradeType Type,
    string CurrencyCode,
    decimal Amount,
    decimal Rate,
    decimal IrrAmount,
    decimal CostIrr,
    decimal ProfitIrr,
    decimal FeeIrr,
    int? CustomerId,
    string? CustomerName,
    string? NationalCode,
    string? Note,
    DateTime OccurredAt,
    int UserId,
    long? ReplacesId = null,
    TradeSettlementMode SettlementMode = TradeSettlementMode.Direct,
    TradeRateMode RateMode = TradeRateMode.Derived,
    decimal CrossRate = 0m,
    decimal CustomerOffsetIrr = 0m,
    IReadOnlyList<TradeSettlementDraft>? Settlements = null,
    string? SettlementCurrencyCode = null);

/// <summary>سطر دریافت/پرداخت آماده‌ی ثبت و بازپخش حسابداری.</summary>
public sealed record TradeSettlementDraft(
    int LineNumber,
    TradeSettlementDirection Direction,
    string CurrencyCode,
    decimal Amount,
    decimal RateIrr,
    decimal IrrAmount,
    decimal CostIrr = 0m,
    decimal ProfitIrr = 0m);

/// <summary>موجودی افتتاحیه‌ی آماده‌ی ذخیره.</summary>
public sealed record OpeningDraft(
    string CurrencyCode,
    decimal Quantity,
    decimal? RateIrr,
    decimal CostIrr,
    DateTime OccurredAt,
    int UserId,
    long? ReplacesId);

/// <summary>
/// ابطال یک سند: سند اصلی علامت باطل می‌خورد و سند معکوس با مجموع سطرهای فعال آن ثبت می‌شود.
/// Description شرح سند معکوس است.
/// </summary>
public sealed record VoidDraft(DocRef Doc, string Reason, string Description, DateTime OccurredAt);

public sealed record JournalLineDraft(string AccountCode, decimal Debit, decimal Credit, int? CustomerId = null);

/// <summary>
/// سند حسابداری آماده‌ی ذخیره؛ سازنده فقط سند متوازن و معتبر را می‌پذیرد.
/// SourceType و Source مشخص می‌کنند سند از کدام نوع سند ساخته شده است. Source.Id = 0 یعنی سند جدید همین ثبت.
/// </summary>
public sealed class JournalDraft
{
    public JournalDraft(string description, DateTime occurredAt, IReadOnlyList<JournalLineDraft> lines, string sourceType, DocRef source)
    {
        if (lines.Count < 2)
        {
            throw new BusinessRuleException("سند حسابداری باید حداقل دو سطر داشته باشد.");
        }
        if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit == 0) == (l.Credit == 0)))
        {
            throw new BusinessRuleException("هر سطر سند باید فقط بدهکار یا فقط بستانکار مثبت داشته باشد.");
        }
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
        {
            throw new BusinessRuleException("سند حسابداری متوازن نیست (جمع بدهکار با بستانکار برابر نیست).");
        }

        Description = description;
        OccurredAt = occurredAt;
        Lines = lines;
        SourceType = sourceType;
        Source = source;
    }

    public string Description { get; }

    public DateTime OccurredAt { get; }

    public IReadOnlyList<JournalLineDraft> Lines { get; }

    public string SourceType { get; }

    public DocRef Source { get; }

    /// <summary>برای سند دستی: شناسه‌ی سندی که این سند جایگزین آن شده است.</summary>
    public long? ReplacesId { get; init; }
}

/// <summary>
/// تغییر موجودی صندوق یک ارز در این ثبت. ExpectedBalance مقدار قبل از ثبت است (کنترل همزمانی).
/// Ref سندی است که این تغییر به آن تعلق دارد؛ برای ابطال، سند ابطال‌شده است.
/// </summary>
public sealed record CashMovementDraft(
    string CurrencyCode,
    decimal ExpectedBalance,
    decimal Delta,
    string Description,
    DateTime OccurredAt,
    string RefType,
    DocRef Ref);

/// <summary>تغییر بهای تمام‌شده‌ی موجودی ارز؛ ExpectedCostIrr برای کنترل همزمانی.</summary>
public sealed record InventoryDraft(string CurrencyCode, decimal ExpectedCostIrr, decimal NewCostIrr);

/// <summary>به‌روزرسانی بهای تمام‌شده و سود یک فروش موجود که در اثر بازمحاسبه تغییر کرده است.</summary>
public sealed record TradeCostUpdate(long TradeId, decimal CostIrr, decimal ProfitIrr);

/// <summary>به‌روزرسانی بهای پرداخت ارزی معامله پس از تغییر میانگین موزون تاریخی آن ارز.</summary>
public sealed record TradeSettlementCostUpdate(long TradeId, int LineNumber, decimal CostIrr, decimal ProfitIrr);

/// <summary>
/// کل تغییرات یک رویداد در یک شعبه که باید در یک تراکنش دیتابیس ذخیره شود.
/// ExpectedVersion نسخه‌ی دفتر شعبه هنگام بارگذاری است. Entity سندی است که این ثبت درباره‌ی آن است.
/// </summary>
public sealed record PostingDraft(
    string Action,
    int BranchId,
    int UserId,
    DateTime Now,
    long ExpectedVersion,
    DocRef Entity,
    VoidDraft? Void,
    TradeDraft? Trade,
    OpeningDraft? Opening,
    IReadOnlyList<JournalDraft> Journals,
    IReadOnlyList<CashMovementDraft> CashMovements,
    IReadOnlyList<InventoryDraft> Inventory,
    IReadOnlyList<TradeCostUpdate> CostUpdates,
    string AuditDetails,
    IReadOnlyList<TradeSettlementCostUpdate>? SettlementCostUpdates = null);
