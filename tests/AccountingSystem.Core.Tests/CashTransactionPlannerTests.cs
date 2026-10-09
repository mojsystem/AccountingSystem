using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public sealed class CashTransactionPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 10, 0, 0);
    private static readonly CurrencyInfo Irr = new(CurrencyCodes.Irr, "ریال ایران", 0, true);
    private static readonly CurrencyInfo Usd = new("USD", "دلار آمریکا", 2, true);

    [Fact]
    public void Independent_irr_receipt_debits_cash_and_credits_customer_receivable()
    {
        var input = new CashTransactionInput(
            1, CashTransactionDirection.Receipt, 27, CurrencyCodes.Irr, 2_500_000m,
            TradeRateMode.Derived, Note: "رسید مستقل");

        var posting = LedgerPlanner.PlanCashTransaction(
            BranchLedger.Empty(1, 0m), Irr, input, 1m, Now, 9, Now);

        Assert.NotNull(posting.CashTransaction);
        Assert.Equal(2_500_000m, posting.CashTransaction!.IrrAmount);
        Assert.Equal(1m, posting.CashTransaction.RateIrr);
        Assert.Equal(2_500_000m, Assert.Single(posting.CashMovements).Delta);
        Assert.Equal(SourceTypes.CashReceipt, Assert.Single(posting.Journals).SourceType);
        var lines = Assert.Single(posting.Journals).Lines;
        Assert.Contains(lines, line => line.AccountCode == AccountCodes.IrrCash && line.Debit == 2_500_000m);
        Assert.Contains(lines, line => line.AccountCode == AccountCodes.CustomerReceivable
            && line.CustomerId == 27 && line.Credit == 2_500_000m);
        Assert.Equal(lines.Sum(line => line.Debit), lines.Sum(line => line.Credit));
    }

    [Fact]
    public void Independent_foreign_payment_uses_weighted_cost_and_sell_rate_value()
    {
        var opening = new LedgerEvent(
            LedgerDocKind.Opening, 5, LedgerEventKind.Acquire, "USD", Now.AddHours(-1), 10,
            10m, 10_000_000m, 0m, 0m, 0m, 0m);
        var ledger = new BranchLedger(
            1,
            0,
            new[] { opening },
            new HashSet<DocRef> { new(LedgerDocKind.Opening, 5) },
            0m,
            new Dictionary<string, PoolBalance> { ["USD"] = new(10m, 10_000_000m) });
        var input = new CashTransactionInput(
            1, CashTransactionDirection.Payment, 27, "USD", 2m,
            TradeRateMode.Direct, 1_100_000m);

        var posting = LedgerPlanner.PlanCashTransaction(ledger, Usd, input, 1_100_000m, Now, 9, Now);

        var payment = Assert.IsType<CashTransactionDraft>(posting.CashTransaction);
        Assert.Equal(2_200_000m, payment.IrrAmount);
        Assert.Equal(2_000_000m, payment.CostIrr);
        Assert.Equal(200_000m, payment.ProfitIrr);
        Assert.Equal(-2m, Assert.Single(posting.CashMovements).Delta);
        Assert.Equal(SourceTypes.CashPayment, Assert.Single(posting.Journals).SourceType);
        var lines = Assert.Single(posting.Journals).Lines;
        Assert.Contains(lines, line => line.AccountCode == AccountCodes.CustomerPayable
            && line.CustomerId == 27 && line.Debit == 2_200_000m);
        Assert.Contains(lines, line => line.AccountCode == AccountCodes.ForeignCash("USD") && line.Credit == 2_000_000m);
        Assert.Contains(lines, line => line.AccountCode == AccountCodes.FxProfit && line.Credit == 200_000m);
        Assert.Equal(lines.Sum(line => line.Debit), lines.Sum(line => line.Credit));
    }

    [Fact]
    public void Void_timestamps_are_truncated_to_database_second_precision()
    {
        var requestedAt = Now.AddTicks(TimeSpan.TicksPerSecond * 3 / 4);
        var target = new DocRef(LedgerDocKind.CashTransaction, 7);
        var eventAt = Now.AddHours(-1);
        var ledger = new BranchLedger(
            1,
            0,
            new[]
            {
                new LedgerEvent(LedgerDocKind.CashTransaction, 7, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                    eventAt, 20, 0m, 3_000_000m, 0m, 3_000_000m, 0m, 0m),
            },
            new HashSet<DocRef> { target },
            3_000_000m,
            new Dictionary<string, PoolBalance>());

        var posting = LedgerPlanner.PlanVoid(ledger, target, "تست", 9, requestedAt);

        Assert.Equal(OccurrenceRules.Truncate(requestedAt), posting.Now);
        Assert.Equal(OccurrenceRules.Truncate(requestedAt), posting.Void!.OccurredAt);
        Assert.Equal(-3_000_000m, Assert.Single(posting.CashMovements).Delta);
    }

    [Fact]
    public void Backdated_foreign_receipt_reprices_later_customer_payment_and_posts_cash_adjustment()
    {
        var opening = new LedgerEvent(
            LedgerDocKind.Opening, 5, LedgerEventKind.Acquire, "USD", Now.AddHours(-3), 10,
            10m, 10_000_000m, 0m, 0m, 0m, 0m);
        var oldPayment = new LedgerEvent(
            LedgerDocKind.CashTransaction, 7, LedgerEventKind.Dispose, "USD", Now.AddHours(-1), 20,
            5m, 5_500_000m, 0m, 0m, 5_000_000m, 500_000m);
        var ledger = new BranchLedger(
            1,
            3,
            new[] { opening, oldPayment },
            new HashSet<DocRef>
            {
                new(LedgerDocKind.Opening, 5),
                new(LedgerDocKind.CashTransaction, 7),
            },
            0m,
            new Dictionary<string, PoolBalance> { ["USD"] = new(5m, 5_000_000m) });
        var input = new CashTransactionInput(
            1, CashTransactionDirection.Receipt, 27, "USD", 10m,
            TradeRateMode.Direct, 2_000_000m);

        var posting = LedgerPlanner.PlanCashTransaction(
            ledger, Usd, input, 2_000_000m, Now.AddHours(-2), 9, Now);

        var update = Assert.Single(posting.CashTransactionCostUpdates!);
        Assert.Equal(7L, update.CashTransactionId);
        Assert.Equal(7_500_000m, update.CostIrr);
        Assert.Equal(-2_000_000m, update.ProfitIrr);
        Assert.Equal(22_500_000m, Assert.Single(posting.Inventory).NewCostIrr);
        var adjustment = Assert.Single(posting.Journals, journal => journal.SourceType == SourceTypes.CashAdjustment);
        Assert.Equal(new DocRef(LedgerDocKind.CashTransaction, 7), adjustment.Source);
        Assert.Equal(adjustment.Lines.Sum(line => line.Debit), adjustment.Lines.Sum(line => line.Credit));
    }
}
