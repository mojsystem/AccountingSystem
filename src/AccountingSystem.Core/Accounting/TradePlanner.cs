using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// قواعد اعتبارسنجی مشترک و خطوط سند معاملات ارزی. برنامه‌ریزی کامل تغییرات دفتر در <see cref="LedgerPlanner"/> است.
/// PlanBuy و PlanSell برای معامله‌ای به کار می‌روند که فقط وضعیت فعلی صندوق را دارد (بدون تاریخچه).
/// </summary>
public static class TradePlanner
{
    private static readonly CurrencyInfo IrrCurrency = new(CurrencyCodes.Irr, "ریال ایران", 0, true);

    public static PostingDraft PlanBuy(TradeInput input, TradeSnapshot snapshot, int userId, DateTime now) =>
        LedgerPlanner.PlanTrade(BranchLedger.FromSnapshot(snapshot), snapshot.Currency, input, TradeType.Buy, userId, now, now);

    public static PostingDraft PlanSell(TradeInput input, TradeSnapshot snapshot, int userId, DateTime now) =>
        LedgerPlanner.PlanTrade(BranchLedger.FromSnapshot(snapshot), snapshot.Currency, input, TradeType.Sell, userId, now, now);

    public static PostingDraft PlanOpeningForeign(CurrencyInfo currency, decimal quantity, decimal unitRateIrr, TradeSnapshot snapshot, int userId, DateTime now) =>
        LedgerPlanner.PlanOpening(BranchLedger.FromSnapshot(snapshot), currency, quantity, unitRateIrr, userId, now, now);

    public static PostingDraft PlanOpeningIrr(int branchId, decimal amountIrr, decimal irrBalance, int userId, DateTime now) =>
        LedgerPlanner.PlanOpening(BranchLedger.Empty(branchId, irrBalance), IrrCurrency, amountIrr, null, userId, now, now);

    public static void ValidateQuantity(decimal amount, CurrencyInfo currency)
    {
        if (amount <= 0)
        {
            throw new BusinessRuleException("مقدار ارز باید بزرگ‌تر از صفر باشد.");
        }
        if (amount != MoneyMath.RoundTo(amount, currency.DecimalPlaces))
        {
            throw new BusinessRuleException($"مقدار {currency.Code} نمی‌تواند بیش از {currency.DecimalPlaces} رقم اعشار داشته باشد.");
        }
    }

    public static void ValidateRate(decimal rate)
    {
        if (rate <= 0)
        {
            throw new BusinessRuleException("نرخ باید بزرگ‌تر از صفر باشد.");
        }
        if (rate != MoneyMath.RoundTo(rate, 4))
        {
            throw new BusinessRuleException("نرخ نمی‌تواند بیش از ۴ رقم اعشار داشته باشد.");
        }
    }

    /// <summary>اطلاعات توصیفی معامله: مقدار خالی null می‌شود و طول آن با ستون‌های دیتابیس بررسی می‌شود.</summary>
    public static (string? CustomerName, string? NationalCode, string? Note) CleanDetails(string? customerName, string? nationalCode, string? note) =>
        (CleanText(customerName, 100, "نام مشتری"), CleanText(nationalCode, 20, "کد ملی"), CleanText(note, 250, "یادداشت"));

    private static string? CleanText(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        if (trimmed.Length > maxLength)
        {
            throw new BusinessRuleException($"{label} نمی‌تواند بیش از {maxLength} کاراکتر باشد.");
        }
        return trimmed;
    }

    internal static void ValidateFee(decimal fee, TradeType type, decimal irr)
    {
        if (fee < 0)
        {
            throw new BusinessRuleException("کارمزد نمی‌تواند منفی باشد.");
        }
        if (fee != MoneyMath.RoundIrr(fee))
        {
            throw new BusinessRuleException("کارمزد باید به‌صورت عدد صحیح ریال وارد شود.");
        }
        if (type == TradeType.Buy && fee >= irr)
        {
            throw new BusinessRuleException("کارمزد خرید باید کمتر از مبلغ ریالی معامله باشد.");
        }
    }

    /// <summary>سطرهای سند خرید: بدهکار موجودی ارز به بهای ریالی، بستانکار صندوق ریال (پس از کسر کارمزد) و درآمد کارمزد.</summary>
    internal static IReadOnlyList<JournalLineDraft> BuyLines(string code, decimal irr, decimal fee)
    {
        var lines = new List<JournalLineDraft>
        {
            new(AccountCodes.ForeignCash(code), irr, 0m),
            new(AccountCodes.IrrCash, 0m, irr - fee),
        };
        if (fee > 0)
        {
            lines.Add(new JournalLineDraft(AccountCodes.FeeIncome, 0m, fee));
        }
        return lines;
    }

    /// <summary>سطرهای سند فروش: بدهکار صندوق ریال (شامل کارمزد)، بستانکار بهای ارز، سود/زیان و درآمد کارمزد.</summary>
    internal static IReadOnlyList<JournalLineDraft> SellLines(string code, decimal irr, decimal fee, decimal cost, decimal profit)
    {
        var lines = new List<JournalLineDraft> { new(AccountCodes.IrrCash, irr + fee, 0m) };
        if (cost > 0)
        {
            lines.Add(new JournalLineDraft(AccountCodes.ForeignCash(code), 0m, cost));
        }
        if (profit > 0)
        {
            lines.Add(new JournalLineDraft(AccountCodes.FxProfit, 0m, profit));
        }
        else if (profit < 0)
        {
            lines.Add(new JournalLineDraft(AccountCodes.FxLoss, -profit, 0m));
        }
        if (fee > 0)
        {
            lines.Add(new JournalLineDraft(AccountCodes.FeeIncome, 0m, fee));
        }
        return lines;
    }
}
