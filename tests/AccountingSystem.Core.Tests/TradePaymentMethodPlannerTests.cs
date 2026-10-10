using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public sealed class TradePaymentMethodPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 10, 0, 0);
    private static readonly DateTime OpeningAt = Now.AddDays(-1);
    private static readonly CurrencyInfo Usd = new("USD", "دلار آمریکا", 2, true);

    [Fact]
    public void Cash_payment_uses_the_selected_currency_cash_box()
    {
        var ledger = Ledger(irrCash: 5_000_000m);
        var input = TradeInputFor(
            TradePaymentMethod.Cash,
            TradeSettlementMode.Direct,
            new[] { new TradeSettlementInput(CurrencyCodes.Irr, 2_000_000m, DecimalPlaces: 0) });

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Buy, 9, Now, Now);

        Assert.Equal(TradePaymentMethod.Cash, posting.Trade!.PaymentMethod);
        Assert.Contains(posting.CashMovements, movement => movement.CurrencyCode == CurrencyCodes.Irr && movement.Delta == -2_000_000m);
        Assert.Contains(posting.Journals.Single().Lines, line => line.AccountCode == AccountCodes.IrrCash && line.Credit == 2_000_000m);
        Assert.Empty(posting.BankAccountUpdates!);
    }

    [Fact]
    public void Credit_trade_posts_to_customer_balance_without_a_settlement_or_consideration_cash_movement()
    {
        var input = TradeInputFor(
            TradePaymentMethod.Credit,
            TradeSettlementMode.CustomerAccount,
            Array.Empty<TradeSettlementInput>());

        var posting = LedgerPlanner.PlanTrade(Ledger(), Usd, input, TradeType.Buy, 9, Now, Now);

        Assert.Equal(TradePaymentMethod.Credit, posting.Trade!.PaymentMethod);
        Assert.Empty(posting.Trade.Settlements!);
        Assert.DoesNotContain(posting.CashMovements, movement => movement.CurrencyCode == CurrencyCodes.Irr);
        Assert.Contains(posting.Journals.Single().Lines, line => line.AccountCode == AccountCodes.CustomerPayable
            && line.CustomerId == 42 && line.CustomerBalanceCurrencyCode == CurrencyCodes.Irr
            && line.CustomerBalanceDelta == -2_000_000m && line.Credit == 2_000_000m);
    }

    [Theory]
    [InlineData(TradePaymentMethod.Cheque, AccountCodes.ChequePayable)]
    [InlineData(TradePaymentMethod.Pos, AccountCodes.PosPayable)]
    public void Cheque_and_pos_payments_use_their_separate_intermediary_accounts(
        TradePaymentMethod method,
        string intermediaryAccount)
    {
        var input = TradeInputFor(
            method,
            TradeSettlementMode.Direct,
            new[] { new TradeSettlementInput(CurrencyCodes.Irr, 2_000_000m, DecimalPlaces: 0) });

        var posting = LedgerPlanner.PlanTrade(Ledger(irrCash: 5_000_000m), Usd, input, TradeType.Buy, 9, Now, Now);
        var lines = posting.Journals.Single().Lines;

        Assert.Equal(method, posting.Trade!.PaymentMethod);
        Assert.Contains(lines, line => line.AccountCode == intermediaryAccount && line.Credit == 2_000_000m);
        Assert.DoesNotContain(lines, line => line.AccountCode == AccountCodes.IrrCash);
        Assert.DoesNotContain(posting.CashMovements, movement => movement.CurrencyCode == CurrencyCodes.Irr);
        Assert.Equal(lines.Sum(line => line.Debit), lines.Sum(line => line.Credit));
    }

    [Theory]
    [InlineData(TradePaymentMethod.Cheque, AccountCodes.ChequeReceivable)]
    [InlineData(TradePaymentMethod.Pos, AccountCodes.PosReceivable)]
    public void Cheque_and_pos_receipts_use_their_separate_intermediary_accounts(
        TradePaymentMethod method,
        string intermediaryAccount)
    {
        var input = TradeInputFor(
            method,
            TradeSettlementMode.Direct,
            new[] { new TradeSettlementInput(CurrencyCodes.Irr, 2_000_000m, DecimalPlaces: 0) });

        var posting = LedgerPlanner.PlanTrade(
            Ledger(usdCash: 5m, usdCostIrr: 5_000_000m), Usd, input, TradeType.Sell, 9, Now, Now);
        var lines = posting.Journals.Single().Lines;

        Assert.Equal(method, posting.Trade!.PaymentMethod);
        Assert.Contains(lines, line => line.AccountCode == intermediaryAccount && line.Debit == 2_000_000m);
        Assert.DoesNotContain(lines, line => line.AccountCode == AccountCodes.IrrCash);
        Assert.DoesNotContain(posting.CashMovements, movement => movement.CurrencyCode == CurrencyCodes.Irr);
        Assert.Equal(lines.Sum(line => line.Debit), lines.Sum(line => line.Credit));
    }

    [Fact]
    public void Bank_transfer_uses_the_named_bank_balance_and_does_not_change_cash_boxes()
    {
        const int bankAccountId = 71;
        var ledger = Ledger(
            irrCash: 5_000_000m,
            banks: new[] { new BankOpening(bankAccountId, CurrencyCodes.Irr, 3_000_000m, 3_000_000m) });
        var input = TradeInputFor(
            TradePaymentMethod.BankTransfer,
            TradeSettlementMode.Direct,
            new[] { new TradeSettlementInput(CurrencyCodes.Irr, 2_000_000m, DecimalPlaces: 0, BankAccountId: bankAccountId) });

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Buy, 9, Now, Now);
        var update = Assert.Single(posting.BankAccountUpdates!);

        Assert.Equal(TradePaymentMethod.BankTransfer, posting.Trade!.PaymentMethod);
        Assert.Equal(bankAccountId, Assert.Single(posting.Trade.Settlements!).BankAccountId);
        Assert.Equal(3_000_000m, update.ExpectedBalance);
        Assert.Equal(1_000_000m, update.NewBalance);
        Assert.Equal(3_000_000m, update.ExpectedCostIrr);
        Assert.Equal(1_000_000m, update.NewCostIrr);
        Assert.DoesNotContain(posting.CashMovements, movement => movement.CurrencyCode == CurrencyCodes.Irr);
        Assert.Contains(posting.Journals.Single().Lines, line => line.AccountCode == AccountCodes.BankCash && line.Credit == 2_000_000m);
        Assert.DoesNotContain(posting.Journals.Single().Lines, line => line.AccountCode == AccountCodes.IrrCash);
    }

    [Fact]
    public void Split_bank_transfer_applies_the_same_method_to_each_line_and_updates_each_selected_account()
    {
        const int eurBankId = 81;
        const int gbpBankId = 82;
        var ledger = Ledger(
            banks: new[]
            {
                new BankOpening(eurBankId, "EUR", 5m, 5_000_000m),
                new BankOpening(gbpBankId, "GBP", 7m, 3_500_000m),
            });
        var input = TradeInputFor(
            TradePaymentMethod.BankTransfer,
            TradeSettlementMode.Split,
            new[]
            {
                new TradeSettlementInput("EUR", 1m, 1_000_000m, 2, eurBankId),
                new TradeSettlementInput("GBP", 2m, 500_000m, 2, gbpBankId),
            },
            amount: 2m,
            valuationIrr: 2_000_000m);

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Buy, 9, Now, Now);

        Assert.Equal(TradePaymentMethod.BankTransfer, posting.Trade!.PaymentMethod);
        Assert.Equal(2, posting.Trade.Settlements!.Count);
        Assert.All(posting.Trade.Settlements, settlement => Assert.Equal(TradeSettlementDirection.Payment, settlement.Direction));
        var bankUpdates = posting.BankAccountUpdates!;
        Assert.Equal(4m, bankUpdates.Single(update => update.BankAccountId == eurBankId).NewBalance);
        Assert.Equal(5m, bankUpdates.Single(update => update.BankAccountId == gbpBankId).NewBalance);
        Assert.DoesNotContain(posting.CashMovements, movement => movement.CurrencyCode is "EUR" or "GBP" or CurrencyCodes.Irr);
        Assert.Contains(posting.Journals.Single().Lines, line => line.AccountCode == AccountCodes.ForeignBank("EUR") && line.Credit == 1_000_000m);
        Assert.Contains(posting.Journals.Single().Lines, line => line.AccountCode == AccountCodes.ForeignBank("GBP") && line.Credit == 1_000_000m);
    }

    [Fact]
    public void Bank_transfer_receipt_increases_only_the_selected_bank_account()
    {
        const int bankAccountId = 91;
        var ledger = Ledger(
            usdCash: 5m,
            usdCostIrr: 5_000_000m,
            banks: new[] { new BankOpening(bankAccountId, CurrencyCodes.Irr, 4_000_000m, 4_000_000m) });
        var input = TradeInputFor(
            TradePaymentMethod.BankTransfer,
            TradeSettlementMode.Direct,
            new[] { new TradeSettlementInput(CurrencyCodes.Irr, 2_000_000m, DecimalPlaces: 0, BankAccountId: bankAccountId) });

        var posting = LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Sell, 9, Now, Now);

        Assert.Equal(6_000_000m, Assert.Single(posting.BankAccountUpdates!).NewBalance);
        Assert.DoesNotContain(posting.CashMovements, movement => movement.CurrencyCode == CurrencyCodes.Irr);
        Assert.Contains(posting.CashMovements, movement => movement.CurrencyCode == "USD" && movement.Delta == -2m);
        Assert.Contains(posting.Journals.Single().Lines, line => line.AccountCode == AccountCodes.BankCash && line.Debit == 2_000_000m);
    }

    [Fact]
    public void Outgoing_cash_payment_is_rejected_when_the_selected_currency_cash_box_is_short()
    {
        var input = TradeInputFor(
            TradePaymentMethod.Cash,
            TradeSettlementMode.Direct,
            new[] { new TradeSettlementInput(CurrencyCodes.Irr, 2_000_000m, DecimalPlaces: 0) });

        var error = Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanTrade(Ledger(irrCash: 1_000_000m), Usd, input, TradeType.Buy, 9, Now, Now));

        Assert.Contains("کافی نیست", error.Message);
        Assert.Contains("صندوق ریال", error.Message);
    }

    [Fact]
    public void Outgoing_bank_transfer_is_rejected_when_the_selected_bank_account_is_short()
    {
        const int bankAccountId = 101;
        var ledger = Ledger(banks: new[] { new BankOpening(bankAccountId, CurrencyCodes.Irr, 1_000_000m, 1_000_000m) });
        var input = TradeInputFor(
            TradePaymentMethod.BankTransfer,
            TradeSettlementMode.Direct,
            new[] { new TradeSettlementInput(CurrencyCodes.Irr, 2_000_000m, DecimalPlaces: 0, BankAccountId: bankAccountId) });

        var error = Assert.Throws<BusinessRuleException>(() =>
            LedgerPlanner.PlanTrade(ledger, Usd, input, TradeType.Buy, 9, Now, Now));

        Assert.Contains("کافی نیست", error.Message);
        Assert.Contains(bankAccountId.ToString(), error.Message);
    }

    private static TradeInput TradeInputFor(
        TradePaymentMethod method,
        TradeSettlementMode mode,
        IReadOnlyList<TradeSettlementInput> settlementLines,
        decimal amount = 2m,
        decimal valuationIrr = 2_000_000m) =>
        new(
            BranchId: 1,
            CurrencyCode: "USD",
            Amount: amount,
            Rate: 1_000_000m,
            CustomerName: null,
            NationalCode: null,
            Note: null,
            CustomerId: 42,
            SettlementMode: mode,
            RateMode: TradeRateMode.Derived,
            SettlementCurrencyCode: CurrencyCodes.Irr,
            CrossRate: 1m,
            SettlementLines: settlementLines,
            ValuationIrr: valuationIrr,
            CustomerBalanceCurrencyCode: CurrencyCodes.Irr,
            CustomerBalanceRateIrr: 1m,
            CustomerBalanceDecimalPlaces: 0,
            PaymentMethod: method);

    private static BranchLedger Ledger(
        decimal irrCash = 0m,
        decimal usdCash = 0m,
        decimal usdCostIrr = 0m,
        IReadOnlyList<BankOpening>? banks = null)
    {
        var events = new List<LedgerEvent>
        {
            new(LedgerDocKind.Opening, -1, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                OpeningAt, long.MinValue, 0m, 0m, 0m, irrCash, 0m, 0m),
        };
        if (usdCash > 0m || usdCostIrr > 0m)
        {
            events.Add(new LedgerEvent(LedgerDocKind.Opening, -2, LedgerEventKind.Acquire, "USD",
                OpeningAt, long.MinValue + 1, usdCash, usdCostIrr, 0m, 0m, usdCostIrr, 0m));
        }

        var bankPools = new Dictionary<int, PoolBalance>();
        foreach (var bank in banks ?? Array.Empty<BankOpening>())
        {
            bankPools.Add(bank.Id, new PoolBalance(bank.Balance, bank.CostIrr));
            events.Add(new LedgerEvent(LedgerDocKind.Opening, -bank.Id, LedgerEventKind.Acquire, bank.CurrencyCode,
                OpeningAt, long.MinValue + bank.Id, bank.Balance, bank.CostIrr, 0m, 0m, bank.CostIrr, 0m,
                BankAccountId: bank.Id));
        }

        var storedCashPools = new Dictionary<string, PoolBalance>(StringComparer.Ordinal)
        {
            ["USD"] = new(usdCash, usdCostIrr),
            ["EUR"] = new(0m, 0m),
            ["GBP"] = new(0m, 0m),
        };
        return new BranchLedger(1, 0, events, new HashSet<DocRef>(), irrCash, storedCashPools, bankPools);
    }

    private sealed record BankOpening(int Id, string CurrencyCode, decimal Balance, decimal CostIrr);
}
