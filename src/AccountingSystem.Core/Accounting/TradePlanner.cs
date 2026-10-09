using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// محاسبات معاملات ارزی به‌صورت تابع خالص (بدون دیتابیس) تا با تست واحد بررسی شود.
/// بهای تمام‌شده‌ی موجودی ارزی به روش میانگین موزون محاسبه می‌شود.
/// کارمزد معامله به ریال و به حساب درآمد کارمزد (4101) ثبت می‌شود.
/// </summary>
public static class TradePlanner
{
    private const int MaxReasonLength = 200;

    /// <summary>
    /// خرید ارز از مشتری. مشتری مبلغ ریالی منهای کارمزد را دریافت می‌کند و بهای تمام‌شده‌ی ارز،
    /// کل مبلغ ریالی معامله است (کارمزد جزو بهای ارز نیست).
    /// </summary>
    public static PostingDraft PlanBuy(TradeInput input, TradeSnapshot snapshot, int userId, DateTime now)
    {
        ValidateTrade(input, snapshot);
        var code = snapshot.Currency.Code;
        var irr = MoneyMath.RoundIrr(input.Amount * input.Rate);
        if (irr <= 0)
        {
            throw new BusinessRuleException("مبلغ ریالی معامله صفر است.");
        }
        ValidateFee(input.FeeIrr, TradeType.Buy, irr);

        var payout = irr - input.FeeIrr;
        if (snapshot.IrrBalance < payout)
        {
            throw new BusinessRuleException(
                $"موجودی صندوق ریال کافی نیست. موجودی: {MoneyMath.FormatAmount(snapshot.IrrBalance, 0)} ریال، موردنیاز: {MoneyMath.FormatAmount(payout, 0)} ریال.");
        }

        var trade = new TradeDraft(TradeType.Buy, code, input.Amount, input.Rate, irr, irr, 0m, input.FeeIrr,
            Clean(input.CustomerName), Clean(input.NationalCode), Clean(input.Note), now, userId);
        var description = $"خرید {MoneyMath.FormatAmount(input.Amount, snapshot.Currency.DecimalPlaces)} {code} از مشتری با نرخ {MoneyMath.FormatRate(input.Rate)}";
        var journal = new JournalDraft(description, now, BuyLines(code, irr, input.FeeIrr));
        var cash = new[]
        {
            new CashMovementDraft(CurrencyCodes.Irr, snapshot.IrrBalance, -payout, "پرداخت ریال بابت خرید ارز"),
            new CashMovementDraft(code, snapshot.ForeignBalance, input.Amount, "دریافت ارز از مشتری"),
        };
        var inventory = new[]
        {
            new InventoryDraft(code, snapshot.ForeignCostIrr, snapshot.ForeignCostIrr + irr),
        };
        return new PostingDraft(SourceTypes.Trade, now, userId, snapshot.BranchId, trade, null, journal, cash, inventory);
    }

    /// <summary>
    /// فروش ارز به مشتری. مشتری مبلغ ریالی به‌علاوه‌ی کارمزد را پرداخت می‌کند؛ سود معامله (مبلغ ریالی منهای بهای تمام‌شده)
    /// جدا از کارمزد به 4001 یا 5001 می‌رود.
    /// </summary>
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
        ValidateFee(input.FeeIrr, TradeType.Sell, irr);

        // بهای تمام‌شده‌ی مقدار فروخته‌شده به میانگین موزون. فروش کل موجودی، دقیقاً کل بهای ثبت‌شده را خارج می‌کند.
        var cost = snapshot.ForeignBalance == input.Amount
            ? snapshot.ForeignCostIrr
            : MoneyMath.RoundIrr(snapshot.ForeignCostIrr * input.Amount / snapshot.ForeignBalance);
        var profit = irr - cost;
        var receipt = irr + input.FeeIrr;

