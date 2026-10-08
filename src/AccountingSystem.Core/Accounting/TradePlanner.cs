using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// محاسبات معاملات ارزی به‌صورت تابع خالص (بدون دیتابیس) تا با تست واحد بررسی شود.
/// بهای تمام‌شده‌ی موجودی ارزی به روش میانگین موزون محاسبه می‌شود.
/// </summary>
public static class TradePlanner
{
    public static PostingDraft PlanBuy(TradeInput input, TradeSnapshot snapshot, int userId, DateTime now)
    {
        ValidateTrade(input, snapshot);
        var code = snapshot.Currency.Code;
        var irr = MoneyMath.RoundIrr(input.Amount * input.Rate);
        if (irr <= 0)
        {
            throw new BusinessRuleException("مبلغ ریالی معامله صفر است.");
        }
        if (snapshot.IrrBalance < irr)
        {
            throw new BusinessRuleException(
                $"موجودی صندوق ریال کافی نیست. موجودی: {MoneyMath.FormatAmount(snapshot.IrrBalance, 0)} ریال، موردنیاز: {MoneyMath.FormatAmount(irr, 0)} ریال.");
        }

        var trade = new TradeDraft(TradeType.Buy, code, input.Amount, input.Rate, irr, irr, 0m,
            Clean(input.CustomerName), Clean(input.NationalCode), Clean(input.Note), now, userId);
        var description = $"خرید {MoneyMath.FormatAmount(input.Amount, snapshot.Currency.DecimalPlaces)} {code} از مشتری با نرخ {MoneyMath.FormatRate(input.Rate)}";
        var journal = new JournalDraft(description, now, new[]
        {
            new JournalLineDraft(AccountCodes.ForeignCash(code), irr, 0m),
            new JournalLineDraft(AccountCodes.IrrCash, 0m, irr),
        });
        var cash = new[]
        {
            new CashMovementDraft(CurrencyCodes.Irr, snapshot.IrrBalance, -irr, "پرداخت ریال بابت خرید ارز"),
            new CashMovementDraft(code, snapshot.ForeignBalance, input.Amount, "دریافت ارز از مشتری"),
        };
        var inventory = new[]
        {
            new InventoryDraft(code, snapshot.ForeignCostIrr, snapshot.ForeignCostIrr + irr),
        };
        return new PostingDraft(SourceTypes.Trade, now, userId, trade, journal, cash, inventory);
    }

    public static PostingDraft PlanSell(TradeInput input, TradeSnapshot snapshot, int userId, DateTime now)
    {
        ValidateTrade(input, snapshot);
        var code = snapshot.Currency.Code;
        if (snapshot.ForeignBalance < input.Amount)
        {
            throw new BusinessRuleException(
                $"موجودی {code} در صندوق کافی نیست. موجودی: {MoneyMath.FormatAmount(snapshot.ForeignBalance, snapshot.Currency.DecimalPlaces)}");
        }
        var irr = MoneyMath.RoundIrr(input.Amount * input.Rate);
        if (irr <= 0)
        {
            throw new BusinessRuleException("مبلغ ریالی معامله صفر است.");
        }

        // بهای تمام‌شده‌ی مقدار فروخته‌شده به میانگین موزون. فروش کل موجودی، دقیقاً کل بهای ثبت‌شده را خارج می‌کند.
        var cost = snapshot.ForeignBalance == input.Amount
            ? snapshot.ForeignCostIrr
            : MoneyMath.RoundIrr(snapshot.ForeignCostIrr * input.Amount / snapshot.ForeignBalance);
        var profit = irr - cost;

        var lines = new List<JournalLineDraft> { new JournalLineDraft(AccountCodes.IrrCash, irr, 0m) };
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

        var trade = new TradeDraft(TradeType.Sell, code, input.Amount, input.Rate, irr, cost, profit,
            Clean(input.CustomerName), Clean(input.NationalCode), Clean(input.Note), now, userId);
        var description = $"فروش {MoneyMath.FormatAmount(input.Amount, snapshot.Currency.DecimalPlaces)} {code} به مشتری با نرخ {MoneyMath.FormatRate(input.Rate)}";
        var journal = new JournalDraft(description, now, lines);
        var cash = new[]
        {
            new CashMovementDraft(CurrencyCodes.Irr, snapshot.IrrBalance, irr, "دریافت ریال بابت فروش ارز"),
            new CashMovementDraft(code, snapshot.ForeignBalance, -input.Amount, "پرداخت ارز به مشتری"),
        };
        var inventory = new[]
        {
            new InventoryDraft(code, snapshot.ForeignCostIrr, snapshot.ForeignCostIrr - cost),
        };
        return new PostingDraft(SourceTypes.Trade, now, userId, trade, journal, cash, inventory);
    }

