using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>مدیریت ارزها، نرخ‌ها، صندوق‌ها و موجودی‌های افتتاحیه‌ی هر شعبه.</summary>
public sealed class CurrencyAdminService
{
    /// <summary>دلیل ثبت شده برای موجودی افتتاحیه‌ای که نسخه‌ی اصلاحی آن جایگزین شده است.</summary>
    public const string OpeningReplacementReason = "ویرایش موجودی افتتاحیه؛ نسخه‌ی اصلاحی جایگزین شد";

    private readonly IAccountingRepository _repository;

    public CurrencyAdminService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(CancellationToken ct = default) =>
        _repository.GetCurrenciesAsync(ct);

    public Task<IReadOnlyList<RateInfo>> GetLatestRatesAsync(CurrentUser actor, int? branchId, CancellationToken ct = default) =>
        _repository.GetLatestRatesAsync(BranchScope.ResolveForReport(actor, branchId), ct);

    public Task<IReadOnlyList<CashBoxInfo>> GetCashBoxesAsync(CurrentUser actor, int? branchId, CancellationToken ct = default) =>
        _repository.GetCashBoxesAsync(BranchScope.ResolveForReport(actor, branchId), ct);

    public Task<IReadOnlyList<OpeningInfo>> GetOpeningsAsync(CurrentUser actor, int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default) =>
        _repository.GetOpeningsAsync(BranchScope.ResolveForReport(actor, branchId), fromInclusive, toExclusive, ct);

