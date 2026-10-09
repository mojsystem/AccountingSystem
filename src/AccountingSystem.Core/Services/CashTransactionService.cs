using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// رسید دریافت و پرداخت مستقل از خریدوفروش ارز. هر سند صندوق را جابه‌جا و مانده‌ی دریافتنی/پرداختنی همان مشتری را تسویه می‌کند.
/// ویرایش با سند معکوس و نسخه‌ی جایگزین و ابطال با سند معکوس انجام می‌شود.
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
        var (normalized, currency, rate) = await PrepareAsync(input, occurredAt, null, ct);
        var posting = LedgerPlanner.PlanCashTransaction(ledger, currency, normalized, rate, occurredAt, actor.Id, now);
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
        var scopedInput = input with { BranchId = old.BranchId };
        var (normalized, currency, rate) = await PrepareAsync(scopedInput, occurredAt, transactionId, ct);
        var posting = LedgerPlanner.PlanCashTransaction(
            ledger,
            currency,
            normalized,
            rate,
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

    private async Task<(CashTransactionInput Input, CurrencyInfo Currency, decimal RateIrr)> PrepareAsync(
        CashTransactionInput input,
        DateTime occurredAt,
        long? excludeCashTransactionId,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(input.Direction))
        {
            throw new BusinessRuleException("نوع دریافت یا پرداخت نامعتبر است.");
        }
        if (input.CustomerId <= 0 || await _repository.GetCustomerAsync(input.CustomerId, ct) is null)
        {
            throw new BusinessRuleException("مشتری را انتخاب کنید.");
        }

        var code = (input.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currency = (await _repository.GetCurrenciesAsync(ct))
            .FirstOrDefault(c => c.Code == code && c.IsActive)
            ?? throw new BusinessRuleException("ارز انتخابی فعال نیست یا پیدا نشد.");
        TradePlanner.ValidateQuantity(input.Amount, currency);

        decimal rate;
        if (currency.Code == CurrencyCodes.Irr)
        {
            if (input.RateMode != TradeRateMode.Derived)
            {
                throw new BusinessRuleException("نرخ ریال همیشه یک است.");
            }
            rate = 1m;
        }
        else if (input.RateMode == TradeRateMode.Direct)
        {
            rate = input.RateIrr ?? throw new BusinessRuleException("نرخ توافقی را وارد کنید.");
            TradePlanner.ValidateRate(rate);
        }
        else if (input.RateMode == TradeRateMode.Derived)
        {
            var rateInfo = (await _repository.GetLatestRatesAsync(input.BranchId, ct))
                .FirstOrDefault(r => r.CurrencyCode == currency.Code)
                ?? throw new BusinessRuleException($"برای {currency.Code} در این شعبه نرخ روز ثبت نشده است.");
            // صندوق در رسید ارز را از مشتری می‌خرد؛ در پرداخت ارز را به مشتری می‌فروشد.
            rate = input.Direction == CashTransactionDirection.Receipt
                ? rateInfo.BuyRateIrr
                : rateInfo.SellRateIrr;
        }
        else
        {
            throw new BusinessRuleException("روش نرخ دریافت/پرداخت نامعتبر است.");
        }

        var irrAmount = currency.Code == CurrencyCodes.Irr
            ? MoneyMath.RoundIrr(input.Amount)
            : MoneyMath.RoundIrr(input.Amount * rate);
        if (irrAmount <= 0m)
        {
            throw new BusinessRuleException("ارزش ریالی دریافت/پرداخت صفر است.");
        }

        var balance = await _repository.GetCustomerAccountBalanceAsync(
            input.BranchId,
            input.CustomerId,
            occurredAt,
            excludeCashTransactionId: excludeCashTransactionId,
            ct: ct);
        var available = input.Direction == CashTransactionDirection.Receipt
            ? balance.ReceivableIrr
            : balance.PayableIrr;
        if (irrAmount > available)
        {
            var side = input.Direction == CashTransactionDirection.Receipt ? "دریافتنی" : "پرداختنی";
            throw new BusinessRuleException(
                $"مبلغ معادل {MoneyMath.FormatAmount(irrAmount, 0)} ریال از مانده‌ی {side} مشتری " +
                $"({MoneyMath.FormatAmount(Math.Max(0m, available), 0)} ریال) بیشتر است.");
        }

        return (input with { CurrencyCode = code }, currency, rate);
    }
}
