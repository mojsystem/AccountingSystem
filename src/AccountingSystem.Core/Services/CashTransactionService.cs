using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// دریافت و پرداخت مستقل از معامله. صندوق بر اساس ارز واقعی جابه‌جا می‌شود و حساب مشتری مانده‌ی امضاشده‌ی
/// جداگانه برای هر ارز دارد؛ دریافت مانده را کاهش و پرداخت آن را افزایش می‌دهد.
/// </summary>
public sealed class CashTransactionService
{
    public const string ReplacementReason = "ویرایش دریافت/پرداخت؛ نسخه‌ی اصلاحی جایگزین شد";

    private readonly IAccountingRepository _repository;
    private readonly PermissionService _permissions;

    public CashTransactionService(IAccountingRepository repository)
    {
        _repository = repository;
        _permissions = new PermissionService(repository);
    }

    public async Task<IReadOnlyList<CashTransactionInfo>> GetTransactionsAsync(
        CurrentUser actor,
        int? branchId,
        DateTime fromInclusive,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        return await _repository.GetCashTransactionsAsync(scope, fromInclusive, toExclusive, ct);
    }

    public async Task<CashTransactionInfo?> GetAsync(CurrentUser actor, long transactionId, CancellationToken ct = default)
    {
        var transaction = await _repository.GetCashTransactionAsync(transactionId, ct);
        if (transaction is not null)
        {
            await _permissions.RequireReadAsync(actor, transaction.BranchId, ct);
        }
        return transaction;
    }

    public async Task<CustomerAccountBalance> GetCustomerBalanceAsync(
        CurrentUser actor,
        int branchId,
        int customerId,
        DateTime asOf,
        CancellationToken ct = default)
    {
        await _permissions.RequireReadAsync(actor, branchId, ct);
        return await _repository.GetCustomerAccountBalanceAsync(branchId, customerId, asOf, ct: ct);
    }

    public async Task<IReadOnlyList<CustomerCurrencyBalance>> GetCustomerCurrencyBalancesAsync(
        CurrentUser actor,
        int branchId,
        int customerId,
        DateTime asOf,
        CancellationToken ct = default)
    {
        await _permissions.RequireReadAsync(actor, branchId, ct);
        return await _repository.GetCustomerCurrencyBalancesAsync(branchId, customerId, asOf, ct: ct);
    }

    public async Task<long> RecordAsync(
        CurrentUser actor,
        CashTransactionInput input,
        DateTime now,
        DateTime? occurredOn = null,
        CancellationToken ct = default)
    {
        if (input.BranchId <= 0)
        {
            throw new BusinessRuleException("شعبه را انتخاب کنید.");
        }
        await _permissions.RequireAsync(actor, Permission.CashTransactionCreate, input.BranchId, ct);
        var occurredAt = OccurrenceRules.Resolve(occurredOn, now);
        var ledger = await _repository.GetBranchLedgerAsync(input.BranchId, ct);
        var prepared = await PrepareAsync(input, occurredAt, null, ct);
        var posting = LedgerPlanner.PlanCashTransaction(
            ledger,
            prepared.CashCurrency,
            prepared.BalanceCurrency,
            prepared.Input,
            prepared.AccountingRateIrr,
            prepared.BalanceAmount,
            prepared.BalanceBefore,
            occurredAt,
            actor.Id,
            now);
        var id = await _repository.PostAsync(posting, ct);
        return id ?? throw new InvalidOperationException("شناسه‌ی دریافت/پرداخت ثبت نشد.");
    }

    public async Task<long?> EditAsync(
        CurrentUser actor,
        long transactionId,
        CashTransactionInput input,
        DateTime? occurredOn,
        DateTime now,
        CancellationToken ct = default)
    {
        var old = await _repository.GetCashTransactionAsync(transactionId, ct)
            ?? throw new BusinessRuleException("دریافت/پرداخت انتخاب‌شده پیدا نشد.");
        await _permissions.RequireAsync(actor, Permission.CashTransactionEdit, old.BranchId, ct);
        if (old.IsVoided)
        {
            throw new BusinessRuleException("دریافت/پرداخت باطل‌شده قابل ویرایش نیست.");
        }

        var sameDay = occurredOn is null || occurredOn.Value.Date == old.OccurredAt.Date;
        var occurredAt = sameDay ? old.OccurredAt : OccurrenceRules.Resolve(occurredOn, now);
        var ledger = await _repository.GetBranchLedgerAsync(old.BranchId, ct);
        var scopedInput = input with
        {
            BranchId = old.BranchId,
            BalanceCurrencyCode = string.IsNullOrWhiteSpace(input.BalanceCurrencyCode)
                ? old.BalanceCurrencyCode
                : input.BalanceCurrencyCode,
        };
        var prepared = await PrepareAsync(scopedInput, occurredAt, transactionId, ct);
        var posting = LedgerPlanner.PlanCashTransaction(
            ledger,
            prepared.CashCurrency,
            prepared.BalanceCurrency,
            prepared.Input,
            prepared.AccountingRateIrr,
            prepared.BalanceAmount,
            prepared.BalanceBefore,
            occurredAt,
            actor.Id,
            now,
            new DocRef(LedgerDocKind.CashTransaction, transactionId),
            ReplacementReason);
        return await _repository.PostAsync(posting, ct);
    }

