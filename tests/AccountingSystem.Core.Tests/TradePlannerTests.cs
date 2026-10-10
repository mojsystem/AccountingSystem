using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public class TradePlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 10, 0, 0);
    private static readonly CurrencyInfo Usd = new("USD", "دلار آمریکا", 2, true);

    [Fact]
    public void Buy_creates_balanced_journal_and_adds_cost_to_inventory()
    {
        var snapshot = new TradeSnapshot(1, Usd, 10_000_000m, 0m, 0m);
        var input = new TradeInput(1, "USD", 10m, 1_000_000m, "علی", null, null);

        var posting = TradePlanner.PlanBuy(input, snapshot, 1, Now);

        Assert.NotNull(posting.Trade);
        Assert.Equal(1, posting.BranchId);
        Assert.Equal(TradeType.Buy, posting.Trade!.Type);
        Assert.Equal(10_000_000m, posting.Trade.IrrAmount);
        Assert.Equal(0m, posting.Trade.ProfitIrr);
        Assert.Equal(-10_000_000m, posting.CashMovements.Single(m => m.CurrencyCode == "IRR").Delta);
        Assert.Equal(10m, posting.CashMovements.Single(m => m.CurrencyCode == "USD").Delta);
        Assert.Equal(10_000_000m, posting.Inventory.Single().NewCostIrr);
        Assert.Equal(posting.Journals[0].Lines.Sum(l => l.Debit), posting.Journals[0].Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Buy_is_rejected_when_irr_balance_is_insufficient()
    {
        var snapshot = new TradeSnapshot(1, Usd, 9_999_999m, 0m, 0m);
        var input = new TradeInput(1, "USD", 10m, 1_000_000m, null, null, null);

        var ex = Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanBuy(input, snapshot, 1, Now));
        Assert.Contains("کافی نیست", ex.Message);
    }

    [Fact]
    public void Sell_with_profit_uses_weighted_average_cost()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 100m, 100_000_000m);
        var input = new TradeInput(1, "USD", 40m, 1_200_000m, null, null, null);

        var posting = TradePlanner.PlanSell(input, snapshot, 1, Now);

        Assert.Equal(48_000_000m, posting.Trade!.IrrAmount);
        Assert.Equal(40_000_000m, posting.Trade.CostIrr);
        Assert.Equal(8_000_000m, posting.Trade.ProfitIrr);
        Assert.Equal(60_000_000m, posting.Inventory.Single().NewCostIrr);
        Assert.Equal(8_000_000m, posting.Journals[0].Lines.Single(l => l.AccountCode == AccountCodes.FxProfit).Credit);
        // معین مشتری در همان سند به‌صورت بدهکار و بستانکار تسویه می‌شود؛ گردش ناخالص سند دوبرابر ارزش معامله است.
        Assert.Equal(96_000_000m, posting.Journals[0].Lines.Sum(l => l.Debit));
        Assert.Equal(96_000_000m, posting.Journals[0].Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Sell_with_loss_debits_fx_loss_account()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 100m, 100_000_000m);
        var input = new TradeInput(1, "USD", 10m, 900_000m, null, null, null);

        var posting = TradePlanner.PlanSell(input, snapshot, 1, Now);

        Assert.Equal(-1_000_000m, posting.Trade!.ProfitIrr);
        Assert.Equal(1_000_000m, posting.Journals[0].Lines.Single(l => l.AccountCode == AccountCodes.FxLoss).Debit);
        Assert.Equal(posting.Journals[0].Lines.Sum(l => l.Debit), posting.Journals[0].Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Selling_entire_stock_removes_exact_cost_basis()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 3m, 100_000_001m);
        var input = new TradeInput(1, "USD", 3m, 1_100_000m, null, null, null);

        var posting = TradePlanner.PlanSell(input, snapshot, 1, Now);

        Assert.Equal(100_000_001m, posting.Trade!.CostIrr);
        Assert.Equal(0m, posting.Inventory.Single().NewCostIrr);
    }

    [Fact]
    public void Sell_is_rejected_when_stock_is_insufficient()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 5m, 5_000_000m);
        var input = new TradeInput(1, "USD", 6m, 1_000_000m, null, null, null);

        Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanSell(input, snapshot, 1, Now));
    }

    [Fact]
    public void Buy_with_fee_pays_the_net_amount_and_credits_fee_income()
    {
        var snapshot = new TradeSnapshot(1, Usd, 10_000_000m, 0m, 0m);
        var input = new TradeInput(1, "USD", 10m, 1_000_000m, null, null, null, FeeIrr: 100_000m);

        var posting = TradePlanner.PlanBuy(input, snapshot, 1, Now);

        Assert.Equal(100_000m, posting.Trade!.FeeIrr);
        Assert.Equal(10_000_000m, posting.Trade.IrrAmount);
        Assert.Equal(10_000_000m, posting.Trade.CostIrr);
        Assert.Equal(-9_900_000m, posting.CashMovements.Single(m => m.CurrencyCode == "IRR").Delta);
        Assert.Equal(10_000_000m, posting.Inventory.Single().NewCostIrr);
        Assert.Equal(100_000m, posting.Journals[0].Lines.Single(l => l.AccountCode == AccountCodes.FeeIncome).Credit);
        Assert.Equal(posting.Journals[0].Lines.Sum(l => l.Debit), posting.Journals[0].Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Sell_with_fee_collects_fee_on_top_and_keeps_trade_profit_separate()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 100m, 100_000_000m);
        var input = new TradeInput(1, "USD", 40m, 1_200_000m, null, null, null, FeeIrr: 50_000m);

        var posting = TradePlanner.PlanSell(input, snapshot, 1, Now);

        Assert.Equal(8_000_000m, posting.Trade!.ProfitIrr);
        Assert.Equal(50_000m, posting.Trade.FeeIrr);
        Assert.Equal(48_050_000m, posting.CashMovements.Single(m => m.CurrencyCode == "IRR").Delta);
        Assert.Equal(50_000m, posting.Journals[0].Lines.Single(l => l.AccountCode == AccountCodes.FeeIncome).Credit);
        Assert.Equal(96_100_000m, posting.Journals[0].Lines.Sum(l => l.Debit));
        Assert.Equal(96_100_000m, posting.Journals[0].Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Buy_fee_must_be_smaller_than_the_trade_amount()
    {
        var snapshot = new TradeSnapshot(1, Usd, 10_000_000m, 0m, 0m);
        var input = new TradeInput(1, "USD", 10m, 1_000_000m, null, null, null, FeeIrr: 10_000_000m);

        var ex = Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanBuy(input, snapshot, 1, Now));
        Assert.Contains("کمتر", ex.Message);
    }

    [Fact]
    public void Negative_or_fractional_fee_is_rejected()
    {
        var snapshot = new TradeSnapshot(1, Usd, 10_000_000m, 100m, 100_000_000m);

        Assert.Throws<BusinessRuleException>(() =>
            TradePlanner.PlanSell(new TradeInput(1, "USD", 1m, 1_000_000m, null, null, null, FeeIrr: -1m), snapshot, 1, Now));
        Assert.Throws<BusinessRuleException>(() =>
            TradePlanner.PlanSell(new TradeInput(1, "USD", 1m, 1_000_000m, null, null, null, FeeIrr: 0.5m), snapshot, 1, Now));
    }

    [Fact]
    public void Opening_foreign_balance_credits_opening_capital()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 0m, 0m);

        var posting = TradePlanner.PlanOpeningForeign(Usd, 100m, 1_000_000m, snapshot, 1, Now);

        Assert.Equal(SourceTypes.Opening, posting.Journals[0].SourceType);
        Assert.Null(posting.Trade);
        Assert.Equal(100_000_000m, posting.Journals[0].Lines.Single(l => l.AccountCode == AccountCodes.OpeningCapital).Credit);
        Assert.Equal(100m, posting.CashMovements.Single().Delta);
        Assert.Equal(100_000_000m, posting.Inventory.Single().NewCostIrr);
    }

    [Fact]
    public void Opening_irr_requires_whole_rials()
    {
        Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanOpeningIrr(1, 1_000.5m, 0m, 1, Now));
    }

    [Fact]
    public void Quantity_with_more_decimals_than_currency_allows_is_rejected()
    {
        var snapshot = new TradeSnapshot(1, Usd, 10_000_000m, 0m, 0m);
        var input = new TradeInput(1, "USD", 10.555m, 1_000_000m, null, null, null);

        Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanBuy(input, snapshot, 1, Now));
    }

    [Fact]
    public void Trade_for_another_branch_is_rejected()
    {
        var snapshot = new TradeSnapshot(2, Usd, 10_000_000m, 0m, 0m);
        var input = new TradeInput(1, "USD", 10m, 1_000_000m, null, null, null);

        Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanBuy(input, snapshot, 1, Now));
    }

    private static BranchLedger MultiCurrencyLedger(
        decimal irrBalance,
        params (string Code, decimal Quantity, decimal CostIrr)[] stocks)
    {
        var events = new List<LedgerEvent>
        {
            new(LedgerDocKind.Opening, -100, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                DateTime.MinValue, long.MinValue, 0m, 0m, 0m, irrBalance, 0m, 0m),
        };
        var pools = new Dictionary<string, PoolBalance>(StringComparer.Ordinal);
        for (var i = 0; i < stocks.Length; i++)
        {
            var (code, quantity, cost) = stocks[i];
            pools.Add(code, new PoolBalance(quantity, cost));
            if (quantity != 0m || cost != 0m)
            {
                events.Add(new LedgerEvent(LedgerDocKind.Opening, -101 - i, LedgerEventKind.Acquire, code,
                    DateTime.MinValue, long.MinValue + i + 1, quantity, cost, 0m, 0m, 0m, 0m));
            }
        }
        return new BranchLedger(1, 5, events, new HashSet<DocRef>(), irrBalance, pools);
    }

    [Fact]
    public void Direct_trade_can_exchange_any_two_currencies_using_a_manual_cross_rate()
    {
        var ledger = MultiCurrencyLedger(0m, ("USD", 0m, 0m), ("EUR", 100m, 5_000_000m));
        var input = new TradeInput(
            1, "USD", 10m, 100_000m, "مشتری", null, null,
            CustomerId: 42,
            SettlementMode: TradeSettlementMode.Direct,
            RateMode: TradeRateMode.Direct,
            SettlementCurrencyCode: "EUR",
            CrossRate: 2m,
            SettlementLines: new[] { new TradeSettlementInput("EUR", 20m, 50_000m) },
            ValuationIrr: 1_000_000m);

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Buy, 7, Now, Now);

        Assert.Equal(1_000_000m, posting.Trade!.IrrAmount);
        Assert.Equal("EUR", posting.Trade.SettlementCurrencyCode);
        Assert.Equal(2m, posting.Trade.CrossRate);
        var settlement = Assert.Single(posting.Trade.Settlements!);
        Assert.Equal(TradeSettlementDirection.Payment, settlement.Direction);
        Assert.Equal("EUR", settlement.CurrencyCode);
        Assert.Equal(20m, settlement.Amount);
        Assert.Equal(-20m, posting.CashMovements.Single(m => m.CurrencyCode == "EUR").Delta);
        Assert.Equal(10m, posting.CashMovements.Single(m => m.CurrencyCode == "USD").Delta);
        Assert.DoesNotContain(posting.CashMovements, m => m.CurrencyCode == CurrencyCodes.Irr);
        Assert.All(posting.Journals.Single().Lines.Where(l => l.AccountCode is AccountCodes.CustomerPayable or AccountCodes.CustomerReceivable),
            line => Assert.Equal(42, line.CustomerId));
        Assert.Equal(posting.Journals.Single().Lines.Sum(l => l.Debit), posting.Journals.Single().Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Split_trade_can_receive_multiple_currencies_and_offset_the_customer_account()
    {
        var ledger = MultiCurrencyLedger(0m,
            ("USD", 10m, 700_000m), ("EUR", 0m, 0m), ("GBP", 0m, 0m));
        var input = new TradeInput(
            1, "USD", 10m, 100_000m, null, null, null,
            CustomerId: 77,
            SettlementMode: TradeSettlementMode.Split,
            RateMode: TradeRateMode.Derived,
            SettlementLines: new[]
            {
                new TradeSettlementInput("EUR", 10m, 50_000m),
                new TradeSettlementInput("GBP", 10m, 30_000m),
            },
            CustomerOffsetIrr: 200_000m,
            ValuationIrr: 1_000_000m);

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Sell, 7, Now, Now);

        Assert.Equal(300_000m, posting.Trade!.ProfitIrr);
        Assert.Equal(2, posting.Trade.Settlements!.Count);
        Assert.All(posting.Trade.Settlements, line => Assert.Equal(TradeSettlementDirection.Receipt, line.Direction));
        Assert.Equal(10m, posting.CashMovements.Single(m => m.CurrencyCode == "EUR").Delta);
        Assert.Equal(10m, posting.CashMovements.Single(m => m.CurrencyCode == "GBP").Delta);
        Assert.Equal(-10m, posting.CashMovements.Single(m => m.CurrencyCode == "USD").Delta);
        Assert.Equal(200_000m, posting.Trade.CustomerOffsetIrr);
        Assert.Equal(200_000m, posting.Journals.Single().Lines.Single(l => l.AccountCode == AccountCodes.CustomerPayable && l.Debit > 0).Debit);
        Assert.All(posting.Journals.Single().Lines.Where(l => l.AccountCode is AccountCodes.CustomerPayable or AccountCodes.CustomerReceivable),
            line => Assert.Equal(77, line.CustomerId));
        Assert.Equal(posting.Journals.Single().Lines.Sum(l => l.Debit), posting.Journals.Single().Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Customer_account_trade_records_the_unsettled_amount_without_a_cash_settlement()
    {
        var ledger = MultiCurrencyLedger(0m, ("USD", 20m, 2_000_000m));
        var input = new TradeInput(
            1, "USD", 5m, 150_000m, null, null, null,
            CustomerId: 81,
            SettlementMode: TradeSettlementMode.CustomerAccount,
            RateMode: TradeRateMode.Derived,
            SettlementLines: Array.Empty<TradeSettlementInput>(),
            ValuationIrr: 750_000m);

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Sell, 7, Now, Now);

        Assert.Empty(posting.Trade!.Settlements!);
        Assert.Equal(CurrencyCodes.Irr, posting.Trade.SettlementCurrencyCode);
        Assert.Equal(-5m, posting.CashMovements.Single().Delta);
        Assert.Equal(750_000m, posting.Journals.Single().Lines.Single(l => l.AccountCode == AccountCodes.CustomerReceivable).Debit);
        Assert.Equal(81, posting.Journals.Single().Lines.Single(l => l.AccountCode == AccountCodes.CustomerReceivable).CustomerId);
        Assert.Equal(posting.Journals.Single().Lines.Sum(l => l.Debit), posting.Journals.Single().Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Backdated_purchase_recalculates_the_cost_of_a_foreign_settlement_payment()
    {
        var initialEvents = new List<LedgerEvent>
        {
            new(LedgerDocKind.Opening, 9, LedgerEventKind.CashOnly, CurrencyCodes.Irr, Day1, 1, 100_000m, 0m, 0m, 100_000m, 0m, 0m),
            new(LedgerDocKind.Opening, 10, LedgerEventKind.Acquire, "EUR", Day1, 2, 100m, 1_000m, 0m, 0m, 0m, 0m),
            new(LedgerDocKind.Trade, 5, LedgerEventKind.Acquire, "USD", Day3, 3, 1m, 2_000m, 0m, 0m, 0m, 1_900m),
            new(LedgerDocKind.Trade, 5, LedgerEventKind.Dispose, "EUR", Day3, 4, 10m, 2_000m, 0m, 0m, 100m, 1_900m, 1),
        };
        var state = LedgerEngine.Replay(initialEvents);
        var ledger = new BranchLedger(1, 5, initialEvents, new HashSet<DocRef>
        {
            new(LedgerDocKind.Opening, 9), new(LedgerDocKind.Opening, 10), new(LedgerDocKind.Trade, 5),
        }, state.IrrBalance, state.Pools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

        var purchase = new TradeInput(1, "EUR", 100m, 100m, null, null, null);
        var posting = LedgerPlanner.PlanTrade(ledger, new CurrencyInfo("EUR", "یورو", 2, true), purchase,
            TradeType.Buy, 7, Day2, Today);

        var settlementUpdate = Assert.Single(posting.SettlementCostUpdates!);
        Assert.Equal(5, settlementUpdate.TradeId);
        Assert.Equal(1, settlementUpdate.LineNumber);
        Assert.Equal(550m, settlementUpdate.CostIrr);
        Assert.Equal(1_450m, settlementUpdate.ProfitIrr);
        var tradeUpdate = Assert.Single(posting.CostUpdates);
        Assert.Equal(5, tradeUpdate.TradeId);
        Assert.Equal(1_450m, tradeUpdate.ProfitIrr);
        Assert.Contains(posting.Journals, journal => journal.SourceType == SourceTypes.Adjust);
    }

    // ---------------------------------------------------------------------------------------------
    // تاریخچه‌ی کامل: ثبت با تاریخ گذشته، ابطال هر سند، جایگزینی، سند افتتاحیه و سند دستی.
    // ---------------------------------------------------------------------------------------------

    private static readonly DateTime Day1 = new(2026, 10, 1, 9, 0, 0);
    private static readonly DateTime Day2 = new(2026, 10, 2, 9, 0, 0);
    private static readonly DateTime Day3 = new(2026, 10, 3, 9, 0, 0);
    private static readonly DateTime Today = new(2026, 10, 4, 10, 0, 0);

    private static LedgerEvent Acquire(long tradeId, DateTime at, long seq, decimal qty, decimal cost, decimal irrDelta) =>
        new(LedgerDocKind.Trade, tradeId, LedgerEventKind.Acquire, "USD", at, seq, qty, cost, 0m, irrDelta, 0m, 0m);

    private static LedgerEvent Sell(long tradeId, DateTime at, long seq, decimal qty, decimal irr) =>
        new(LedgerDocKind.Trade, tradeId, LedgerEventKind.Dispose, "USD", at, seq, qty, irr, 0m, irr, 0m, 0m);

    private static LedgerEvent Cash(LedgerDocKind kind, long id, DateTime at, long seq, decimal delta) =>
        new(kind, id, LedgerEventKind.CashOnly, "IRR", at, seq, 0m, 0m, 0m, delta, 0m, 0m);

    /// <summary>
    /// دفتر با مقادیر ذخیره‌شده‌ی درست: بهای فروش‌ها و مانده‌ها از همان بازپخش تاریخچه به دست می‌آید.
    /// </summary>
    private static BranchLedger LedgerOf(params LedgerEvent[] events)
    {
        var state = LedgerEngine.Replay(events);
        var filled = events
            .Select(e => e.Kind == LedgerEventKind.Dispose
                ? e with { StoredCostIrr = state.Disposals[e.Doc].CostIrr, StoredProfitIrr = state.Disposals[e.Doc].ProfitIrr }
                : e)
            .ToList();
        var active = new HashSet<DocRef>(events.Where(e => e.DocId > 0).Select(e => e.Doc));
        var pools = state.Pools.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new BranchLedger(1, 5, filled, active, state.IrrBalance, pools);
    }

    private static TradeInput Input(decimal amount, decimal rate, decimal fee = 0m) =>
        new(1, "USD", amount, rate, null, null, null, fee);

    private static IReadOnlyDictionary<string, AccountInfo> Accounts() => new Dictionary<string, AccountInfo>
    {
        [AccountCodes.IrrCash] = new(AccountCodes.IrrCash, "صندوق ریال", "Asset", 3, "10", true, true, false, false),
        [AccountCodes.ForeignCash("USD")] = new(AccountCodes.ForeignCash("USD"), "موجودی ارز", "Asset", 4, "1101", true, true, false, false),
        [AccountCodes.OpeningCapital] = new(AccountCodes.OpeningCapital, "سرمایه", "Equity", 3, "30", true, true, false, false),
        ["6001"] = new("6001", "هزینه‌های اداری", "Expense", 3, "60", false, true, false, false),
        ["6999"] = new("6999", "حساب غیرفعال", "Expense", 3, "69", false, false, false, false),
    };

    [Fact]
    public void Backdated_buy_recalculates_later_sale_and_posts_an_adjustment()
    {
        // موجودی افتتاحیه، خرید ۱۰۰ دلاری در روز دوم و فروش ۵۰ دلاری در روز سوم.
        var ledger = LedgerOf(
            Cash(LedgerDocKind.Opening, 9, Day1, 1, 400_000_000m),
            Acquire(1, Day2, 2, 100m, 100_000_000m, -100_000_000m),
            Sell(2, Day3, 3, 50m, 60_000_000m));

        // خرید جدید با تاریخ روز اول (گذشته) و نرخ ۱٫۵ میلیون: بهای فروش روز سوم بالا می‌رود.
        var plan = LedgerPlanner.PlanTrade(ledger, Usd, Input(100m, 1_500_000m), TradeType.Buy, 1,
            new DateTime(2026, 10, 1, 12, 0, 0), Today);

        Assert.Equal("TRADE_CREATE", plan.Action);
        Assert.Equal(5, plan.ExpectedVersion);
        Assert.Equal(150_000_000m, plan.Trade!.CostIrr);
        var update = Assert.Single(plan.CostUpdates);
        Assert.Equal(2, update.TradeId);
        Assert.Equal(62_500_000m, update.CostIrr);
        Assert.Equal(-2_500_000m, update.ProfitIrr);

        var adjustment = Assert.Single(plan.Journals, j => j.SourceType == SourceTypes.Adjust);
        Assert.Equal(12_500_000m, adjustment.Lines.Single(l => l.AccountCode == "1101-USD").Credit);
        Assert.Equal(10_000_000m, adjustment.Lines.Single(l => l.AccountCode == AccountCodes.FxProfit).Debit);
        Assert.Equal(2_500_000m, adjustment.Lines.Single(l => l.AccountCode == AccountCodes.FxLoss).Debit);
        Assert.Equal(187_500_000m, plan.Inventory.Single().NewCostIrr);
        Assert.Equal(50_000_000m, plan.Inventory.Single().ExpectedCostIrr);
        Assert.Equal(-150_000_000m, plan.CashMovements.Single(m => m.CurrencyCode == "IRR").Delta);
    }

    [Fact]
    public void Voiding_a_buy_is_rejected_when_a_later_sale_would_have_no_stock()
    {
        var ledger = LedgerOf(
            Cash(LedgerDocKind.Opening, 9, Day1, 1, 300_000_000m),
            Acquire(1, Day1, 2, 100m, 100_000_000m, -100_000_000m),
            Sell(2, Day2, 3, 60m, 90_000_000m));

        var ex = Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Trade, 1), "اشتباه", 1, Today));
        Assert.Contains("کافی نیست", ex.Message);
    }

    [Fact]
    public void Voiding_an_unused_earlier_buy_is_allowed_and_recalculates_the_sale()
    {
        var ledger = LedgerOf(
            Cash(LedgerDocKind.Opening, 9, Day1, 1, 500_000_000m),
            Acquire(1, Day1, 2, 100m, 100_000_000m, -100_000_000m),
            Acquire(2, Day2, 3, 100m, 200_000_000m, -200_000_000m),
            Sell(3, Day3, 4, 50m, 80_000_000m));

        var plan = LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Trade, 1), "خطا در مبلغ", 1, Today);

        Assert.Equal(new DocRef(LedgerDocKind.Trade, 1), plan.Void!.Doc);
        var update = Assert.Single(plan.CostUpdates);
        Assert.Equal(3, update.TradeId);
        Assert.Equal(100_000_000m, update.CostIrr);
        Assert.Equal(-20_000_000m, update.ProfitIrr);
        var adjustment = Assert.Single(plan.Journals, j => j.SourceType == SourceTypes.Adjust);
        Assert.Equal(25_000_000m, adjustment.Lines.Single(l => l.AccountCode == "1101-USD").Credit);
        Assert.Equal(100_000_000m, plan.Inventory.Single().NewCostIrr);
        Assert.Equal(100_000_000m, plan.CashMovements.Single(m => m.CurrencyCode == "IRR").Delta);
        Assert.Equal(-100m, plan.CashMovements.Single(m => m.CurrencyCode == "USD").Delta);
        Assert.All(plan.CashMovements, m => Assert.Equal("VOID", m.RefType));
    }

    [Fact]
    public void Voiding_an_opening_is_rejected_when_trades_depend_on_it()
    {
        var ledger = LedgerOf(
            Cash(LedgerDocKind.Opening, 9, Day1, 1, 400_000_000m),
            new LedgerEvent(LedgerDocKind.Opening, 7, LedgerEventKind.Acquire, "USD", Day1, 2, 50m, 50_000_000m, 0m, 0m, 0m, 0m),
            Sell(1, Day2, 3, 50m, 60_000_000m));

        var ex = Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Opening, 7), "اشتباه", 1, Today));
        Assert.Contains("کافی نیست", ex.Message);
    }

    [Fact]
    public void Replacing_a_trade_removes_the_old_version_and_recalculates_later_sales()
    {
        var ledger = LedgerOf(
            Cash(LedgerDocKind.Opening, 9, Day1, 1, 300_000_000m),
            Acquire(1, Day1.AddHours(1), 2, 100m, 100_000_000m, -100_000_000m),
            Sell(2, Day2, 3, 50m, 60_000_000m));

        var plan = LedgerPlanner.PlanTrade(ledger, Usd, Input(200m, 1_200_000m), TradeType.Buy, 1,
            Day1.AddHours(1), Today, new DocRef(LedgerDocKind.Trade, 1), "ویرایش معامله");

        Assert.Equal("TRADE_REPLACE", plan.Action);
        Assert.Equal(new DocRef(LedgerDocKind.Trade, 1), plan.Void!.Doc);
        Assert.Equal(1L, plan.Trade!.ReplacesId);
        Assert.Equal(240_000_000m, plan.Trade.CostIrr);
        var update = Assert.Single(plan.CostUpdates);
        Assert.Equal(2, update.TradeId);
        Assert.Equal(60_000_000m, update.CostIrr);
        Assert.Equal(0m, update.ProfitIrr);
        Assert.Equal(180_000_000m, plan.Inventory.Single().NewCostIrr);
        Assert.Equal(-140_000_000m, plan.CashMovements.Where(m => m.CurrencyCode == "IRR").Sum(m => m.Delta));
        Assert.Equal(100m, plan.CashMovements.Where(m => m.CurrencyCode == "USD").Sum(m => m.Delta));
    }

    [Fact]
    public void Manual_expense_is_checked_against_the_cash_balance()
    {
        var ledger = LedgerOf(Cash(LedgerDocKind.Opening, 9, Day1, 1, 100_000_000m));
        var lines = new[]
        {
            new JournalLineDraft("6001", 150_000_000m, 0m),
            new JournalLineDraft(AccountCodes.IrrCash, 0m, 150_000_000m),
        };

        var ex = Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanManual(ledger, Accounts(), "هزینه‌ی اجاره", lines, 1, Day2, Today));
        Assert.Contains("کافی نیست", ex.Message);
    }

    [Fact]
    public void Manual_expense_paid_in_cash_creates_a_cash_movement()
    {
        var ledger = LedgerOf(Cash(LedgerDocKind.Opening, 9, Day1, 1, 100_000_000m));
        var lines = new[]
        {
            new JournalLineDraft("6001", 30_000_000m, 0m),
            new JournalLineDraft(AccountCodes.IrrCash, 0m, 30_000_000m),
        };

        var plan = LedgerPlanner.PlanManual(ledger, Accounts(), " هزینه‌ی برق ", lines, 1, Day2, Today);

        Assert.Equal(SourceTypes.Manual, plan.Journals.Single().SourceType);
        Assert.Equal("هزینه‌ی برق", plan.Journals.Single().Description);
        Assert.Equal(-30_000_000m, plan.CashMovements.Single().Delta);
        Assert.Equal("MANUAL", plan.CashMovements.Single().RefType);
    }

    [Fact]
    public void Manual_document_cannot_touch_foreign_inventory_accounts()
    {
        var ledger = LedgerOf(Cash(LedgerDocKind.Opening, 9, Day1, 1, 100_000_000m));
        var lines = new[]
        {
            new JournalLineDraft("1101-USD", 1_000_000m, 0m),
            new JournalLineDraft(AccountCodes.OpeningCapital, 0m, 1_000_000m),
        };

        Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanManual(ledger, Accounts(), "تعدیل ارز", lines, 1, Day2, Today));
    }

    [Fact]
    public void Manual_document_rejects_inactive_accounts_and_unbalanced_lines()
    {
        var ledger = LedgerOf(Cash(LedgerDocKind.Opening, 9, Day1, 1, 100_000_000m));
        var inactive = new[]
        {
            new JournalLineDraft("6999", 1_000_000m, 0m),
            new JournalLineDraft(AccountCodes.OpeningCapital, 0m, 1_000_000m),
        };
        Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanManual(ledger, Accounts(), "هزینه", inactive, 1, Day2, Today));

        Assert.Throws<BusinessRuleException>(() => new JournalDraft("نامتوازن", Day2, new[]
        {
            new JournalLineDraft(AccountCodes.IrrCash, 100m, 0m),
            new JournalLineDraft(AccountCodes.OpeningCapital, 0m, 90m),
        }, SourceTypes.Manual, new DocRef(LedgerDocKind.Manual, 0)));
    }

    [Fact]
    public void Voiding_an_unknown_or_already_voided_document_is_rejected()
    {
        var ledger = LedgerOf(Cash(LedgerDocKind.Opening, 9, Day1, 1, 100_000_000m));

        var unknown = Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Trade, 999), "دلیل", 1, Today));
        Assert.Contains("یافت نشد", unknown.Message);
    }

    [Fact]
    public void Void_requires_a_reason()
    {
        var ledger = LedgerOf(
            Cash(LedgerDocKind.Opening, 9, Day1, 1, 400_000_000m),
            Acquire(1, Day2, 2, 10m, 10_000_000m, -10_000_000m));

        Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Trade, 1), "   ", 1, Today));
    }

    [Fact]
    public void Stored_state_that_does_not_match_the_history_is_reported_as_inconsistent()
    {
        var good = LedgerOf(
            Cash(LedgerDocKind.Opening, 9, Day1, 1, 400_000_000m),
            Acquire(1, Day2, 2, 10m, 10_000_000m, -10_000_000m));
        var broken = good with { StoredIrrBalance = good.StoredIrrBalance + 1m };

        Assert.Throws<InvalidOperationException>(() =>
            LedgerPlanner.PlanVoid(broken, new DocRef(LedgerDocKind.Trade, 1), "دلیل", 1, Today));
    }

    [Fact]
    public void Occurrence_date_is_limited_to_thirty_days_back_and_never_in_the_future()
    {
        var now = new DateTime(2026, 10, 9, 14, 30, 45, 500);

        Assert.Equal(new DateTime(2026, 10, 9, 14, 30, 45), OccurrenceRules.Resolve(null, now));
        Assert.Equal(new DateTime(2026, 9, 9, 14, 30, 45), OccurrenceRules.Resolve(new DateTime(2026, 9, 9), now));
        Assert.Throws<BusinessRuleException>(() => OccurrenceRules.Resolve(new DateTime(2026, 9, 8), now));
        Assert.Throws<BusinessRuleException>(() => OccurrenceRules.Resolve(new DateTime(2026, 10, 10), now));
    }
}
