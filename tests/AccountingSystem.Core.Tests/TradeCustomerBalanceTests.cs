using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public sealed class TradeCustomerBalanceTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 10, 0, 0);
    private static readonly CurrencyInfo Usd = new("USD", "دلار", 2, true);

    [Fact]
    public void Customer_account_trade_uses_selected_balance_currency_not_traded_quantity()
    {
        var input = new TradeInput(
            BranchId: 1,
            CurrencyCode: "USD",
            Amount: 5m,
            Rate: 1_000_000m,
            CustomerName: null,
            NationalCode: null,
            Note: null,
            CustomerId: 27,
            SettlementMode: TradeSettlementMode.CustomerAccount,
            SettlementCurrencyCode: "EUR",
            ValuationIrr: 5_000_000m,
            CustomerBalanceCurrencyCode: "EUR",
            CustomerBalanceRateIrr: 500_000m,
            CustomerBalanceDecimalPlaces: 2);

        var posting = LedgerPlanner.PlanTrade(
            BranchLedger.Empty(1, 0m), Usd, input, TradeType.Buy, 9, Now, Now);

        var payable = Assert.Single(Assert.Single(posting.Journals).Lines,
            line => line.AccountCode == AccountCodes.CustomerPayable && line.Credit > 0m);
        Assert.Equal(5_000_000m, payable.Credit);
        Assert.Equal("EUR", payable.CustomerBalanceCurrencyCode);
        Assert.Equal(-10m, payable.CustomerBalanceDelta);
        Assert.Equal("EUR", posting.Trade!.SettlementCurrencyCode);
    }

    [Fact]
    public void Direct_trade_settlement_clears_customer_balance_in_actual_settlement_currency()
    {
        var openingAt = Now.AddHours(-1);
        var opening = new LedgerEvent(
            LedgerDocKind.Opening, 5, LedgerEventKind.Acquire, "EUR", openingAt, 10,
            20m, 10_000_000m, 0m, 0m, 0m, 0m);
        var ledger = new BranchLedger(
            1,
            0,
            new[] { opening },
            new HashSet<DocRef> { new(LedgerDocKind.Opening, 5) },
            0m,
            new Dictionary<string, PoolBalance> { ["EUR"] = new(20m, 10_000_000m) });
        var input = new TradeInput(
            BranchId: 1,
            CurrencyCode: "USD",
            Amount: 5m,
            Rate: 1_000_000m,
            CustomerName: null,
            NationalCode: null,
            Note: null,
            CustomerId: 27,
            SettlementMode: TradeSettlementMode.Direct,
            RateMode: TradeRateMode.Derived,
            SettlementCurrencyCode: "EUR",
            CrossRate: 2m,
            SettlementLines: new[] { new TradeSettlementInput("EUR", 10m, 500_000m, 2) },
            ValuationIrr: 5_000_000m,
            CustomerBalanceCurrencyCode: "EUR",
            CustomerBalanceRateIrr: 500_000m,
            CustomerBalanceDecimalPlaces: 2);

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Buy, 9, Now, Now);

        var customerLines = Assert.Single(posting.Journals).Lines
            .Where(line => line.CustomerId == 27)
            .ToList();
        Assert.Equal(2, customerLines.Count);
        Assert.All(customerLines, line => Assert.Equal("EUR", line.CustomerBalanceCurrencyCode));
        Assert.Equal(0m, customerLines.Sum(line => line.CustomerBalanceDelta));
        Assert.DoesNotContain(customerLines, line => line.CustomerBalanceCurrencyCode == "USD");
        Assert.Contains(posting.CashMovements, movement => movement.CurrencyCode == "EUR" && movement.Delta == -10m);
        Assert.DoesNotContain(posting.CashMovements, movement => movement.CurrencyCode == CurrencyCodes.Irr);
    }
}
