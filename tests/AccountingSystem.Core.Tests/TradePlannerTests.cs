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
        Assert.Equal(posting.Journal.Lines.Sum(l => l.Debit), posting.Journal.Lines.Sum(l => l.Credit));
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
        Assert.Equal(8_000_000m, posting.Journal.Lines.Single(l => l.AccountCode == AccountCodes.FxProfit).Credit);
        Assert.Equal(48_000_000m, posting.Journal.Lines.Sum(l => l.Debit));
        Assert.Equal(48_000_000m, posting.Journal.Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Sell_with_loss_debits_fx_loss_account()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 100m, 100_000_000m);
        var input = new TradeInput(1, "USD", 10m, 900_000m, null, null, null);

        var posting = TradePlanner.PlanSell(input, snapshot, 1, Now);

        Assert.Equal(-1_000_000m, posting.Trade!.ProfitIrr);
        Assert.Equal(1_000_000m, posting.Journal.Lines.Single(l => l.AccountCode == AccountCodes.FxLoss).Debit);
        Assert.Equal(posting.Journal.Lines.Sum(l => l.Debit), posting.Journal.Lines.Sum(l => l.Credit));
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
        Assert.Equal(100_000m, posting.Journal.Lines.Single(l => l.AccountCode == AccountCodes.FeeIncome).Credit);
        Assert.Equal(posting.Journal.Lines.Sum(l => l.Debit), posting.Journal.Lines.Sum(l => l.Credit));
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
        Assert.Equal(50_000m, posting.Journal.Lines.Single(l => l.AccountCode == AccountCodes.FeeIncome).Credit);
        Assert.Equal(48_050_000m, posting.Journal.Lines.Sum(l => l.Debit));
        Assert.Equal(48_050_000m, posting.Journal.Lines.Sum(l => l.Credit));
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
    public void Voiding_the_latest_buy_restores_the_previous_state_exactly()
    {
        var before = new TradeSnapshot(1, Usd, 10_000_000m, 0m, 0m);
        var buy = TradePlanner.PlanBuy(new TradeInput(1, "USD", 10m, 1_000_000m, null, null, null, FeeIrr: 100_000m), before, 1, Now);
        var trade = TradeInfoFor(7, TradeType.Buy, amount: 10m, irr: 10_000_000m, cost: 10_000_000m, profit: 0m, fee: 100_000m);
        var afterBuy = new TradeSnapshot(1, Usd, 100_000m, 10m, 10_000_000m);

        var posting = TradePlanner.PlanVoid(new VoidContext(trade, afterBuy, IsLatestInPool: true), "اشتباه در مبلغ", 1, Now);

        Assert.Equal(SourceTypes.Void, posting.SourceType);
        Assert.Equal(7, posting.Void!.TradeId);
        Assert.Equal(9_900_000m, posting.CashMovements.Single(m => m.CurrencyCode == "IRR").Delta);
        Assert.Equal(-10m, posting.CashMovements.Single(m => m.CurrencyCode == "USD").Delta);
        Assert.Equal(0m, posting.Inventory.Single().NewCostIrr);
        AssertOriginalAndReversalCancel(buy.Journal.Lines, posting.Journal.Lines);
    }

    [Fact]
    public void Voiding_a_sell_returns_stock_cash_and_fee_and_cancels_the_journal()
    {
        var before = new TradeSnapshot(1, Usd, 0m, 100m, 100_000_000m);
        var sell = TradePlanner.PlanSell(new TradeInput(1, "USD", 40m, 1_200_000m, null, null, null, FeeIrr: 50_000m), before, 1, Now);
        var trade = TradeInfoFor(9, TradeType.Sell, amount: 40m, irr: 48_000_000m, cost: 40_000_000m, profit: 8_000_000m, fee: 50_000m);
        var afterSell = new TradeSnapshot(1, Usd, 48_050_000m, 60m, 60_000_000m);

        var posting = TradePlanner.PlanVoid(new VoidContext(trade, afterSell, IsLatestInPool: true), "مشتری انصراف داد", 2, Now);

        Assert.Equal(-48_050_000m, posting.CashMovements.Single(m => m.CurrencyCode == "IRR").Delta);
        Assert.Equal(40m, posting.CashMovements.Single(m => m.CurrencyCode == "USD").Delta);
        Assert.Equal(100_000_000m, posting.Inventory.Single().NewCostIrr);
        AssertOriginalAndReversalCancel(sell.Journal.Lines, posting.Journal.Lines);
    }

    [Fact]
    public void Void_is_rejected_when_the_trade_is_not_the_latest_movement()
    {
        var trade = TradeInfoFor(3, TradeType.Buy, amount: 10m, irr: 10_000_000m, cost: 10_000_000m, profit: 0m, fee: 0m);
        var snapshot = new TradeSnapshot(1, Usd, 0m, 20m, 20_000_000m);

        var ex = Assert.Throws<BusinessRuleException>(() =>
            TradePlanner.PlanVoid(new VoidContext(trade, snapshot, IsLatestInPool: false), "دلیل", 1, Now));
        Assert.Contains("آخرین", ex.Message);
    }

    [Fact]
    public void Void_is_rejected_for_an_already_voided_trade()
    {
        var trade = TradeInfoFor(3, TradeType.Buy, amount: 10m, irr: 10_000_000m, cost: 10_000_000m, profit: 0m, fee: 0m) with
        {
            IsVoided = true,
            VoidedAt = Now,
            VoidedBy = "admin",
            VoidReason = "قبلی",
        };
        var snapshot = new TradeSnapshot(1, Usd, 0m, 10m, 10_000_000m);

        var ex = Assert.Throws<BusinessRuleException>(() =>
            TradePlanner.PlanVoid(new VoidContext(trade, snapshot, IsLatestInPool: true), "دلیل", 1, Now));
        Assert.Contains("قبلاً باطل", ex.Message);
    }

    [Fact]
    public void Void_requires_a_reason()
    {
        var trade = TradeInfoFor(3, TradeType.Buy, amount: 10m, irr: 10_000_000m, cost: 10_000_000m, profit: 0m, fee: 0m);
        var snapshot = new TradeSnapshot(1, Usd, 0m, 10m, 10_000_000m);

        Assert.Throws<BusinessRuleException>(() =>
            TradePlanner.PlanVoid(new VoidContext(trade, snapshot, IsLatestInPool: true), "   ", 1, Now));
    }

    [Fact]
    public void Voiding_a_sell_is_rejected_when_irr_cash_was_already_spent()
    {
        var trade = TradeInfoFor(9, TradeType.Sell, amount: 40m, irr: 48_000_000m, cost: 40_000_000m, profit: 8_000_000m, fee: 0m);
        var snapshot = new TradeSnapshot(1, Usd, 1_000_000m, 60m, 60_000_000m);

        Assert.Throws<BusinessRuleException>(() =>
            TradePlanner.PlanVoid(new VoidContext(trade, snapshot, IsLatestInPool: true), "دلیل", 1, Now));
    }

    [Fact]
    public void Journal_draft_rejects_unbalanced_lines()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new JournalDraft("نامتوازن", Now, new[]
            {
                new JournalLineDraft(AccountCodes.IrrCash, 100m, 0m),
                new JournalLineDraft(AccountCodes.OpeningCapital, 0m, 90m),
            });
        });
    }

    [Fact]
    public void Opening_foreign_balance_credits_opening_capital()
    {
        var snapshot = new TradeSnapshot(1, Usd, 0m, 0m, 0m);

        var posting = TradePlanner.PlanOpeningForeign(Usd, 100m, 1_000_000m, snapshot, 1, Now);

        Assert.Equal(SourceTypes.Opening, posting.SourceType);
        Assert.Null(posting.Trade);
        Assert.Equal(100_000_000m, posting.Journal.Lines.Single(l => l.AccountCode == AccountCodes.OpeningCapital).Credit);
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

    private static TradeInfo TradeInfoFor(long id, TradeType type, decimal amount, decimal irr, decimal cost, decimal profit, decimal fee) =>
        new(id, 1, "MAIN", "شعبه‌ی مرکزی", type, "USD", amount, irr / amount, irr, cost, profit, fee,
            null, null, null, Now, "cashier", false, null, null, null);

    /// <summary>سند اصلی و سند ابطال باید روی هر حساب جمعاً صفر شوند.</summary>
    private static void AssertOriginalAndReversalCancel(IEnumerable<JournalLineDraft> original, IEnumerable<JournalLineDraft> reversal)
    {
        var net = new Dictionary<string, decimal>();
        foreach (var line in original.Concat(reversal))
        {
            net[line.AccountCode] = net.GetValueOrDefault(line.AccountCode) + line.Debit - line.Credit;
        }
        Assert.All(net, pair => Assert.Equal(0m, pair.Value));
    }
}