    /// <summary>موجودی افتتاحیه‌ی ارز: بهای ریالی آن در حساب سرمایه‌ی افتتاحیه ثبت می‌شود.</summary>
    public static PostingDraft PlanOpeningForeign(CurrencyInfo currency, decimal quantity, decimal unitRateIrr, TradeSnapshot snapshot, int userId, DateTime now)
    {
        if (currency.Code == CurrencyCodes.Irr)
        {
            throw new BusinessRuleException("برای ریال از موجودی افتتاحیه ریال استفاده کنید.");
        }
        if (!currency.IsActive)
        {
            throw new BusinessRuleException("این ارز غیرفعال است.");
        }
        ValidateQuantity(quantity, currency);
        ValidateRate(unitRateIrr);

        var code = currency.Code;
        var costIrr = MoneyMath.RoundIrr(quantity * unitRateIrr);
        if (costIrr <= 0)
        {
            throw new BusinessRuleException("بهای ریالی موجودی افتتاحیه صفر است.");
        }
        var description = $"موجودی افتتاحیه {code}: {MoneyMath.FormatAmount(quantity, currency.DecimalPlaces)} واحد با نرخ {MoneyMath.FormatRate(unitRateIrr)}";
        var journal = new JournalDraft(description, now, new[]
        {
            new JournalLineDraft(AccountCodes.ForeignCash(code), costIrr, 0m),
            new JournalLineDraft(AccountCodes.OpeningCapital, 0m, costIrr),
        });
        var cash = new[] { new CashMovementDraft(code, snapshot.ForeignBalance, quantity, "موجودی افتتاحیه ارز") };
        var inventory = new[] { new InventoryDraft(code, snapshot.ForeignCostIrr, snapshot.ForeignCostIrr + costIrr) };
        return new PostingDraft(SourceTypes.Opening, now, userId, null, journal, cash, inventory);
    }

    public static PostingDraft PlanOpeningIrr(decimal amountIrr, decimal irrBalance, int userId, DateTime now)
    {
        if (amountIrr <= 0 || amountIrr != MoneyMath.RoundIrr(amountIrr))
        {
            throw new BusinessRuleException("مبلغ افتتاحیه ریال باید عددی صحیح و بزرگ‌تر از صفر باشد.");
        }
        var journal = new JournalDraft("موجودی افتتاحیه ریال", now, new[]
        {
            new JournalLineDraft(AccountCodes.IrrCash, amountIrr, 0m),
            new JournalLineDraft(AccountCodes.OpeningCapital, 0m, amountIrr),
        });
        var cash = new[] { new CashMovementDraft(CurrencyCodes.Irr, irrBalance, amountIrr, "موجودی افتتاحیه ریال") };
        return new PostingDraft(SourceTypes.Opening, now, userId, null, journal, cash, Array.Empty<InventoryDraft>());
    }

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

    private static void ValidateTrade(TradeInput input, TradeSnapshot snapshot)
    {
        if (snapshot.Currency.Code == CurrencyCodes.Irr)
        {
            throw new BusinessRuleException("ریال ارز معاملاتی نیست.");
        }
        if (!snapshot.Currency.IsActive)
        {
            throw new BusinessRuleException("این ارز غیرفعال است.");
        }
        ValidateQuantity(input.Amount, snapshot.Currency);
        ValidateRate(input.Rate);
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
