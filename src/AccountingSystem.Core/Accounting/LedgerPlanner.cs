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
            var lineDraft = new TradeSettlementDraft(lineNumber, direction, settlementCode, line.Amount, line.RateIrr, lineValueIrr);
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
                var trade = new TradeDraft(type, code, input.Amount, rate, irr, cost, profit, fee,
                    input.CustomerId, customer, nationalCode, note, occurredAt, userId, ReplacedId(replaces),
                    mode, rateMode, crossRate, input.CustomerOffsetIrr, completedSettlements,
                    mode == TradeSettlementMode.Direct
                        ? input.SettlementCurrencyCode ?? completedSettlements.FirstOrDefault()?.CurrencyCode
                        : null);
                var lines = TradeJournalLines(code, irr, fee, cost, baseProfit, input.CustomerId,
                    input.CustomerOffsetIrr, completedSettlements, type);
                var journal = new JournalDraft(description, occurredAt, lines, SourceTypes.Trade, tradeRef);
                return new NewDocumentResult(trade, null, new[] { journal });
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
        IReadOnlyList<TradeSettlementDraft> settlements,
        TradeType type)
    {
        var lines = new List<JournalLineDraft>();
        if (type == TradeType.Buy)
        {
            lines.Add(new JournalLineDraft(AccountCodes.ForeignCash(currencyCode), irr, 0m));
            lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, 0m, irr - fee, customerId));
            if (fee > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.FeeIncome, 0m, fee));
            }
            if (customerOffsetIrr > 0m)
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, customerOffsetIrr, 0m, customerId));
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, 0m, customerOffsetIrr, customerId));
            }
        }
        else
        {
            lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, irr + fee, 0m, customerId));
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
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, 0m, customerOffsetIrr, customerId));
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, customerOffsetIrr, 0m, customerId));
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
                lines.Add(new JournalLineDraft(AccountCodes.CustomerReceivable, 0m, settlement.IrrAmount, customerId));
            }
            else
            {
                lines.Add(new JournalLineDraft(AccountCodes.CustomerPayable, settlement.IrrAmount, 0m, customerId));
                if (settlement.CostIrr > 0m)
                {
                    lines.Add(new JournalLineDraft(cashAccount, 0m, settlement.CostIrr));
                }
                AddProfitLoss(lines, settlement.ProfitIrr);
            }
        }

        return lines;
    }

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
                new[] { cashEvent }, _ => new NewDocumentResult(null, irrOpening, new[] { irrJournal }));
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
            new[] { acquire }, _ => new NewDocumentResult(null, opening, new[] { journal }));
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
            replaces, replaceReason, events, _ => new NewDocumentResult(null, null, new[] { journal }));
    }

    /// <summary>نتیجه‌ی ساخت سند جدید: سند معامله یا افتتاحیه و سندهای حسابداری آن.</summary>
    private sealed record NewDocumentResult(TradeDraft? Trade, OpeningDraft? Opening, IReadOnlyList<JournalDraft> Journals);

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
            ? new NewDocumentResult(null, null, Array.Empty<JournalDraft>())
            : finish(after);

        var journals = new List<JournalDraft>(created.Journals);
        var costUpdates = new List<TradeCostUpdate>();
        var settlementCostUpdates = new List<TradeSettlementCostUpdate>();
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
            Journals: journals,
            CashMovements: cash,
            Inventory: inventory,
            CostUpdates: costUpdates,
            AuditDetails: BuildDetails(action, voidDraft, costUpdates.Count + settlementCostUpdates.Count),
            SettlementCostUpdates: settlementCostUpdates);
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

        return new JournalDraft(
            $"تعدیل بهای تمام‌شده‌ی معامله‌ی شماره {sale.DocId} بر اثر تغییر تاریخچه‌ی موجودی",
            now,
            lines,
            SourceTypes.Adjust,
            new DocRef(LedgerDocKind.Trade, sale.DocId));
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
        _ => SourceTypes.Manual,
    };

    private static string IrrDescription(LedgerEvent e) => e.DocKind switch
    {
        LedgerDocKind.Trade => e.IrrDelta < 0m ? "پرداخت ریال بابت خرید ارز" : "دریافت ریال بابت فروش ارز",
        LedgerDocKind.Opening => "موجودی افتتاحیه ریال",
        _ => "سند دستی روی صندوق ریال",
    };

    private static string ForeignDescription(LedgerEvent e)
    {
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
            parts.Add($"تعدیل بهای تمام‌شده‌ی {costChanges} فروش");
        }
        return string.Join(" | ", parts);
    }
}
