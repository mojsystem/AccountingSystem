using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// ثبت، ابطال و ویرایش معاملات ارزی. پشتیبانی از جفت‌ارز، تسویه‌ی چندبخشی و مانده‌ی مشتری.
/// هر تغییر کل تاریخچه‌ی شعبه را بازمحاسبه می‌کند.
/// </summary>
public sealed class CurrencyTradeService
{
    /// <summary>دلیل ثبت شده برای معامله‌ای که نسخه‌ی اصلاحی آن جایگزین شده است.</summary>
    public const string ReplacementReason = "ویرایش معامله؛ نسخه‌ی اصلاحی جایگزین شد";

    private readonly IAccountingRepository _repository;
    private readonly PermissionService _permissions;

    public CurrencyTradeService(IAccountingRepository repository)
    {
        _repository = repository;
        _permissions = new PermissionService(repository);
    }

    public Task<long> BuyFromCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default) =>
        RecordTradeAsync(input, TradeType.Buy, user, now, null, ct);

    public Task<long> SellToCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default) =>
        RecordTradeAsync(input, TradeType.Sell, user, now, null, ct);

    /// <summary>حساب‌های بانکی قابل مشاهده برای فرم حواله‌ی معامله.</summary>
    public async Task<IReadOnlyList<BankAccountInfo>> GetBankAccountsAsync(CurrentUser user, int? branchId, CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(user, branchId, ct);
        return await _repository.GetBankAccountsAsync(scope, ct);
    }

    /// <summary>مانده‌ی دریافتنی و پرداختنی مشتری در شعبه را برای نمایش و محاسبه‌ی تهاتر می‌خواند.</summary>
    public async Task<CustomerAccountBalance> GetCustomerAccountBalanceAsync(
        CurrentUser user,
        int branchId,
        int customerId,
        DateTime asOf,
        CancellationToken ct = default)
    {
        await _permissions.RequireReadAsync(user, branchId, ct);
        return await _repository.GetCustomerAccountBalanceAsync(branchId, customerId, asOf, ct: ct);
    }

    /// <summary>خواندن یک معامله برای نمایش و ویرایش؛ کاربر صندوق فقط معامله‌ی شعبه‌ی خودش را می‌بیند.</summary>
    public async Task<TradeInfo?> GetTradeAsync(CurrentUser user, long tradeId, CancellationToken ct = default)
    {
        var trade = await _repository.GetTradeAsync(tradeId, ct);
        if (trade is not null)
        {
            await _permissions.RequireReadAsync(user, trade.BranchId, ct);
        }
        return trade;
    }

    /// <summary>
    /// ثبت معامله. occurredOn خالی یعنی همین لحظه؛ وگرنه معامله با آن تاریخ (تا ۳۰ روز قبل) ثبت می‌شود.
    /// </summary>
    public async Task<long> RecordTradeAsync(TradeInput input, TradeType type, CurrentUser user, DateTime now, DateTime? occurredOn = null, CancellationToken ct = default)
    {
        if (input.BranchId <= 0)
        {
            throw new BusinessRuleException("شعبه را انتخاب کنید.");
        }
        await _permissions.RequireAsync(user, Permission.TradeRecord, input.BranchId, ct);
        var customer = await RequireCustomerAsync(input.CustomerId, ct);
        var currency = await LoadCurrencyAsync(input.CurrencyCode, ct);
        var occurredAt = OccurrenceRules.Resolve(occurredOn, now);

        // ابتدا نسخه‌ی دفتر را می‌خوانیم و سپس مانده‌ی مشتری را؛ هر ثبت هم‌زمان بعدی با نسخه رد می‌شود.
        var ledger = await _repository.GetBranchLedgerAsync(input.BranchId, ct);
        var prepared = await PrepareTradeInputAsync(input with
        {
            BranchId = input.BranchId,
            CustomerId = customer.Id,
            CustomerName = customer.FullName,
            NationalCode = customer.NationalCode,
        }, currency, type, occurredAt, null, ct);

        var posting = LedgerPlanner.PlanTrade(ledger, currency, prepared, type, user.Id, occurredAt, now);
        var tradeId = await _repository.PostAsync(posting, ct);
        return tradeId ?? throw new InvalidOperationException("شناسه‌ی معامله ایجاد نشد.");
    }

    /// <summary>
    /// ابطال هر معامله‌ای (مدیر، یا دارنده‌ی دسترسی «ابطال معامله» در همان شعبه). سند معکوس ثبت می‌شود و موجودی و بهای فروش‌های بعدی بازمحاسبه می‌شود.
    /// اگر این کار باعث موجودی منفی شود، ابطال رد می‌شود.
    /// </summary>
    public async Task VoidTradeAsync(CurrentUser user, long tradeId, string reason, DateTime now, CancellationToken ct = default)
    {
        var trade = await _repository.GetTradeAsync(tradeId, ct)
            ?? throw new BusinessRuleException("معامله‌ی انتخابی یافت نشد.");
        await _permissions.RequireAsync(user, Permission.TradeVoid, trade.BranchId, ct);
        var ledger = await _repository.GetBranchLedgerAsync(trade.BranchId, ct);
        var posting = LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Trade, tradeId), reason, user.Id, now);
        await _repository.PostAsync(posting, ct);
    }

    /// <summary>
    /// ویرایش معامله (مدیر، یا دارنده‌ی دسترسی «ویرایش معامله» در همان شعبه). تغییر مالی با ابطال نسخه‌ی قبلی و ثبت نسخه‌ی جایگزین انجام می‌شود.
    /// </summary>
    public async Task<long?> EditTradeAsync(CurrentUser user, long tradeId, TradeInput input, TradeType type, DateTime? occurredOn, DateTime now, CancellationToken ct = default)
    {
        var old = await _repository.GetTradeAsync(tradeId, ct)
            ?? throw new BusinessRuleException("معامله‌ی انتخابی یافت نشد.");
        await _permissions.RequireAsync(user, Permission.TradeEdit, old.BranchId, ct);
        if (old.IsVoided)
        {
            throw new BusinessRuleException("معامله‌ی باطل‌شده قابل ویرایش نیست.");
        }

        var customer = await RequireCustomerAsync(input.CustomerId, ct);
        var currency = await LoadCurrencyAsync(input.CurrencyCode, ct);
        var sameDay = occurredOn is null || occurredOn.Value.Date == old.OccurredAt.Date;
        var occurredAt = sameDay ? old.OccurredAt : OccurrenceRules.Resolve(occurredOn, now);
        var ledger = await _repository.GetBranchLedgerAsync(old.BranchId, ct);
        var scoped = input with
        {
            BranchId = old.BranchId,
            CustomerId = customer.Id,
            CustomerName = customer.FullName,
            NationalCode = customer.NationalCode,
        };
        if (SameFinancialInputs(scoped, old, type, currency.Code, occurredAt))
        {
            var (_, _, noteOnly) = TradePlanner.CleanDetails(customer.FullName, customer.NationalCode, input.Note);
            await _repository.UpdateTradeDetailsAsync(tradeId, old.BranchId, customer.Id, customer.FullName, customer.NationalCode, noteOnly, user.Id, now, ct);
            return null;
        }
        var prepared = await PrepareTradeInputAsync(scoped, currency, type, occurredAt, tradeId, ct);

        var effectiveSettlementMode = prepared.SettlementMode ?? TradeSettlementMode.Direct;
        var effectiveCrossRate = prepared.CrossRate ?? (effectiveSettlementMode == TradeSettlementMode.Direct ? prepared.Rate : 0m);
        var comparisonLines = prepared.SettlementLines;
        if (prepared.SettlementMode is null)
        {
            var legacyIrrDue = type == TradeType.Buy
                ? (prepared.ValuationIrr ?? MoneyMath.RoundIrr(prepared.Amount * prepared.Rate)) - prepared.FeeIrr
                : (prepared.ValuationIrr ?? MoneyMath.RoundIrr(prepared.Amount * prepared.Rate)) + prepared.FeeIrr;
            comparisonLines = legacyIrrDue > 0m
                ? new[] { new TradeSettlementInput(CurrencyCodes.Irr, legacyIrrDue, 1m) }
                : Array.Empty<TradeSettlementInput>();
        }

        var financialChanged = type != old.Type
            || currency.Code != old.CurrencyCode
            || prepared.Amount != old.Amount
            || prepared.Rate != old.Rate
            || prepared.FeeIrr != old.FeeIrr
            || customer.Id != old.CustomerId
            || effectiveSettlementMode != old.SettlementMode
            || prepared.PaymentMethod != old.PaymentMethod
            || prepared.RateMode != old.RateMode
            || effectiveCrossRate != old.CrossRate
            || !string.Equals(prepared.SettlementCurrencyCode, old.SettlementCurrencyCode, StringComparison.OrdinalIgnoreCase)
            || prepared.CustomerOffsetIrr != old.CustomerOffsetIrr
            || !SameSettlementLines(comparisonLines, old.Settlements, type)
            || !sameDay;

        var (_, _, note) = TradePlanner.CleanDetails(customer.FullName, customer.NationalCode, input.Note);
        if (!financialChanged)
        {
            await _repository.UpdateTradeDetailsAsync(tradeId, old.BranchId, customer.Id, customer.FullName, customer.NationalCode, note, user.Id, now, ct);
            return null;
        }

        var posting = LedgerPlanner.PlanTrade(ledger, currency, prepared, type, user.Id, occurredAt, now,
            new DocRef(LedgerDocKind.Trade, tradeId), ReplacementReason);
        return await _repository.PostAsync(posting, ct);
    }

    private async Task<TradeInput> PrepareTradeInputAsync(
        TradeInput input,
        CurrencyInfo currency,
        TradeType type,
        DateTime occurredAt,
        long? excludeTradeId,
        CancellationToken ct)
    {
        TradePlanner.ValidateQuantity(input.Amount, currency);

        var mode = input.SettlementMode;
        var paymentMethod = input.PaymentMethod;
        if (!Enum.IsDefined(paymentMethod))
        {
            throw new BusinessRuleException("روش دریافت/پرداخت معامله نامعتبر است.");
        }
        if (paymentMethod == TradePaymentMethod.Credit)
        {
            if (mode is not null and not TradeSettlementMode.CustomerAccount)
            {
                throw new BusinessRuleException("روش نسیه باید روی حساب مشتری ثبت شود.");
            }
            mode = TradeSettlementMode.CustomerAccount;
        }
        else if (mode == TradeSettlementMode.CustomerAccount)
        {
            // حفظ سازگاری فراخوانی‌های قبلی که فقط ACCOUNT را می‌فرستادند.
            if (paymentMethod != TradePaymentMethod.Cash)
            {
                throw new BusinessRuleException("روش حساب مشتری با روش نقدی/چک/کارتخوان/حواله همخوانی ندارد.");
            }
            paymentMethod = TradePaymentMethod.Credit;
        }
        input = input with { SettlementMode = mode, PaymentMethod = paymentMethod };
        if (paymentMethod != TradePaymentMethod.BankTransfer && input.BankAccountId is not null)
        {
            throw new BusinessRuleException("انتخاب حساب بانکی فقط برای روش حواله مجاز است.");
        }

        // فراخوانی‌های قدیمی همچنان همان تسویه‌ی ریالی را ایجاد می‌کنند.
        if (input.SettlementMode is null)
        {
            TradePlanner.ValidateRate(input.Rate);
            var legacyIrr = MoneyMath.RoundIrr(input.Amount * input.Rate);
            TradePlanner.ValidateFee(input.FeeIrr, type, legacyIrr);
            return input with
            {
                SettlementCurrencyCode = CurrencyCodes.Irr,
                CrossRate = input.Rate,
                ValuationIrr = legacyIrr,
                CustomerOffsetIrr = 0m,
                SettlementLines = null,
                CustomerBalanceCurrencyCode = CurrencyCodes.Irr,
                CustomerBalanceRateIrr = 1m,
                CustomerBalanceDecimalPlaces = 0,
            };
        }

        var effectiveMode = input.SettlementMode.Value;
        var currencies = (await _repository.GetCurrenciesAsync(ct)).Where(c => c.IsActive).ToDictionary(c => c.Code, StringComparer.Ordinal);
        var rates = await _repository.GetRatesAtAsync(input.BranchId, occurredAt, ct);
        var bankAccounts = paymentMethod == TradePaymentMethod.BankTransfer
            ? (await _repository.GetBankAccountsAsync(input.BranchId, ct)).ToDictionary(account => account.Id)
            : new Dictionary<int, BankAccountInfo>();
        var applyOffset = input.ApplyCustomerOffset;
        var offset = new CustomerOffsetResolution(0m, CurrencyCodes.Irr, 0m);
        var crossRate = 0m;
        decimal valuationIrr;
        decimal effectiveRate;
        IReadOnlyList<TradeSettlementInput> settlements;

        if (effectiveMode == TradeSettlementMode.Direct)
        {
            var counterCode = (input.SettlementCurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
            if (!currencies.TryGetValue(counterCode, out var counterCurrency))
            {
                throw new BusinessRuleException("ارز دریافت/پرداخت را انتخاب کنید.");
            }
            if (counterCode == currency.Code)
            {
                throw new BusinessRuleException("ارز دریافت/پرداخت باید با ارز مورد معامله متفاوت باشد.");
            }

            var counterRate = SettlementRate(counterCurrency, rates, input.BranchId, type);
            if (input.RateMode == TradeRateMode.Direct)
            {
                crossRate = input.CrossRate ?? 0m;
                TradePlanner.ValidateCrossRate(crossRate);
                valuationIrr = MoneyMath.RoundIrr(input.Amount * crossRate * counterRate);
            }
            else
            {
                var baseRate = BaseRate(currency, rates, input.BranchId, type);
                TradePlanner.ValidateRate(baseRate);
                crossRate = MoneyMath.RoundTo(baseRate / counterRate, 8);
                TradePlanner.ValidateCrossRate(crossRate);
                valuationIrr = MoneyMath.RoundIrr(input.Amount * baseRate);
            }

            if (valuationIrr <= 0m)
            {
                throw new BusinessRuleException("ارزش ریالی معامله صفر است؛ مقدار یا نرخ را بررسی کنید.");
            }
            TradePlanner.ValidateFee(input.FeeIrr, type, valuationIrr);
            offset = await ResolveOffsetAsync(input, type, valuationIrr, counterCurrency, counterRate,
                occurredAt, excludeTradeId, applyOffset, ct);
            var due = type == TradeType.Buy ? valuationIrr - input.FeeIrr : valuationIrr + input.FeeIrr;
            var amountInCounter = MoneyMath.RoundTo((due - offset.IrrAmount) / counterRate, counterCurrency.DecimalPlaces);
            if (amountInCounter > 0m)
            {
                TradePlanner.ValidateQuantity(amountInCounter, counterCurrency);
                int? bankAccountId = null;
                if (paymentMethod == TradePaymentMethod.BankTransfer)
                {
                    bankAccountId = input.BankAccountId;
                    ValidateBankAccount(bankAccounts, bankAccountId, counterCode, occurredAt);
                }
                settlements = new[] { new TradeSettlementInput(counterCode, amountInCounter, counterRate, counterCurrency.DecimalPlaces, bankAccountId) };
            }
            else
            {
                settlements = Array.Empty<TradeSettlementInput>();
            }
            effectiveRate = Math.Max(0.0001m, MoneyMath.RoundTo(valuationIrr / input.Amount, 4));
            return input with
            {
                Rate = effectiveRate,
                PaymentMethod = paymentMethod,
                SettlementMode = effectiveMode,
                SettlementCurrencyCode = counterCode,
                CrossRate = crossRate,
                ValuationIrr = valuationIrr,
                CustomerOffsetIrr = offset.IrrAmount,
                CustomerOffsetCurrencyCode = offset.CurrencyCode,
                CustomerOffsetAmount = offset.Amount,
                SettlementLines = settlements,
                CustomerBalanceCurrencyCode = counterCode,
                CustomerBalanceRateIrr = counterRate,
                CustomerBalanceDecimalPlaces = counterCurrency.DecimalPlaces,
            };
        }

        var accountCurrencyCode = (input.SettlementCurrencyCode ?? CurrencyCodes.Irr).Trim().ToUpperInvariant();
        if (!currencies.TryGetValue(accountCurrencyCode, out var accountCurrency))
        {
            throw new BusinessRuleException("ارز مانده‌ی حساب مشتری را انتخاب کنید.");
        }
        var accountBalanceRate = SettlementRate(accountCurrency, rates, input.BranchId, type);

        TradePlanner.ValidateRate(input.Rate);
        valuationIrr = MoneyMath.RoundIrr(input.Amount * input.Rate);
        if (valuationIrr <= 0m)
        {
            throw new BusinessRuleException("مبلغ ریالی معامله صفر است.");
        }
        TradePlanner.ValidateFee(input.FeeIrr, type, valuationIrr);
        offset = await ResolveOffsetAsync(input, type, valuationIrr, accountCurrency, accountBalanceRate,
            occurredAt, excludeTradeId, applyOffset, ct);
        effectiveRate = input.Rate;
        crossRate = 0m;

        if (effectiveMode == TradeSettlementMode.CustomerAccount)
        {
            if (input.SettlementLines is { Count: > 0 })
            {
                throw new BusinessRuleException("روش حساب مشتری بدون دریافت یا پرداخت نقدی است.");
            }
            settlements = Array.Empty<TradeSettlementInput>();
        }
        else if (effectiveMode == TradeSettlementMode.Split)
        {
            var requested = input.SettlementLines ?? Array.Empty<TradeSettlementInput>();
            if (requested.Count == 0)
            {
                throw new BusinessRuleException("حداقل یک سطر دریافت یا پرداخت برای تسویه‌ی چندبخشی وارد کنید.");
            }
            if (requested.Count > 100)
            {
                throw new BusinessRuleException("در یک معامله حداکثر ۱۰۰ سطر دریافت یا پرداخت می‌توان ثبت کرد.");
            }

            var normalized = new List<TradeSettlementInput>(requested.Count);
            foreach (var line in requested)
            {
                var settlementCode = (line.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
                if (!currencies.TryGetValue(settlementCode, out var settlementCurrency))
                {
                    throw new BusinessRuleException($"ارز سطر تسویه‌ی {settlementCode} فعال نیست یا پیدا نشد.");
                }
                if (settlementCode == currency.Code)
                {
                    throw new BusinessRuleException("ارز دریافتی/پرداختی باید با ارز مورد معامله متفاوت باشد.");
                }
                TradePlanner.ValidateQuantity(line.Amount, settlementCurrency);
                var rateIrr = SettlementRate(settlementCurrency, rates, input.BranchId, type);
                int? bankAccountId = null;
                if (paymentMethod == TradePaymentMethod.BankTransfer)
                {
                    bankAccountId = line.BankAccountId;
                    ValidateBankAccount(bankAccounts, bankAccountId, settlementCode, occurredAt);
                }
                else if (line.BankAccountId is not null)
                {
                    throw new BusinessRuleException("انتخاب حساب بانکی فقط برای روش حواله مجاز است.");
                }
                normalized.Add(new TradeSettlementInput(settlementCode, line.Amount, rateIrr, settlementCurrency.DecimalPlaces, bankAccountId));
            }
            settlements = normalized;
        }
        else
        {
            throw new BusinessRuleException("روش تسویه‌ی معامله نامعتبر است.");
        }

        return input with
        {
            Rate = effectiveRate,
            PaymentMethod = paymentMethod,
            SettlementMode = effectiveMode,
            SettlementCurrencyCode = accountCurrencyCode,
            CrossRate = crossRate,
            ValuationIrr = valuationIrr,
            CustomerOffsetIrr = offset.IrrAmount,
            CustomerOffsetCurrencyCode = offset.CurrencyCode,
            CustomerOffsetAmount = offset.Amount,
            SettlementLines = settlements,
            CustomerBalanceCurrencyCode = accountCurrencyCode,
            CustomerBalanceRateIrr = accountBalanceRate,
            CustomerBalanceDecimalPlaces = accountCurrency.DecimalPlaces,
        };
    }

    private static void ValidateBankAccount(
        IReadOnlyDictionary<int, BankAccountInfo> accounts,
        int? bankAccountId,
        string currencyCode,
        DateTime occurredAt)
    {
        if (bankAccountId is not { } id || !accounts.TryGetValue(id, out var account))
        {
            throw new BusinessRuleException("حساب بانکی حواله را انتخاب کنید.");
        }
        if (!string.Equals(account.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException($"حساب بانکی «{account.Name}» برای ارز {account.CurrencyCode} است، نه {currencyCode}.");
        }
        if (OccurrenceRules.Truncate(occurredAt) < account.CreatedAt)
        {
            throw new BusinessRuleException($"حساب بانکی «{account.Name}» در تاریخ معامله هنوز افتتاح نشده بود.");
        }
    }

    private async Task<CustomerOffsetResolution> ResolveOffsetAsync(
        TradeInput input,
        TradeType type,
        decimal valuationIrr,
        CurrencyInfo balanceCurrency,
        decimal balanceRateIrr,
        DateTime occurredAt,
        long? excludeTradeId,
        bool applyOffset,
        CancellationToken ct)
    {
        if (!applyOffset)
        {
            return new CustomerOffsetResolution(0m, balanceCurrency.Code, 0m);
        }
        if (input.CustomerId is not { } customerId)
        {
            throw new BusinessRuleException("برای تهاتر، مشتری را انتخاب کنید.");
        }
        var balances = await _repository.GetCustomerCurrencyBalancesAsync(
            input.BranchId, customerId, occurredAt, excludeTradeId: excludeTradeId, ct: ct);
        var current = balances.FirstOrDefault(b => b.CurrencyCode == balanceCurrency.Code)?.BalanceAmount ?? 0m;
        var available = type == TradeType.Buy ? Math.Max(0m, current) : Math.Max(0m, -current);
        var due = type == TradeType.Buy ? valuationIrr - input.FeeIrr : valuationIrr + input.FeeIrr;
        var amountInIrr = MoneyMath.RoundIrr(Math.Min(due, available * balanceRateIrr));
        var offsetAmount = balanceCurrency.Code == CurrencyCodes.Irr
            ? amountInIrr
            : Math.Min(available, MoneyMath.RoundTo(amountInIrr / balanceRateIrr, balanceCurrency.DecimalPlaces));
        return new CustomerOffsetResolution(amountInIrr, balanceCurrency.Code, offsetAmount);
    }

    private sealed record CustomerOffsetResolution(decimal IrrAmount, string CurrencyCode, decimal Amount);

    private static decimal BaseRate(CurrencyInfo currency, IReadOnlyList<RateInfo> rates, int branchId, TradeType type)
    {
        var rate = rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == currency.Code)
            ?? throw new BusinessRuleException($"برای {currency.Code} نرخ روز شعبه ثبت نشده است.");
        return type == TradeType.Buy ? rate.BuyRateIrr : rate.SellRateIrr;
    }

    private static decimal SettlementRate(CurrencyInfo currency, IReadOnlyList<RateInfo> rates, int branchId, TradeType type)
    {
        if (currency.Code == CurrencyCodes.Irr)
        {
            return 1m;
        }
        var rate = rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == currency.Code)
            ?? throw new BusinessRuleException($"برای ارز دریافت/پرداخت {currency.Code} نرخ روز شعبه ثبت نشده است.");
        // در خرید صرافی ارز مقابل را می‌فروشد؛ در فروش، ارز مقابل را از مشتری می‌خرد.
        return type == TradeType.Buy ? rate.SellRateIrr : rate.BuyRateIrr;
    }

    /// <summary>هر معامله باید به یک مشتری ثبت‌شده وصل باشد.</summary>
    private async Task<CustomerInfo> RequireCustomerAsync(int? customerId, CancellationToken ct)
    {
        if (customerId is null or <= 0)
        {
            throw new BusinessRuleException("مشتری را انتخاب کنید؛ هر معامله باید به یک مشتری ثبت‌شده وصل باشد.");
        }
        return await _repository.GetCustomerAsync(customerId.Value, ct)
            ?? throw new BusinessRuleException("مشتری انتخاب‌شده پیدا نشد.");
    }

    private async Task<CurrencyInfo> LoadCurrencyAsync(string currencyCode, CancellationToken ct)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currencies = await _repository.GetCurrenciesAsync(ct);
        return currencies.FirstOrDefault(c => c.Code == code)
            ?? throw new BusinessRuleException("ارز انتخابی یافت نشد.");
    }

    private static bool SameFinancialInputs(TradeInput input, TradeInfo old, TradeType type, string currencyCode, DateTime occurredAt)
    {
        if (type != old.Type
            || currencyCode != old.CurrencyCode
            || input.Amount != old.Amount
            || input.Rate != old.Rate
            || input.FeeIrr != old.FeeIrr
            || input.CustomerId != old.CustomerId
            || occurredAt.Date != old.OccurredAt.Date)
        {
            return false;
        }

        var mode = input.SettlementMode ?? TradeSettlementMode.Direct;
        var paymentMethod = mode == TradeSettlementMode.CustomerAccount
            ? TradePaymentMethod.Credit
            : input.PaymentMethod;
        if (mode != old.SettlementMode || paymentMethod != old.PaymentMethod || input.RateMode != old.RateMode)
        {
            return false;
        }
        if (mode == TradeSettlementMode.Direct)
        {
            var code = (input.SettlementCurrencyCode ?? (old.SettlementCurrencyCode == CurrencyCodes.Irr ? CurrencyCodes.Irr : string.Empty))
                .Trim().ToUpperInvariant();
            if (!string.Equals(code, old.SettlementCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (input.RateMode == TradeRateMode.Direct && input.CrossRate != old.CrossRate)
            {
                return false;
            }
            if (paymentMethod == TradePaymentMethod.BankTransfer
                && input.BankAccountId != (old.Settlements?.FirstOrDefault()?.BankAccountId))
            {
                return false;
            }
        }
        else
        {
            var code = (input.SettlementCurrencyCode ?? CurrencyCodes.Irr).Trim().ToUpperInvariant();
            var oldCode = (old.SettlementCurrencyCode ?? CurrencyCodes.Irr).Trim().ToUpperInvariant();
            if (!string.Equals(code, oldCode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        if (mode == TradeSettlementMode.Split
            && !SameSettlementInputs(input.SettlementLines, old.Settlements, type))
        {
            return false;
        }

        if (input.ApplyCustomerOffset != (old.CustomerOffsetIrr > 0m))
        {
            return false;
        }
        return input.CustomerOffsetIrr == 0m || input.CustomerOffsetIrr == old.CustomerOffsetIrr;
    }

    private static bool SameSettlementInputs(
        IReadOnlyList<TradeSettlementInput>? requested,
        IReadOnlyList<TradeSettlementInfo>? existing,
        TradeType type)
    {
        var lines = requested ?? Array.Empty<TradeSettlementInput>();
        var saved = existing ?? Array.Empty<TradeSettlementInfo>();
        if (lines.Count != saved.Count)
        {
            return false;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var stored = saved[i];
            if (!string.Equals(line.CurrencyCode, stored.CurrencyCode, StringComparison.OrdinalIgnoreCase)
                || line.Amount != stored.Amount
                || line.BankAccountId != stored.BankAccountId
                || (type == TradeType.Buy ? TradeSettlementDirection.Payment : TradeSettlementDirection.Receipt) != stored.Direction)
            {
                return false;
            }
        }
        return true;
    }

    private static bool SameSettlementLines(
        IReadOnlyList<TradeSettlementInput>? requested,
        IReadOnlyList<TradeSettlementInfo>? existing,
        TradeType type)
    {
        var lines = requested ?? Array.Empty<TradeSettlementInput>();
        var saved = existing ?? Array.Empty<TradeSettlementInfo>();
        if (lines.Count != saved.Count)
        {
            return false;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var stored = saved[i];
            if (!string.Equals(line.CurrencyCode, stored.CurrencyCode, StringComparison.OrdinalIgnoreCase)
                || line.Amount != stored.Amount
                || line.RateIrr != stored.RateIrr
                || line.BankAccountId != stored.BankAccountId
                || (type == TradeType.Buy ? TradeSettlementDirection.Payment : TradeSettlementDirection.Receipt) != stored.Direction)
            {
                return false;
            }
        }
        return true;
    }
}
