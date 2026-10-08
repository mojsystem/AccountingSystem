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
        var snapshot = new TradeSnapshot(Usd, 10_000_000m, 0m, 0m);
        var input = new TradeInput("USD", 10m, 1_000_000m, "علی", null, null);

        var posting = TradePlanner.PlanBuy(input, snapshot, 1, Now);

        Assert.NotNull(posting.Trade);
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
        var snapshot = new TradeSnapshot(Usd, 9_999_999m, 0m, 0m);
        var input = new TradeInput("USD", 10m, 1_000_000m, null, null, null);

        var ex = Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanBuy(input, snapshot, 1, Now));
        Assert.Contains("کافی نیست", ex.Message);
    }

    [Fact]
    public void Sell_with_profit_uses_weighted_average_cost()
    {
        var snapshot = new TradeSnapshot(Usd, 0m, 100m, 100_000_000m);
        var input = new TradeInput("USD", 40m, 1_200_000m, null, null, null);

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
        var snapshot = new TradeSnapshot(Usd, 0m, 100m, 100_000_000m);
        var input = new TradeInput("USD", 10m, 900_000m, null, null, null);

        var posting = TradePlanner.PlanSell(input, snapshot, 1, Now);

        Assert.Equal(-1_000_000m, posting.Trade!.ProfitIrr);
        Assert.Equal(1_000_000m, posting.Journal.Lines.Single(l => l.AccountCode == AccountCodes.FxLoss).Debit);
        Assert.Equal(posting.Journal.Lines.Sum(l => l.Debit), posting.Journal.Lines.Sum(l => l.Credit));
    }

    [Fact]
    public void Selling_entire_stock_removes_exact_cost_basis()
    {
        var snapshot = new TradeSnapshot(Usd, 0m, 3m, 100_000_001m);
        var input = new TradeInput("USD", 3m, 1_100_000m, null, null, null);

        var posting = TradePlanner.PlanSell(input, snapshot, 1, Now);

        Assert.Equal(100_000_001m, posting.Trade!.CostIrr);
        Assert.Equal(0m, posting.Inventory.Single().NewCostIrr);
    }

    [Fact]
    public void Sell_is_rejected_when_stock_is_insufficient()
    {
        var snapshot = new TradeSnapshot(Usd, 0m, 5m, 5_000_000m);
        var input = new TradeInput("USD", 6m, 1_000_000m, null, null, null);

        Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanSell(input, snapshot, 1, Now));
    }

    [Fact]
    public void Opening_foreign_balance_credits_opening_capital()
    {
        var snapshot = new TradeSnapshot(Usd, 0m, 0m, 0m);

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
        Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanOpeningIrr(1_000.5m, 0m, 1, Now));
    }

    [Fact]
    public void Quantity_with_more_decimals_than_currency_allows_is_rejected()
    {
        var snapshot = new TradeSnapshot(Usd, 10_000_000m, 0m, 0m);
        var input = new TradeInput("USD", 10.555m, 1_000_000m, null, null, null);

        Assert.Throws<BusinessRuleException>(() => TradePlanner.PlanBuy(input, snapshot, 1, Now));
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
}
