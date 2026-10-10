using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// برنامه‌ریزی همه‌ی تغییرات دفتر: ثبت معامله، سند افتتاحیه یا سند دستی، ابطال، و جایگزینی (ویرایش مالی).
/// هر تغییر کل تاریخچه‌ی شعبه را از نو اجرا می‌کند. اگر تاریخچه‌ی جدید معتبر باشد، بهای تمام‌شده‌ی فروش‌های
/// بعدی بازمحاسبه می‌شود و تفاوت‌ها با سند تعدیل ثبت می‌شوند. اسناد قبلی هیچ‌وقت تغییر داده نمی‌شوند.
/// </summary>
public static class LedgerPlanner
{
    private const int MaxReasonLength = 200;
    private const int MaxDescriptionLength = 250;
    private const string ForeignInventoryPrefix = "1101-";

    /// <summary>سطرهای جدید بعد از همه‌ی رویدادهای هم‌زمان قرار می‌گیرند.</summary>
    private const long NewEventSeq = long.MaxValue - 100_000;

    public static PostingDraft PlanTrade(
        BranchLedger ledger,
        CurrencyInfo currency,
        TradeInput input,
        TradeType type,
        int userId,
        DateTime occurredAt,
        DateTime now,
        DocRef? replaces = null,
        string? replaceReason = null)
    {
        ValidateTrade(input, ledger.BranchId, currency);
        var code = currency.Code;
        var irr = input.ValuationIrr ?? MoneyMath.RoundIrr(input.Amount * input.Rate);
        if (irr <= 0)
        {
            throw new BusinessRuleException("مبلغ ریالی معامله صفر است.");
        }
        var rate = input.ValuationIrr is { } explicitValue
            ? MoneyMath.RoundTo(explicitValue / input.Amount, 4)
            : input.Rate;
        TradePlanner.ValidateRate(rate);

        var fee = input.FeeIrr;
        TradePlanner.ValidateFee(fee, type, irr);
        var mode = input.SettlementMode ?? TradeSettlementMode.Direct;
        var rateMode = input.RateMode;
        var crossRate = input.CrossRate ?? (mode == TradeSettlementMode.Direct ? rate : 0m);
        if (mode == TradeSettlementMode.Direct)
        {
            TradePlanner.ValidateCrossRate(crossRate);
        }
        if (input.CustomerOffsetIrr < 0 || input.CustomerOffsetIrr != MoneyMath.RoundIrr(input.CustomerOffsetIrr))
        {
            throw new BusinessRuleException("مبلغ تهاتر باید عدد صحیح و نامنفی ریال باشد.");
        }

        var customerDue = type == TradeType.Buy ? irr - fee : irr + fee;
        if (input.CustomerOffsetIrr > customerDue)
        {
            throw new BusinessRuleException("مبلغ تهاتر از مانده‌ی این معامله بیشتر است.");
        }

        var (customer, nationalCode, note) = TradePlanner.CleanDetails(input.CustomerName, input.NationalCode, input.Note);
        var settlementInputs = input.SettlementLines ?? Array.Empty<TradeSettlementInput>();
        if (input.SettlementMode is null)
        {
            // سازگاری با فراخوانی‌های قدیمی: خرید/فروش با صندوق ریال به‌صورت کامل تسویه می‌شد.
            var legacyAmount = type == TradeType.Buy ? irr - fee : irr + fee;
            settlementInputs = legacyAmount > 0
                ? new[] { new TradeSettlementInput(CurrencyCodes.Irr, legacyAmount, 1m) }
                : Array.Empty<TradeSettlementInput>();
            mode = TradeSettlementMode.Direct;
            rateMode = TradeRateMode.Derived;
            crossRate = rate;
        }
        if (mode == TradeSettlementMode.CustomerAccount && settlementInputs.Count != 0)
        {
            throw new BusinessRuleException("در روش حساب مشتری، سطر دریافت یا پرداخت نقدی وارد نکنید.");
        }
        if (mode == TradeSettlementMode.Direct && settlementInputs.Count > 1)
        {
            throw new BusinessRuleException("تسویه‌ی مستقیم فقط یک ارز مقابل دارد؛ برای چند ارز، روش تسویه‌ی چندبخشی را انتخاب کنید.");
        }
        if (mode == TradeSettlementMode.Split && settlementInputs.Count == 0)
        {
            throw new BusinessRuleException("حداقل یک سطر دریافت یا پرداخت برای تسویه‌ی چندبخشی وارد کنید.");
        }
        if (input.CustomerOffsetIrr > 0 && input.CustomerId is null)
        {
            throw new BusinessRuleException("برای تهاتر، مشتری را انتخاب کنید.");
        }

        var direction = type == TradeType.Buy ? TradeSettlementDirection.Payment : TradeSettlementDirection.Receipt;
        var settlements = new List<TradeSettlementDraft>();
        var newEvents = new List<LedgerEvent>
        {
            type == TradeType.Buy
                ? new LedgerEvent(LedgerDocKind.Trade, 0, LedgerEventKind.Acquire, code, occurredAt, NewEventSeq,
                    input.Amount, irr, fee, 0m, 0m, 0m)
                : new LedgerEvent(LedgerDocKind.Trade, 0, LedgerEventKind.Dispose, code, occurredAt, NewEventSeq,
                    input.Amount, irr, fee, 0m, 0m, 0m),
        };

        for (var index = 0; index < settlementInputs.Count; index++)
        {
            var line = settlementInputs[index];
            var settlementCode = (line.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
            if (settlementCode.Length != 3 || settlementCode == code)
            {
                throw new BusinessRuleException("ارز هر سطر دریافت/پرداخت باید معتبر و با ارز معامله متفاوت باشد.");
            }
            if (line.Amount <= 0 || line.Amount != MoneyMath.RoundTo(line.Amount, 4))
            {
                throw new BusinessRuleException("مقدار هر سطر دریافت/پرداخت باید مثبت و حداکثر تا ۴ رقم اعشار باشد.");
            }
            TradePlanner.ValidateRate(line.RateIrr);
            var lineValueIrr = settlementCode == CurrencyCodes.Irr
                ? MoneyMath.RoundIrr(line.Amount)
                : MoneyMath.RoundIrr(line.Amount * line.RateIrr);
            if (lineValueIrr <= 0)
            {
                throw new BusinessRuleException($"ارزش ریالی سطر تسویه‌ی {settlementCode} صفر است.");
            }

            var lineNumber = index + 1;
            var lineDraft = new TradeSettlementDraft(lineNumber, direction, settlementCode, line.Amount, line.RateIrr, lineValueIrr,
                DecimalPlaces: line.DecimalPlaces);
            settlements.Add(lineDraft);
            var seq = NewEventSeq + lineNumber;
            if (settlementCode == CurrencyCodes.Irr)
            {
                var irrDelta = direction == TradeSettlementDirection.Payment ? -line.Amount : line.Amount;
                newEvents.Add(new LedgerEvent(LedgerDocKind.Trade, 0, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                    occurredAt, seq, 0m, lineValueIrr, fee, irrDelta, 0m, 0m, lineNumber));
            }
            else
            {
                var eventKind = direction == TradeSettlementDirection.Payment ? LedgerEventKind.Dispose : LedgerEventKind.Acquire;
                newEvents.Add(new LedgerEvent(LedgerDocKind.Trade, 0, eventKind, settlementCode, occurredAt, seq,
                    line.Amount, lineValueIrr, 0m, 0m, 0m, 0m, lineNumber));
            }
        }

        var customerBalanceCurrencyCode = (input.CustomerBalanceCurrencyCode
            ?? input.SettlementCurrencyCode
            ?? settlementInputs.FirstOrDefault()?.CurrencyCode
            ?? CurrencyCodes.Irr).Trim().ToUpperInvariant();
        if (customerBalanceCurrencyCode.Length != 3)
        {
            throw new BusinessRuleException("ارز مانده‌ی حساب مشتری نامعتبر است.");
        }
        var balanceCurrencySettlement = settlementInputs.FirstOrDefault(line =>
            string.Equals(line.CurrencyCode, customerBalanceCurrencyCode, StringComparison.OrdinalIgnoreCase));
        var customerBalanceRateIrr = customerBalanceCurrencyCode == CurrencyCodes.Irr
            ? 1m
            : input.CustomerBalanceRateIrr ?? balanceCurrencySettlement?.RateIrr ?? rate;
        TradePlanner.ValidateRate(customerBalanceRateIrr);
        var customerBalanceDecimalPlaces = input.CustomerBalanceDecimalPlaces
            ?? balanceCurrencySettlement?.DecimalPlaces
            ?? (customerBalanceCurrencyCode == CurrencyCodes.Irr ? 0 : 4);
        if (customerBalanceDecimalPlaces is < 0 or > 4)
        {
            throw new BusinessRuleException("تعداد رقم اعشار ارز مانده‌ی حساب مشتری نامعتبر است.");
        }
        var customerOffsetCurrencyCode = input.CustomerOffsetCurrencyCode ?? CurrencyCodes.Irr;
        var customerOffsetAmount = input.CustomerOffsetCurrencyCode is null
            ? input.CustomerOffsetIrr
            : input.CustomerOffsetAmount;

        var amountText = MoneyMath.FormatAmount(input.Amount, currency.DecimalPlaces);
        var description = type == TradeType.Buy
            ? $"خرید {amountText} {code} از مشتری"
            : $"فروش {amountText} {code} به مشتری";
        if (mode == TradeSettlementMode.CustomerAccount)
        {
            description += "؛ ثبت روی حساب مشتری";
        }
        else if (settlements.Count > 0)
        {
            description += $"؛ {(direction == TradeSettlementDirection.Payment ? "پرداخت" : "دریافت")} " +
                string.Join("، ", settlements.Select(s => $"{MoneyMath.FormatAmount(s.Amount, 4)} {s.CurrencyCode}"));
        }
        if (description.Length > 240)
        {
            description = description[..240];
        }

        var tradeRef = new DocRef(LedgerDocKind.Trade, 0);
        return Compose(
            ledger,
            replaces is null ? "TRADE_CREATE" : "TRADE_REPLACE",
            tradeRef,
            userId,
            now,
            replaces,
            replaceReason,
            newEvents,
            state =>
            {
                var mainDisposal = type == TradeType.Sell ? state.Disposals[tradeRef] : default;
                var cost = type == TradeType.Sell ? mainDisposal.CostIrr : irr;
                var baseProfit = type == TradeType.Sell ? mainDisposal.ProfitIrr : 0m;
                var completedSettlements = settlements.Select(line =>
                {
                    if (line.Direction == TradeSettlementDirection.Payment && line.CurrencyCode != CurrencyCodes.Irr)
                    {
                        var disposal = state.SettlementDisposals[(tradeRef, line.LineNumber)];
                        return line with { CostIrr = disposal.CostIrr, ProfitIrr = disposal.ProfitIrr };
                    }
                    return line with { CostIrr = line.IrrAmount };
                }).ToList();
                var profit = baseProfit + completedSettlements.Sum(s => s.ProfitIrr);
                var balanceComponents = CustomerBalanceComponents(
                    customerDue,
                    input.CustomerOffsetIrr,
                    customerOffsetCurrencyCode,
                    customerOffsetAmount,
                    customerBalanceCurrencyCode,
                    customerBalanceRateIrr,
                    customerBalanceDecimalPlaces,
                    completedSettlements);
                var trade = new TradeDraft(type, code, input.Amount, rate, irr, cost, profit, fee,
                    input.CustomerId, customer, nationalCode, note, occurredAt, userId, ReplacedId(replaces),
                    mode, rateMode, crossRate, input.CustomerOffsetIrr, completedSettlements,
                    customerBalanceCurrencyCode);
                var lines = TradeJournalLines(code, irr, fee, cost, baseProfit, input.CustomerId,
                    input.CustomerOffsetIrr, customerOffsetCurrencyCode, customerOffsetAmount,
                    completedSettlements, balanceComponents, type);
                var journal = new JournalDraft(description, occurredAt, lines, SourceTypes.Trade, tradeRef);
                return new NewDocumentResult(trade, null, null, new[] { journal });
            });
    }

    private static IReadOnlyList<JournalLineDraft> TradeJournalLines(
        string currencyCode,
        decimal irr,
        decimal fee,
        decimal cost,
        decimal baseProfit,
        int? customerId,
        decimal customerOffsetIrr,
        string customerOffsetCurrencyCode,
        decimal customerOffsetAmount,
        IReadOnlyList<TradeSettlementDraft> settlements,
        IReadOnlyList<CustomerBalanceComponent> balanceComponents,
        TradeType type)
    {
        var lines = new List<JournalLineDraft>();
        if (type == TradeType.Buy)
        {
            lines.Add(new JournalLineDraft(AccountCodes.ForeignCash(currencyCode), irr, 0m));
            AddCustomerBalanceComponents(lines, AccountCodes.CustomerPayable, customerId, balanceComponents, liability: true);
            if (fee > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.FeeIncome, 0m, fee));
            }
            if (customerOffsetIrr > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, customerOffsetIrr, 0m, customerId,
                    customerOffsetCurrencyCode, customerOffsetAmount));
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, 0m, customerOffsetIrr, customerId,
                    customerOffsetCurrencyCode, -customerOffsetAmount));
            }
        }
        else
        {
            AddCustomerBalanceComponents(lines, AccountCodes.CustomerReceivable, customerId, balanceComponents, liability: false);
            if (cost > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.ForeignCash(currencyCode), 0m, cost));
            }
            AddProfitLoss(lines, baseProfit);
            if (fee > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.FeeIncome, 0m, fee));
            }
            if (customerOffsetIrr > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, 0m, customerOffsetIrr, customerId,
                    customerOffsetCurrencyCode, -customerOffsetAmount));
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, customerOffsetIrr, 0m, customerId,
                    customerOffsetCurrencyCode, customerOffsetAmount));
            }
        }

        foreach (var settlement in settlements)
        {
            var cashAccount = settlement.CurrencyCode == CurrencyCodes.Irr
                ? AccountCodes.IrrCash
                : AccountCodes.ForeignCash(settlement.CurrencyCode);
            if (settlement.Direction == TradeSettlementDirection.Receipt)
            {
                lines.Add(new JournalLineDraft(cashAccount, settlement.IrrAmount, 0m));
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, 0m, settlement.IrrAmount, customerId,
                    settlement.CurrencyCode, -settlement.Amount));
            }
            else
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, settlement.IrrAmount, 0m, customerId,
                    settlement.CurrencyCode, settlement.Amount));
                if (settlement.CostIrr > 0m)
                {
                    lines.Add(new JournalLineDraft(cashAccount, 0m, settlement.CostIrr));
                }
                AddProfitLoss(lines, settlement.ProfitIrr);
            }
        }

        return lines;
    }

    private static void AddCustomerBalanceComponents(
        List<JournalLineDraft> lines,
        string accountCode,
        int? customerId,
        IReadOnlyList<CustomerBalanceComponent> components,
        bool liability)
    {
        foreach (var component in components)
        {
            if (component.IrrAmount <= 0m)
            {
                continue;
            }
            var delta = liability ? -component.Amount : component.Amount;
            lines.Add(liability
                ? new JournalLineDraft(accountCode, 0m, component.IrrAmount, customerId, component.CurrencyCode, delta)
                : new JournalLineDraft(accountCode, component.IrrAmount, 0m, customerId, component.CurrencyCode, delta));
        }
    }

    private static IReadOnlyList<CustomerBalanceComponent> CustomerBalanceComponents(
        decimal customerDueIrr,
        decimal offsetIrr,
        string offsetCurrencyCode,
        decimal offsetAmount,
        string accountCurrencyCode,
        decimal accountRateIrr,
        int accountDecimalPlaces,
        IReadOnlyList<TradeSettlementDraft> settlements)
    {
        var components = new List<CustomerBalanceComponent>();
        var remainingIrr = customerDueIrr;
        if (offsetIrr > 0m)
        {
            components.Add(new CustomerBalanceComponent(offsetCurrencyCode, offsetAmount, offsetIrr));
            remainingIrr -= offsetIrr;
        }

        foreach (var settlement in settlements)
        {
            if (remainingIrr <= 0m)
            {
                break;
            }
            var allocatedIrr = Math.Min(remainingIrr, settlement.IrrAmount);
            if (allocatedIrr <= 0m)
            {
                continue;
            }
            var amount = allocatedIrr == settlement.IrrAmount
                ? settlement.Amount
                : MoneyMath.RoundTo(allocatedIrr / settlement.RateIrr, settlement.DecimalPlaces);
            amount = Math.Min(settlement.Amount, amount);
            components.Add(new CustomerBalanceComponent(settlement.CurrencyCode, amount, allocatedIrr));
            remainingIrr -= allocatedIrr;
        }

        if (remainingIrr > 0m)
        {
            var amount = accountCurrencyCode == CurrencyCodes.Irr
                ? MoneyMath.RoundIrr(remainingIrr)
                : MoneyMath.RoundTo(remainingIrr / accountRateIrr, accountDecimalPlaces);
            components.Add(new CustomerBalanceComponent(accountCurrencyCode, amount, remainingIrr));
        }
        return components;
    }

    private sealed record CustomerBalanceComponent(string CurrencyCode, decimal Amount, decimal IrrAmount);

    private static void AddProfitLoss(List<JournalLineDraft> lines, decimal profit)
    {
        if (profit > 0m)
        {
            lines.Add(new JournalLineDraft(AccountCodes.FxProfit, 0m, profit));
        }
        else if (profit < 0m)
        {
            lines.Add(new JournalLineDraft(AccountCodes.FxLoss, -profit, 0m));
        }
    }

    /// <summary>
    /// ابطال یک سند (معامله، افتتاحیه یا سند دستی). سند معکوس ثبت می‌شود و موجودی و بهای فروش‌های بعدی بازمحاسبه می‌شود.
    /// اگر در هر لحظه‌ای ریال یا ارز منفی شود، ابطال رد می‌شود.
    /// </summary>
    public static PostingDraft PlanVoid(BranchLedger ledger, DocRef target, string reason, int userId, DateTime now)
    {
        if (target.Id <= 0)
        {
            throw new BusinessRuleException("سند انتخابی یافت نشد.");
        }
        return Compose(ledger, SourceTypes.Void, target, userId, now, target, reason, Array.Empty<LedgerEvent>(), null);
    }

    /// <summary>
    /// موجودی افتتاحیه. برای ریال، quantity مبلغ ریال است. برای ارز، نرخ ریالی واحد لازم است.
    /// </summary>
    public static PostingDraft PlanOpening(
        BranchLedger ledger,
        CurrencyInfo currency,
        decimal quantity,
        decimal? unitRateIrr,
        int userId,
        DateTime occurredAt,
        DateTime now,
        DocRef? replaces = null,
        string? replaceReason = null)
    {
        var code = currency.Code;
        var openingRef = new DocRef(LedgerDocKind.Opening, 0);
        if (code == CurrencyCodes.Irr)
        {
            if (quantity <= 0 || quantity != MoneyMath.RoundIrr(quantity))
            {
                throw new BusinessRuleException("مبلغ افتتاحیه ریال باید عددی صحیح و بزرگ‌تر از صفر باشد.");
            }

            var cashEvent = new LedgerEvent(LedgerDocKind.Opening, 0, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                occurredAt, NewEventSeq, 0m, 0m, 0m, quantity, 0m, 0m);
            var irrJournal = new JournalDraft("موجودی افتتاحیه ریال", occurredAt, new[]
            {
                new JournalLineDraft(AccountCodes.IrrCash, quantity, 0m),
                new JournalLineDraft(AccountCodes.OpeningCapital, 0m, quantity),
            }, SourceTypes.Opening, openingRef);
            var irrOpening = new OpeningDraft(CurrencyCodes.Irr, quantity, null, quantity, occurredAt, userId, ReplacedId(replaces));
            return Compose(ledger, OpeningAction(replaces), openingRef, userId, now, replaces, replaceReason,
                new[] { cashEvent }, _ => new NewDocumentResult(null, irrOpening, null, new[] { irrJournal }));
        }

        if (!currency.IsActive)
        {
            throw new BusinessRuleException("این ارز غیرفعال است.");
        }
        TradePlanner.ValidateQuantity(quantity, currency);
        if (unitRateIrr is null)
        {
            throw new BusinessRuleException("نرخ ریالی موجودی افتتاحیه را وارد کنید.");
        }
        var rate = unitRateIrr.Value;
        TradePlanner.ValidateRate(rate);
        var costIrr = MoneyMath.RoundIrr(quantity * rate);
        if (costIrr <= 0)
        {
            throw new BusinessRuleException("بهای ریالی موجودی افتتاحیه صفر است.");
        }

        var acquire = new LedgerEvent(LedgerDocKind.Opening, 0, LedgerEventKind.Acquire, code, occurredAt, NewEventSeq,
            quantity, costIrr, 0m, 0m, 0m, 0m);
        var description = $"موجودی افتتاحیه {code}: {MoneyMath.FormatAmount(quantity, currency.DecimalPlaces)} واحد با نرخ {MoneyMath.FormatRate(rate)}";
        var journal = new JournalDraft(description, occurredAt, new[]
        {
            new JournalLineDraft(AccountCodes.ForeignCash(code), costIrr, 0m),
            new JournalLineDraft(AccountCodes.OpeningCapital, 0m, costIrr),
        }, SourceTypes.Opening, openingRef);
        var opening = new OpeningDraft(code, quantity, rate, costIrr, occurredAt, userId, ReplacedId(replaces));
        return Compose(ledger, OpeningAction(replaces), openingRef, userId, now, replaces, replaceReason,
            new[] { acquire }, _ => new NewDocumentResult(null, opening, null, new[] { journal }));
    }

    /// <summary>
    /// سند حسابداری دستی. سطرهای آن باید متوازن باشند. حساب‌های موجودی ارز پذیرفته نمی‌شوند.
    /// اگر سند به حساب صندوق ریال (1001) بزند، اثر آن روی موجودی صندوق هم بررسی می‌شود.
    /// </summary>
    public static PostingDraft PlanManual(
        BranchLedger ledger,
        IReadOnlyDictionary<string, AccountInfo> accounts,
        string description,
        IReadOnlyList<JournalLineDraft> lines,
        int userId,
        DateTime occurredAt,
        DateTime now,
        DocRef? replaces = null,
        string? replaceReason = null)
    {
        var cleanDescription = (description ?? string.Empty).Trim();
        if (cleanDescription.Length == 0)
        {
            throw new BusinessRuleException("شرح سند را وارد کنید.");
        }
        if (cleanDescription.Length > MaxDescriptionLength)
        {
            throw new BusinessRuleException($"شرح سند نمی‌تواند بیش از {MaxDescriptionLength} کاراکتر باشد.");
        }
        foreach (var line in lines)
        {
            if (!accounts.TryGetValue(line.AccountCode, out var account) || !account.IsPostable)
            {
                throw new BusinessRuleException($"حساب {line.AccountCode} وجود ندارد یا سند نمی‌گیرد (غیرفعال است یا زیرمجموعه دارد).");
            }
            if (line.AccountCode.StartsWith(ForeignInventoryPrefix, StringComparison.Ordinal))
            {
                throw new BusinessRuleException("حساب‌های موجودی ارز فقط از راه معامله و موجودی افتتاحیه تغییر می‌کنند.");
            }
            if (line.Debit != MoneyMath.RoundIrr(line.Debit) || line.Credit != MoneyMath.RoundIrr(line.Credit))
            {
                throw new BusinessRuleException("مبلغ سطرهای سند دستی باید عدد صحیح ریال باشد.");
            }
        }

        var manualRef = new DocRef(LedgerDocKind.Manual, 0);
        var journal = new JournalDraft(cleanDescription, occurredAt, lines, SourceTypes.Manual, manualRef)
        {
            ReplacesId = ReplacedId(replaces),
        };
        var irrDelta = lines.Where(l => l.AccountCode == AccountCodes.IrrCash).Sum(l => l.Debit - l.Credit);
        var events = irrDelta == 0
            ? Array.Empty<LedgerEvent>()
            : new[]
            {
                new LedgerEvent(LedgerDocKind.Manual, 0, LedgerEventKind.CashOnly, CurrencyCodes.Irr, occurredAt,
                    NewEventSeq, 0m, 0m, 0m, irrDelta, 0m, 0m),
            };
        return Compose(ledger, replaces is null ? "MANUAL_CREATE" : "MANUAL_REPLACE", manualRef, userId, now,
            replaces, replaceReason, events, _ => new NewDocumentResult(null, null, null, new[] { journal }));
    }

    /// <summary>
    /// رسید یا پرداخت مستقل از معامله. دریافت، حساب دریافتنی مشتری را بستانکار می‌کند؛ پرداخت، حساب پرداختنی را بدهکار.
    /// پرداخت ارزی با میانگین موزون صندوق ارزش‌گذاری و در صورت تفاوت، سود/زیان ارزی شناسایی می‌شود.
    /// </summary>
    public static PostingDraft PlanCashTransaction(
        BranchLedger ledger,
        CurrencyInfo currency,
        CurrencyInfo balanceCurrency,
        CashTransactionInput input,
        decimal accountingRateIrr,
        decimal balanceAmount,
        decimal balanceBefore,
        DateTime occurredAt,
        int userId,
        DateTime now,
        DocRef? replaces = null,
        string? replaceReason = null)
    {
        if (input.BranchId <= 0 || input.BranchId != ledger.BranchId)
        {
            throw new BusinessRuleException("شعبه‌ی دریافت/پرداخت با وضعیت صندوق همخوانی ندارد.");
        }
        if (input.CustomerId <= 0)
        {
            throw new BusinessRuleException("مشتری را انتخاب کنید.");
        }
        if (!Enum.IsDefined(input.Direction) || !Enum.IsDefined(input.RateMode))
        {
            throw new BusinessRuleException("نوع یا روش نرخ اطلاع‌رسانی نامعتبر است.");
        }
        if (!currency.IsActive || !balanceCurrency.IsActive)
        {
            throw new BusinessRuleException("ارز صندوق یا ارز حساب مشتری غیرفعال است.");
        }
        TradePlanner.ValidateQuantity(input.Amount, currency);
        TradePlanner.ValidateQuantity(balanceAmount, balanceCurrency);

        var code = currency.Code;
        if (code == CurrencyCodes.Irr)
        {
            accountingRateIrr = 1m;
        }
        else
        {
            TradePlanner.ValidateRate(accountingRateIrr);
        }

        var balanceCode = balanceCurrency.Code;
        if (!string.IsNullOrWhiteSpace(input.BalanceCurrencyCode)
            && !string.Equals(input.BalanceCurrencyCode, balanceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException("ارز مانده‌ی مشتری با ارز انتخاب‌شده همخوانی ندارد.");
        }
        if (code == balanceCode && balanceAmount != input.Amount)
        {
            throw new BusinessRuleException("وقتی ارز صندوق و حساب مشتری یکسان است، تسویه باید ۱:۱ باشد.");
        }
        if (code != CurrencyCodes.Irr && input.RateIrr is { } informationRate)
        {
            TradePlanner.ValidateRate(informationRate);
        }
        if (balanceAmount <= 0m)
        {
            throw new BusinessRuleException("مبلغ حساب مشتری باید بزرگ‌تر از صفر باشد.");
        }

        var irrAmount = code == CurrencyCodes.Irr
            ? MoneyMath.RoundIrr(input.Amount)
            : MoneyMath.RoundIrr(input.Amount * accountingRateIrr);
        if (irrAmount <= 0m)
        {
            throw new BusinessRuleException("ارزش دفتری دریافت/پرداخت صفر است.");
        }

        var note = TradePlanner.CleanDetails(null, null, input.Note).Note;
        var reference = new DocRef(LedgerDocKind.CashTransaction, 0);
        var isReceipt = input.Direction == CashTransactionDirection.Receipt;
        var events = code == CurrencyCodes.Irr
            ? new[]
            {
                new LedgerEvent(LedgerDocKind.CashTransaction, 0, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                    occurredAt, NewEventSeq, 0m, irrAmount, 0m,
                    isReceipt ? input.Amount : -input.Amount, 0m, 0m),
            }
            : new[]
            {
                new LedgerEvent(LedgerDocKind.CashTransaction, 0,
                    isReceipt ? LedgerEventKind.Acquire : LedgerEventKind.Dispose,
                    code, occurredAt, NewEventSeq, input.Amount, irrAmount, 0m, 0m, 0m, 0m),
            };

        var description = $"{(isReceipt ? "دریافت از مشتری" : "پرداخت به مشتری")}؛ " +
            $"{MoneyMath.FormatAmount(input.Amount, currency.DecimalPlaces)} {code}";
        if (balanceCode != code)
        {
            description += $"؛ تسویه‌ی حساب به ارز {balanceCode} ({MoneyMath.FormatAmount(balanceAmount, balanceCurrency.DecimalPlaces)})";
        }
        if (note is not null)
        {
            description += "؛ " + note;
        }
        if (description.Length > MaxDescriptionLength)
        {
            description = description[..MaxDescriptionLength];
        }

        var sourceType = isReceipt ? SourceTypes.CashReceipt : SourceTypes.CashPayment;
        return Compose(
            ledger,
            isReceipt
                ? (replaces is null ? "CASH_RECEIPT_CREATE" : "CASH_RECEIPT_REPLACE")
                : (replaces is null ? "CASH_PAYMENT_CREATE" : "CASH_PAYMENT_REPLACE"),
            reference,
            userId,
            now,
            replaces,
            replaceReason,
            events,
            state =>
            {
                var disposal = !isReceipt && code != CurrencyCodes.Irr
                    ? state.Disposals[reference]
                    : default;
                var costIrr = !isReceipt && code != CurrencyCodes.Irr ? disposal.CostIrr : irrAmount;
                var profitIrr = !isReceipt && code != CurrencyCodes.Irr ? disposal.ProfitIrr : 0m;
                var rateInfo = code == CurrencyCodes.Irr ? 1m : (input.RateIrr ?? accountingRateIrr);
                var transaction = new CashTransactionDraft(
                    input.Direction,
                    input.CustomerId,
                    code,
                    input.Amount,
                    balanceCode,
                    balanceAmount,
                    input.RateMode,
                    rateInfo,
                    irrAmount,
                    costIrr,
                    profitIrr,
                    note,
                    occurredAt,
                    userId,
                    ReplacedId(replaces));

                var cashAccount = code == CurrencyCodes.Irr ? AccountCodes.IrrCash : AccountCodes.ForeignCash(code);
                var lines = CashTransactionJournalLines(
                    cashAccount,
                    input.Direction,
                    input.CustomerId,
                    balanceCode,
                    balanceAmount,
                    balanceBefore,
                    irrAmount,
                    costIrr,
                    profitIrr);
                var journal = new JournalDraft(description, occurredAt, lines, sourceType, reference);
                return new NewDocumentResult(null, null, transaction, new[] { journal });
            });
    }

    private static IReadOnlyList<JournalLineDraft> CashTransactionJournalLines(
        string cashAccount,
        CashTransactionDirection direction,
        int customerId,
        string balanceCurrencyCode,
        decimal balanceAmount,
        decimal balanceBefore,
        decimal irrAmount,
        decimal costIrr,
        decimal profitIrr)
    {
        var lines = new List<JournalLineDraft>();
        if (direction == CashTransactionDirection.Receipt)
        {
            lines.Add(new JournalLineDraft(cashAccount, irrAmount, 0m));
            var receivableAmount = Math.Min(balanceAmount, Math.Max(0m, balanceBefore));
            var payableAmount = balanceAmount - receivableAmount;
            var receivableIrr = MoneyMath.RoundIrr(irrAmount * receivableAmount / balanceAmount);
            var payableIrr = irrAmount - receivableIrr;
            if (receivableAmount > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, 0m, receivableIrr,
                    customerId, balanceCurrencyCode, -receivableAmount));
            }
            if (payableAmount > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, 0m, payableIrr,
                    customerId, balanceCurrencyCode, -payableAmount));
            }
        }
        else
        {
            var payableAmount = Math.Min(balanceAmount, Math.Max(0m, -balanceBefore));
            var receivableAmount = balanceAmount - payableAmount;
            var payableIrr = MoneyMath.RoundIrr(irrAmount * payableAmount / balanceAmount);
            var receivableIrr = irrAmount - payableIrr;
            if (payableAmount > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, payableIrr, 0m,
                    customerId, balanceCurrencyCode, payableAmount));
            }
            if (receivableAmount > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, receivableIrr, 0m,
                    customerId, balanceCurrencyCode, receivableAmount));
            }
            if (costIrr > 0m)
            {
                lines.Add(new JournalLineDraft(cashAccount, 0m, costIrr));
            }
            AddProfitLoss(lines, profitIrr);
        }

        return lines;
    }

    /// <summary>نتیجه‌ی ساخت سند جدید: معامله، افتتاحیه، رسید/پرداخت و سندهای حسابداری آن.</summary>
    private sealed record NewDocumentResult(
        TradeDraft? Trade,
        OpeningDraft? Opening,
        CashTransactionDraft? CashTransaction,
        IReadOnlyList<JournalDraft> Journals);

    /// <summary>
    /// موتور مشترک: حذف رویدادهای سند ابطال‌شده، افزودن رویدادهای سند جدید، بازپخش کل تاریخچه،
    /// تعیین بهای فروش‌های تغییرکرده و تولید همه‌ی سطرهای صندوق، موجودی و سند تعدیل.
    /// </summary>
    private static PostingDraft Compose(
        BranchLedger ledger,
        string action,
        DocRef entity,
        int userId,
        DateTime now,
        DocRef? voidTarget,
        string? voidReason,
        IReadOnlyList<LedgerEvent> newEvents,
        Func<LedgerState, NewDocumentResult>? finish)
    {
        // Persisted timestamps use DATETIME2(0); normalize here so an as-of query at the same instant
        // cannot sort immediately before a void or adjustment created from an unrounded DateTime.Now.
        now = OccurrenceRules.Truncate(now);

        VoidDraft? voidDraft = null;
        var removed = new List<LedgerEvent>();
        if (voidTarget is { } target)
        {
            if (!ledger.ActiveDocs.Contains(target))
            {
                throw new BusinessRuleException("سند انتخابی یافت نشد یا قبلاً باطل شده است.");
            }
            var reason = ValidateReason(voidReason);
            removed.AddRange(ledger.Events.Where(e => e.Doc == target));
            voidDraft = new VoidDraft(target, reason, $"ابطال {DocumentName(target.Kind)} شماره {target.Id}: {reason}", now);
        }

        var kept = ledger.Events.Where(e => voidTarget is null || e.Doc != voidTarget.Value).ToList();

        // وضعیت فعلی باید با مقادیر ذخیره‌شده یکی باشد؛ در غیر این صورت داده ناسازگار است و ثبتی انجام نمی‌شود.
        var before = LedgerEngine.Replay(ledger.Events);
        VerifyStored(before, ledger);

        // اگر تاریخچه‌ی جدید از نظر موجودی ریال یا ارز معتبر نباشد، Replay خطای قواعد کسب‌وکار پرتاب می‌کند.
        var after = LedgerEngine.Replay(kept.Concat(newEvents));
        var created = finish is null
            ? new NewDocumentResult(null, null, null, Array.Empty<JournalDraft>())
            : finish(after);

        var journals = new List<JournalDraft>(created.Journals);
        var costUpdates = new List<TradeCostUpdate>();
        var settlementCostUpdates = new List<TradeSettlementCostUpdate>();
        var cashTransactionCostUpdates = new List<CashTransactionCostUpdate>();
        foreach (var disposal in kept.Where(e => e.Kind == LedgerEventKind.Dispose))
        {
            var result = disposal.SettlementLineNumber is { } lineNumber
                ? after.SettlementDisposals[(disposal.Doc, lineNumber)]
                : after.Disposals[disposal.Doc];
            if (result.CostIrr == disposal.StoredCostIrr && result.ProfitIrr == disposal.StoredProfitIrr)
            {
                continue;
            }

            if (disposal.SettlementLineNumber is { } settlementLine)
            {
                settlementCostUpdates.Add(new TradeSettlementCostUpdate(disposal.DocId, settlementLine, result.CostIrr, result.ProfitIrr));
            }
            else if (disposal.DocKind == LedgerDocKind.CashTransaction)
            {
                cashTransactionCostUpdates.Add(new CashTransactionCostUpdate(disposal.DocId, result.CostIrr, result.ProfitIrr));
            }
            journals.Add(AdjustmentJournal(disposal, result.CostIrr, now));
        }

        foreach (var tradeEvents in kept.Where(e => e.DocKind == LedgerDocKind.Trade).GroupBy(e => e.Doc))
        {
            var main = tradeEvents.Single(e => e.SettlementLineNumber is null);
            var mainCost = main.Kind == LedgerEventKind.Dispose ? after.Disposals[main.Doc].CostIrr : main.StoredCostIrr;
            var baseProfit = main.Kind == LedgerEventKind.Dispose ? after.Disposals[main.Doc].ProfitIrr : 0m;
            var settlementProfit = tradeEvents
                .Where(e => e.SettlementLineNumber is not null && e.Kind == LedgerEventKind.Dispose)
                .Sum(e => after.SettlementDisposals[(e.Doc, e.SettlementLineNumber!.Value)].ProfitIrr);
            var totalProfit = baseProfit + settlementProfit;
            if (mainCost != main.StoredCostIrr || totalProfit != main.StoredProfitIrr)
            {
                costUpdates.Add(new TradeCostUpdate(main.DocId, mainCost, totalProfit));
            }
        }

        var cash = new List<CashMovementDraft>();
        foreach (var e in removed)
        {
            cash.AddRange(CashDrafts(ledger, e, reverse: true, now));
        }
        foreach (var e in newEvents)
        {
            cash.AddRange(CashDrafts(ledger, e, reverse: false, now));
        }

        var irrCashDelta = cash.Where(c => c.CurrencyCode == CurrencyCodes.Irr).Sum(c => c.Delta);
        if (after.IrrBalance != ledger.StoredIrrBalance + irrCashDelta)
        {
            throw new InvalidOperationException("ناسازگاری داخلی: موجودی ریال پس از ثبت با سطرهای صندوق برابر نیست.");
        }

        var inventory = new List<InventoryDraft>();
        foreach (var (code, stored) in ledger.StoredPools)
        {
            var delta = cash.Where(c => c.CurrencyCode == code).Sum(c => c.Delta);
            var pool = after.Pool(code);
            if (pool.Quantity != stored.Quantity + delta)
            {
                throw new InvalidOperationException($"ناسازگاری داخلی: موجودی {code} پس از ثبت با سطرهای صندوق برابر نیست.");
            }
            inventory.Add(new InventoryDraft(code, stored.CostIrr, pool.CostIrr));
        }

        return new PostingDraft(
            Action: action,
            BranchId: ledger.BranchId,
            UserId: userId,
            Now: now,
            ExpectedVersion: ledger.Version,
            Entity: entity,
            Void: voidDraft,
            Trade: created.Trade,
            Opening: created.Opening,
            CashTransaction: created.CashTransaction,
            Journals: journals,
            CashMovements: cash,
            Inventory: inventory,
            CostUpdates: costUpdates,
            AuditDetails: BuildDetails(action, voidDraft, costUpdates.Count + settlementCostUpdates.Count + cashTransactionCostUpdates.Count),
            SettlementCostUpdates: settlementCostUpdates,
            CashTransactionCostUpdates: cashTransactionCostUpdates);
    }

    /// <summary>
    /// سند تعدیل برای یک فروش: بهای تمام‌شده از oldCost به newCost می‌رسد و در نتیجه سود یا زیان آن تغییر می‌کند.
    /// سطرها با هم خنثی‌سازی می‌شوند: موجودی ارز و حساب‌های سود/زیان به‌اندازه‌ی تفاوت تنظیم می‌شوند.
    /// </summary>
    private static JournalDraft AdjustmentJournal(LedgerEvent sale, decimal newCost, DateTime now)
    {
        var oldCost = sale.StoredCostIrr;
        var oldProfit = sale.ValueIrr - oldCost;
        var newProfit = sale.ValueIrr - newCost;

        // مقدار نگهداری‌شده به‌صورت «بدهکار خالص» هر حساب است.
        var net = new Dictionary<string, decimal>(StringComparer.Ordinal);
        AddTo(net, AccountCodes.ForeignCash(sale.CurrencyCode), -(newCost - oldCost));
        AddTo(net, AccountCodes.FxProfit, Math.Max(oldProfit, 0m) - Math.Max(newProfit, 0m));
        AddTo(net, AccountCodes.FxLoss, Math.Max(-newProfit, 0m) - Math.Max(-oldProfit, 0m));

        var lines = net
            .Where(kv => kv.Value != 0m)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value > 0m
                ? new JournalLineDraft(kv.Key, kv.Value, 0m)
                : new JournalLineDraft(kv.Key, 0m, -kv.Value))
            .ToList();

        var isCashTransaction = sale.DocKind == LedgerDocKind.CashTransaction;
        var description = isCashTransaction
            ? $"تعدیل بهای تمام‌شده‌ی پرداخت ارزی شماره {sale.DocId} بر اثر تغییر تاریخچه‌ی موجودی"
            : $"تعدیل بهای تمام‌شده‌ی معامله‌ی شماره {sale.DocId} بر اثر تغییر تاریخچه‌ی موجودی";
        return new JournalDraft(
            description,
            now,
            lines,
            isCashTransaction ? SourceTypes.CashAdjustment : SourceTypes.Adjust,
            sale.Doc);
    }

    private static IEnumerable<CashMovementDraft> CashDrafts(BranchLedger ledger, LedgerEvent e, bool reverse, DateTime now)
    {
        var sign = reverse ? -1m : 1m;
        var refType = reverse ? SourceTypes.Void : RefTypeOf(e.DocKind);
        var at = reverse ? now : e.OccurredAt;
        var prefix = reverse ? "ابطال: " : string.Empty;

        if (e.IrrDelta != 0m)
        {
            yield return new CashMovementDraft(CurrencyCodes.Irr, ledger.StoredIrrBalance, sign * e.IrrDelta,
                prefix + IrrDescription(e), at, refType, e.Doc);
        }

        if (e.Kind != LedgerEventKind.CashOnly)
        {
            if (!ledger.StoredPools.TryGetValue(e.CurrencyCode, out var pool))
            {
                throw new InvalidOperationException($"صندوق ارز {e.CurrencyCode} برای این شعبه وجود ندارد.");
            }
            var direction = e.Kind == LedgerEventKind.Acquire ? 1m : -1m;
            yield return new CashMovementDraft(e.CurrencyCode, pool.Quantity, sign * direction * e.Quantity,
                prefix + ForeignDescription(e), at, refType, e.Doc);
        }
    }

    private static void VerifyStored(LedgerState state, BranchLedger ledger)
    {
        if (state.IrrBalance != ledger.StoredIrrBalance)
        {
            throw new InvalidOperationException("ناسازگاری داده‌ها: موجودی ریال صندوق با تاریخچه‌ی معاملات برابر نیست.");
        }
        foreach (var (code, stored) in ledger.StoredPools)
        {
            var pool = state.Pool(code);
            if (pool.Quantity != stored.Quantity || pool.CostIrr != stored.CostIrr)
            {
                throw new InvalidOperationException($"ناسازگاری داده‌ها: موجودی یا بهای {code} با تاریخچه‌ی معاملات برابر نیست.");
            }
        }
    }

    private static void ValidateTrade(TradeInput input, int ledgerBranchId, CurrencyInfo currency)
    {
        if (input.BranchId <= 0)
        {
            throw new BusinessRuleException("شعبه‌ی معامله را انتخاب کنید.");
        }
        if (input.BranchId != ledgerBranchId)
        {
            throw new BusinessRuleException("شعبه‌ی معامله با وضعیت صندوق همخوانی ندارد.");
        }
        if (currency.Code == CurrencyCodes.Irr)
        {
            throw new BusinessRuleException("ریال ارز معاملاتی نیست.");
        }
        if (!currency.IsActive)
        {
            throw new BusinessRuleException("این ارز غیرفعال است.");
        }
        TradePlanner.ValidateQuantity(input.Amount, currency);
        TradePlanner.ValidateRate(input.Rate);
    }

    private static string ValidateReason(string? reason)
    {
        var clean = (reason ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new BusinessRuleException("دلیل ابطال یا ویرایش را وارد کنید.");
        }
        if (clean.Length > MaxReasonLength)
        {
            throw new BusinessRuleException($"دلیل ابطال نمی‌تواند بیش از {MaxReasonLength} کاراکتر باشد.");
        }
        return clean;
    }

    private static long? ReplacedId(DocRef? replaces) => replaces is { Id: > 0 } r ? r.Id : (long?)null;

    private static string OpeningAction(DocRef? replaces) => replaces is null ? "OPENING_CREATE" : "OPENING_REPLACE";

    private static void AddTo(Dictionary<string, decimal> map, string key, decimal amount) =>
        map[key] = (map.TryGetValue(key, out var current) ? current : 0m) + amount;

    private static string RefTypeOf(LedgerDocKind kind) => kind switch
    {
        LedgerDocKind.Trade => SourceTypes.Trade,
        LedgerDocKind.Opening => SourceTypes.Opening,
        LedgerDocKind.CashTransaction => SourceTypes.CashTransaction,
        _ => SourceTypes.Manual,
    };

    private static string IrrDescription(LedgerEvent e) => e.DocKind switch
    {
        LedgerDocKind.Trade => e.IrrDelta < 0m ? "پرداخت ریال بابت خرید ارز" : "دریافت ریال بابت فروش ارز",
        LedgerDocKind.Opening => "موجودی افتتاحیه ریال",
        LedgerDocKind.CashTransaction => e.IrrDelta < 0m ? "پرداخت ریال به مشتری" : "دریافت ریال از مشتری",
        _ => "سند دستی روی صندوق ریال",
    };

    private static string ForeignDescription(LedgerEvent e)
    {
        if (e.DocKind == LedgerDocKind.CashTransaction)
        {
            return e.Kind == LedgerEventKind.Dispose ? "پرداخت ارز به مشتری" : "دریافت ارز از مشتری";
        }
        if (e.Kind == LedgerEventKind.Dispose)
        {
            return "پرداخت ارز به مشتری";
        }
        return e.DocKind == LedgerDocKind.Opening ? "موجودی افتتاحیه ارز" : "دریافت ارز از مشتری";
    }

    private static string DocumentName(LedgerDocKind kind) => kind switch
    {
        LedgerDocKind.Trade => "معامله",
        LedgerDocKind.Opening => "سند افتتاحیه",
        LedgerDocKind.CashTransaction => "دریافت/پرداخت",
        _ => "سند دستی",
    };

    private static string BuildDetails(string action, VoidDraft? voidDraft, int costChanges)
    {
        var parts = new List<string> { action };
        if (voidDraft is not null)
        {
            parts.Add(voidDraft.Description);
        }
        if (costChanges > 0)
        {
            parts.Add($"تعدیل بهای تمام‌شده‌ی {costChanges} خروج ارز");
        }
        return string.Join(" | ", parts);
    }
}
