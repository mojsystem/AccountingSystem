using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>ورودی کاربر برای یک معامله ارزی.</summary>
public sealed record TradeInput(string CurrencyCode, decimal Amount, decimal Rate, string? CustomerName, string? NationalCode, string? Note);

/// <summary>وضعیت فعلی صندوق و موجودی یک ارز؛ مبنای محاسبه‌ی معامله.</summary>
public sealed record TradeSnapshot(CurrencyInfo Currency, decimal IrrBalance, decimal ForeignBalance, decimal ForeignCostIrr);

public sealed record TradeDraft(
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
    int UserId);

public sealed record JournalLineDraft(string AccountCode, decimal Debit, decimal Credit);

/// <summary>سند حسابداری؛ سازنده فقط سند متوازن و معتبر را می‌پذیرد.</summary>
public sealed class JournalDraft
{
    public JournalDraft(string description, DateTime occurredAt, IReadOnlyList<JournalLineDraft> lines)
    {
        if (lines.Count < 2)
        {
            throw new InvalidOperationException("سند حسابداری باید حداقل دو سطر داشته باشد.");
        }
        if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit == 0) == (l.Credit == 0)))
        {
            throw new InvalidOperationException("هر سطر سند باید فقط بدهکار یا فقط بستانکار مثبت داشته باشد.");
        }
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
        {
            throw new InvalidOperationException("سند حسابداری متوازن نیست (جمع بدهکار با بستانکار برابر نیست).");
        }

        Description = description;
        OccurredAt = occurredAt;
        Lines = lines;
    }

    public string Description { get; }

    public DateTime OccurredAt { get; }

    public IReadOnlyList<JournalLineDraft> Lines { get; }
}

/// <summary>تغییر موجودی صندوق. ExpectedBalance برای کنترل همزمانی (مقایسه قبل از به‌روزرسانی) استفاده می‌شود.</summary>
public sealed record CashMovementDraft(string CurrencyCode, decimal ExpectedBalance, decimal Delta, string Description);

/// <summary>تغییر بهای تمام‌شده‌ی موجودی ارز؛ ExpectedCostIrr برای کنترل همزمانی.</summary>
public sealed record InventoryDraft(string CurrencyCode, decimal ExpectedCostIrr, decimal NewCostIrr);

/// <summary>کل تغییرات یک رویداد مالی که باید در یک تراکنش دیتابیس ذخیره شود.</summary>
public sealed record PostingDraft(
    string SourceType,
    DateTime OccurredAt,
    int UserId,
    TradeDraft? Trade,
    JournalDraft Journal,
    IReadOnlyList<CashMovementDraft> CashMovements,
    IReadOnlyList<InventoryDraft> Inventory);