        var trade = new TradeDraft(TradeType.Sell, code, input.Amount, input.Rate, irr, cost, profit, input.FeeIrr,
            Clean(input.CustomerName), Clean(input.NationalCode), Clean(input.Note), now, userId);
        var description = $"فروش {MoneyMath.FormatAmount(input.Amount, snapshot.Currency.DecimalPlaces)} {code} به مشتری با نرخ {MoneyMath.FormatRate(input.Rate)}";
        var journal = new JournalDraft(description, now, SellLines(code, irr, input.FeeIrr, cost, profit));
        var cash = new[]
        {
            new CashMovementDraft(CurrencyCodes.Irr, snapshot.IrrBalance, receipt, "دریافت ریال بابت فروش ارز"),
            new CashMovementDraft(code, snapshot.ForeignBalance, -input.Amount, "پرداخت ارز به مشتری"),
        };
        var inventory = new[]
        {
            new InventoryDraft(code, snapshot.ForeignCostIrr, snapshot.ForeignCostIrr - cost),
        };
        return new PostingDraft(SourceTypes.Trade, now, userId, snapshot.BranchId, trade, null, journal, cash, inventory);
    }

    /// <summary>
    /// ابطال معامله با سند معکوس. فقط آخرین حرکت فعال موجودی همان ارز در همان شعبه قابل ابطال است،
    /// تا میانگین موزون بعد از ابطال دقیقاً همان مقداری شود که قبل از معامله بود. سند اصلی و سطرهای آن حذف نمی‌شوند.
    /// </summary>
    public static PostingDraft PlanVoid(VoidContext context, string reason, int userId, DateTime now)
    {
        var trade = context.Trade;
        var snapshot = context.Snapshot;
        if (trade.IsVoided)
        {
            throw new BusinessRuleException("این معامله قبلاً باطل شده است.");
        }
        if (snapshot.BranchId != trade.BranchId || snapshot.Currency.Code != trade.CurrencyCode)
        {
            throw new BusinessRuleException("وضعیت صندوق با معامله‌ی انتخابی همخوانی ندارد.");
        }
        if (!context.IsLatestInPool)
        {
            throw new BusinessRuleException(
                "فقط آخرین معامله‌ی همین ارز در این شعبه را می‌توان باطل کرد. ابتدا معاملات جدیدتر همین ارز را باطل کنید.");
        }
        var cleanReason = (reason ?? string.Empty).Trim();
        if (cleanReason.Length == 0)
        {
            throw new BusinessRuleException("دلیل ابطال معامله را وارد کنید.");
        }
        if (cleanReason.Length > MaxReasonLength)
        {
            throw new BusinessRuleException($"دلیل ابطال نمی‌تواند بیش از {MaxReasonLength} کاراکتر باشد.");
        }

        var code = trade.CurrencyCode;
        decimal irrDelta;
        decimal fxDelta;
        decimal costDelta;
        if (trade.Type == TradeType.Buy)
        {
            // خرید باطل می‌شود: ارز برمی‌گردد از صندوق خارج شود و مبلغ پرداختی به صندوق ریال برمی‌گردد.
            var payout = trade.IrrAmount - trade.FeeIrr;
            if (snapshot.ForeignBalance < trade.Amount || snapshot.ForeignCostIrr < trade.CostIrr)
            {
                throw new BusinessRuleException("موجودی ارز برای ابطال این خرید کافی نیست؛ بخشی از ارز قبلاً از صندوق خارج شده است.");
            }
            irrDelta = payout;
            fxDelta = -trade.Amount;
            costDelta = -trade.CostIrr;
        }
        else
        {
            // فروش باطل می‌شود: ارز به صندوق برمی‌گردد و مبلغ دریافتی (شامل کارمزد) به مشتری برگردانده می‌شود.
            var receipt = trade.IrrAmount + trade.FeeIrr;
            if (snapshot.IrrBalance < receipt)
            {
                throw new BusinessRuleException(
                    $"موجودی صندوق ریال برای برگرداندن مبلغ این فروش کافی نیست. موجودی: {MoneyMath.FormatAmount(snapshot.IrrBalance, 0)} ریال، موردنیاز: {MoneyMath.FormatAmount(receipt, 0)} ریال.");
            }
            irrDelta = -receipt;
            fxDelta = trade.Amount;
            costDelta = trade.CostIrr;
        }

        // سند معکوس: همان سطرهای سند اصلی با بدهکار و بستانکار جابه‌جا.
        var original = trade.Type == TradeType.Buy
            ? BuyLines(code, trade.IrrAmount, trade.FeeIrr)
            : SellLines(code, trade.IrrAmount, trade.FeeIrr, trade.CostIrr, trade.ProfitIrr);
        var reversed = original.Select(l => new JournalLineDraft(l.AccountCode, l.Credit, l.Debit)).ToList();
        var journal = new JournalDraft($"ابطال معامله شماره {trade.Id}: {cleanReason}", now, reversed);

        var cash = new[]
        {
            new CashMovementDraft(CurrencyCodes.Irr, snapshot.IrrBalance, irrDelta, $"ابطال معامله شماره {trade.Id}"),
            new CashMovementDraft(code, snapshot.ForeignBalance, fxDelta, $"ابطال معامله شماره {trade.Id}"),
        };
        var inventory = new[]
        {
            new InventoryDraft(code, snapshot.ForeignCostIrr, snapshot.ForeignCostIrr + costDelta),
        };
        return new PostingDraft(SourceTypes.Void, now, userId, trade.BranchId, null,
            new VoidDraft(trade.Id, code, cleanReason), journal, cash, inventory);
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
        return new PostingDraft(SourceTypes.Opening, now, userId, snapshot.BranchId, null, null, journal, cash, inventory);
    }

    public static PostingDraft PlanOpeningIrr(int branchId, decimal amountIrr, decimal irrBalance, int userId, DateTime now)
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
        return new PostingDraft(SourceTypes.Opening, now, userId, branchId, null, null, journal, cash, Array.Empty<InventoryDraft>());
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

    /// <summary>سطرهای سند خرید: بدهکار موجودی ارز به بهای ریالی، بستانکار صندوق ریال (پس از کسر کارمزد) و درآمد کارمزد.</summary>
    private static IReadOnlyList<JournalLineDraft> BuyLines(string code, decimal irr, decimal fee)
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
    private static IReadOnlyList<JournalLineDraft> SellLines(string code, decimal irr, decimal fee, decimal cost, decimal profit)
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

    private static void ValidateTrade(TradeInput input, TradeSnapshot snapshot)
    {
        if (input.BranchId <= 0)
        {
            throw new BusinessRuleException("شعبه‌ی معامله را انتخاب کنید.");
        }
        if (input.BranchId != snapshot.BranchId)
        {
            throw new BusinessRuleException("شعبه‌ی معامله با وضعیت صندوق همخوانی ندارد.");
        }
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

    private static void ValidateFee(decimal fee, TradeType type, decimal irr)
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

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