    public async Task VoidAsync(CurrentUser actor, long transactionId, string reason, DateTime now, CancellationToken ct = default)
    {
        var transaction = await _repository.GetCashTransactionAsync(transactionId, ct)
            ?? throw new BusinessRuleException("دریافت/پرداخت انتخاب‌شده پیدا نشد.");
        await _permissions.RequireAsync(actor, Permission.CashTransactionVoid, transaction.BranchId, ct);
        if (transaction.IsVoided)
        {
            throw new BusinessRuleException("این دریافت/پرداخت قبلاً باطل شده است.");
        }

        var ledger = await _repository.GetBranchLedgerAsync(transaction.BranchId, ct);
        var posting = LedgerPlanner.PlanVoid(
            ledger,
            new DocRef(LedgerDocKind.CashTransaction, transactionId),
            reason,
            actor.Id,
            now);
        await _repository.PostAsync(posting, ct);
    }

    private async Task<PreparedCashTransaction> PrepareAsync(
        CashTransactionInput input,
        DateTime occurredAt,
        long? excludeCashTransactionId,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(input.Direction))
        {
            throw new BusinessRuleException("نوع دریافت یا پرداخت نامعتبر است.");
        }
        if (!Enum.IsDefined(input.RateMode))
        {
            throw new BusinessRuleException("نرخ اطلاع‌رسانی نامعتبر است.");
        }
        if (input.CustomerId <= 0 || await _repository.GetCustomerAsync(input.CustomerId, ct) is null)
        {
            throw new BusinessRuleException("مشتری را انتخاب کنید.");
        }

        var cashCode = (input.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var balanceCode = (input.BalanceCurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (balanceCode.Length == 0)
        {
            balanceCode = cashCode;
        }

        var currencies = await _repository.GetCurrenciesAsync(ct);
        var cashCurrency = currencies.FirstOrDefault(c => c.Code == cashCode && c.IsActive)
            ?? throw new BusinessRuleException("ارز صندوق انتخابی فعال نیست یا پیدا نشد.");
        var balanceCurrency = currencies.FirstOrDefault(c => c.Code == balanceCode && c.IsActive)
            ?? throw new BusinessRuleException("ارز حساب مشتری انتخابی فعال نیست یا پیدا نشد.");
        TradePlanner.ValidateQuantity(input.Amount, cashCurrency);
        if (input.RateIrr is { } informativeRate)
        {
            TradePlanner.ValidateRate(informativeRate);
        }

        // نرخ ورودی اختیاری و صرفاً برای نگهداری/نمایش است؛ تسویه‌ی ارزی از نرخ رسمی شعبه در زمان وقوع استفاده می‌کند.
        var rates = await _repository.GetRatesAtAsync(input.BranchId, occurredAt, ct);
        var side = input.Direction == CashTransactionDirection.Receipt
            ? "نرخ خرید"
            : "نرخ فروش";
        decimal RateFor(string code)
        {
            if (code == CurrencyCodes.Irr)
            {
                return 1m;
            }
            var quote = rates.FirstOrDefault(r => r.CurrencyCode == code)
                ?? throw new BusinessRuleException($"برای {code} در تاریخ سند {side} شعبه ثبت نشده است.");
            return input.Direction == CashTransactionDirection.Receipt ? quote.BuyRateIrr : quote.SellRateIrr;
        }

        var accountingRateIrr = RateFor(cashCode);
        var irrAmount = cashCode == CurrencyCodes.Irr
            ? MoneyMath.RoundIrr(input.Amount)
            : MoneyMath.RoundIrr(input.Amount * accountingRateIrr);
        if (irrAmount <= 0m)
        {
            throw new BusinessRuleException("ارزش دفتری دریافت/پرداخت صفر است.");
        }

        var balanceAmount = cashCode == balanceCode
            ? input.Amount
            : MoneyMath.RoundTo(irrAmount / RateFor(balanceCode), balanceCurrency.DecimalPlaces);
        if (balanceAmount <= 0m)
        {
            throw new BusinessRuleException("مبلغ معادل در ارز حساب مشتری صفر است.");
        }

        var balances = await _repository.GetCustomerCurrencyBalancesAsync(
            input.BranchId,
            input.CustomerId,
            occurredAt,
            excludeCashTransactionId: excludeCashTransactionId,
            ct: ct);
        var balanceBefore = balances.FirstOrDefault(b => b.CurrencyCode == balanceCode)?.BalanceAmount ?? 0m;
        var availableBalance = input.Direction == CashTransactionDirection.Receipt
            ? Math.Max(0m, balanceBefore)
            : Math.Max(0m, -balanceBefore);
        if (balanceAmount > availableBalance)
        {
            var sideName = input.Direction == CashTransactionDirection.Receipt ? "دریافتنی" : "پرداختنی";
            throw new BusinessRuleException(
                $"مبلغ تسویه‌شده ({MoneyMath.FormatAmount(balanceAmount, balanceCurrency.DecimalPlaces)} {balanceCode}) از مانده‌ی {sideName} مشتری بیشتر است.");
        }

        var informationalRateIrr = cashCode == CurrencyCodes.Irr
            ? 1m
            : input.RateIrr ?? accountingRateIrr;
        var normalized = input with
        {
            CurrencyCode = cashCode,
            BalanceCurrencyCode = balanceCode,
            RateMode = input.RateIrr is null ? TradeRateMode.Derived : TradeRateMode.Direct,
            RateIrr = informationalRateIrr,
        };
        return new PreparedCashTransaction(
            normalized,
            cashCurrency,
            balanceCurrency,
            accountingRateIrr,
            irrAmount,
            balanceAmount,
            balanceBefore);
    }

    private sealed record PreparedCashTransaction(
        CashTransactionInput Input,
        CurrencyInfo CashCurrency,
        CurrencyInfo BalanceCurrency,
        decimal AccountingRateIrr,
        decimal IrrAmount,
        decimal BalanceAmount,
        decimal BalanceBefore);
}