    public async Task AddCurrencyAsync(CurrentUser actor, string code, string name, int decimalPlaces, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var normalizedCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedCode.Length != 3 || !normalizedCode.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new BusinessRuleException("کد ارز باید دقیقاً سه حرف انگلیسی باشد (مثلاً USD).");
        }
        if (normalizedCode == CurrencyCodes.Irr)
        {
            throw new BusinessRuleException("ریال ارز پایه است و از قبل ثبت شده است.");
        }
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            throw new BusinessRuleException("نام ارز را وارد کنید.");
        }
        if (decimalPlaces < 0 || decimalPlaces > 4)
        {
            throw new BusinessRuleException("تعداد ارقام اعشار ارز باید بین ۰ و ۴ باشد.");
        }

        await _repository.AddCurrencyAsync(new CurrencyInfo(normalizedCode, cleanName, decimalPlaces, true), actor.Id, now, ct);
    }

    public async Task SetRateAsync(CurrentUser actor, int branchId, string currencyCode, decimal buyRateIrr, decimal sellRateIrr, DateTime now, CancellationToken ct = default)
    {
        var scopedBranch = BranchScope.RequireBranch(actor, branchId);
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code == CurrencyCodes.Irr)
        {
            throw new BusinessRuleException("برای ریال نرخ تعریف نمی‌شود.");
        }
        var branches = await _repository.GetBranchesAsync(ct);
        if (branches.All(b => b.Id != scopedBranch))
        {
            throw new BusinessRuleException("شعبه‌ی انتخابی یافت نشد.");
        }
        var currencies = await _repository.GetCurrenciesAsync(ct);
        var currency = currencies.FirstOrDefault(c => c.Code == code)
            ?? throw new BusinessRuleException("ارز انتخابی یافت نشد.");
        if (!currency.IsActive)
        {
            throw new BusinessRuleException("این ارز غیرفعال است.");
        }
        TradePlanner.ValidateRate(buyRateIrr);
        TradePlanner.ValidateRate(sellRateIrr);
        if (sellRateIrr < buyRateIrr)
        {
            throw new BusinessRuleException("نرخ فروش نمی‌تواند کمتر از نرخ خرید باشد.");
        }

        await _repository.AddRateAsync(scopedBranch, code, buyRateIrr, sellRateIrr, actor.Id, now, ct);
    }

    /// <summary>
    /// ثبت موجودی افتتاحیه (فقط مدیر). برای ریال quantity مبلغ ریال است و نرخ لازم نیست.
    /// occurredOn خالی یعنی همین لحظه؛ وگرنه سند با آن تاریخ (تا 30 روز قبل) ثبت می‌شود.
    /// </summary>
    public async Task<long?> RecordOpeningAsync(CurrentUser actor, int branchId, string currencyCode, decimal quantity, decimal? unitRateIrr, DateTime now, DateTime? occurredOn = null, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var currency = await LoadCurrencyAsync(currencyCode, ct);
        var ledger = await _repository.GetBranchLedgerAsync(branchId, ct);
        var occurredAt = OccurrenceRules.Resolve(occurredOn, now);
        var posting = LedgerPlanner.PlanOpening(ledger, currency, quantity, unitRateIrr, actor.Id, occurredAt, now);
        return await _repository.PostAsync(posting, ct);
    }

    public async Task OpeningIrrAsync(CurrentUser actor, int branchId, decimal amountIrr, DateTime now, CancellationToken ct = default)
    {
        await RecordOpeningAsync(actor, branchId, CurrencyCodes.Irr, amountIrr, null, now, null, ct);
    }

    public async Task OpeningForeignAsync(CurrentUser actor, int branchId, string currencyCode, decimal quantity, decimal unitRateIrr, DateTime now, CancellationToken ct = default)
    {
        await RecordOpeningAsync(actor, branchId, currencyCode, quantity, unitRateIrr, now, null, ct);
    }

    /// <summary>
    /// ابطال موجودی افتتاحیه (فقط مدیر). اگر معاملات بعدی به آن وابسته باشند و موجودی منفی شود، ابطال رد می‌شود.
    /// </summary>
    public async Task VoidOpeningAsync(CurrentUser actor, long openingId, string reason, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var opening = await _repository.GetOpeningAsync(openingId, ct)
            ?? throw new BusinessRuleException("سند افتتاحیه‌ی انتخابی یافت نشد.");
        var ledger = await _repository.GetBranchLedgerAsync(opening.BranchId, ct);
        var posting = LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Opening, openingId), reason, actor.Id, now);
        await _repository.PostAsync(posting, ct);
    }

    /// <summary>
    /// ویرایش موجودی افتتاحیه (فقط مدیر). ویرایش با ابطال نسخه‌ی قبلی و ثبت نسخه‌ی اصلاحی انجام می‌شود.
    /// </summary>
    public async Task<long?> EditOpeningAsync(CurrentUser actor, long openingId, decimal quantity, decimal? unitRateIrr, DateTime? occurredOn, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var old = await _repository.GetOpeningAsync(openingId, ct)
            ?? throw new BusinessRuleException("سند افتتاحیه‌ی انتخابی یافت نشد.");
        if (old.IsVoided)
        {
            throw new BusinessRuleException("این موجودی افتتاحیه قبلاً باطل شده است.");
        }

        var currency = await LoadCurrencyAsync(old.CurrencyCode, ct);
        var sameDay = occurredOn is null || occurredOn.Value.Date == old.OccurredAt.Date;
        var occurredAt = sameDay ? old.OccurredAt : OccurrenceRules.Resolve(occurredOn, now);
        var ledger = await _repository.GetBranchLedgerAsync(old.BranchId, ct);
        var posting = LedgerPlanner.PlanOpening(ledger, currency, quantity, unitRateIrr, actor.Id, occurredAt, now,
            new DocRef(LedgerDocKind.Opening, openingId), OpeningReplacementReason);
        return await _repository.PostAsync(posting, ct);
    }

    private async Task<CurrencyInfo> LoadCurrencyAsync(string currencyCode, CancellationToken ct)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currencies = await _repository.GetCurrenciesAsync(ct);
        return currencies.FirstOrDefault(c => c.Code == code)
            ?? throw new BusinessRuleException("ارز انتخابی یافت نشد.");
    }
}
