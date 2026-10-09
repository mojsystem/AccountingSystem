using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// ثبت، ابطال و ویرایش معاملات خرید و فروش ارز. هر تغییر کل تاریخچه‌ی شعبه را بازمحاسبه می‌کند.
/// ثبت با تاریخ گذشته تا <see cref="OccurrenceRules.MaxBackdateDays"/> روز مجاز است. ابطال و ویرایش فقط برای مدیر است.
/// </summary>
public sealed class CurrencyTradeService
{
    /// <summary>دلیل ثبت شده برای معامله‌ای که نسخه‌ی اصلاحی آن جایگزین شده است.</summary>
    public const string ReplacementReason = "ویرایش معامله؛ نسخه‌ی اصلاحی جایگزین شد";

    private readonly IAccountingRepository _repository;

    public CurrencyTradeService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public Task<long> BuyFromCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default) =>
        RecordTradeAsync(input, TradeType.Buy, user, now, null, ct);

    public Task<long> SellToCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default) =>
        RecordTradeAsync(input, TradeType.Sell, user, now, null, ct);

    /// <summary>خواندن یک معامله برای نمایش و ویرایش؛ کاربر صندوق فقط معامله‌ی شعبه‌ی خودش را می‌بیند.</summary>
    public async Task<TradeInfo?> GetTradeAsync(CurrentUser user, long tradeId, CancellationToken ct = default)
    {
        var trade = await _repository.GetTradeAsync(tradeId, ct);
        if (trade is not null)
        {
            BranchScope.ResolveForReport(user, trade.BranchId);
        }
        return trade;
    }

    /// <summary>
    /// ثبت معامله. occurredOn خالی یعنی همین لحظه؛ وگرنه معامله با آن تاریخ (تا 30 روز قبل) ثبت می‌شود.
    /// </summary>
    public async Task<long> RecordTradeAsync(TradeInput input, TradeType type, CurrentUser user, DateTime now, DateTime? occurredOn = null, CancellationToken ct = default)
    {
        var branchId = BranchScope.RequireBranch(user, input.BranchId);
        var scoped = input with { BranchId = branchId };
        var currency = await LoadCurrencyAsync(scoped.CurrencyCode, ct);
        var ledger = await _repository.GetBranchLedgerAsync(branchId, ct);
        var occurredAt = OccurrenceRules.Resolve(occurredOn, now);
        var posting = LedgerPlanner.PlanTrade(ledger, currency, scoped, type, user.Id, occurredAt, now);
        var tradeId = await _repository.PostAsync(posting, ct);
        return tradeId ?? throw new InvalidOperationException("شناسه‌ی معامله ایجاد نشد.");
    }

    /// <summary>
    /// ابطال هر معامله‌ای (فقط مدیر). سند معکوس ثبت می‌شود و موجودی و بهای فروش‌های بعدی بازمحاسبه می‌شود.
    /// اگر این کار باعث موجودی منفی شود، ابطال رد می‌شود.
    /// </summary>
    public async Task VoidTradeAsync(CurrentUser user, long tradeId, string reason, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(user);
        var trade = await _repository.GetTradeAsync(tradeId, ct)
            ?? throw new BusinessRuleException("معامله‌ی انتخابی یافت نشد.");
        var ledger = await _repository.GetBranchLedgerAsync(trade.BranchId, ct);
        var posting = LedgerPlanner.PlanVoid(ledger, new DocRef(LedgerDocKind.Trade, tradeId), reason, user.Id, now);
        await _repository.PostAsync(posting, ct);
    }

    /// <summary>
    /// ویرایش معامله (فقط مدیر). اگر فقط اطلاعات توصیفی (مشتری، کد ملی، یادداشت) تغییر کند، همان سند به‌روز می‌شود.
    /// اگر نوع، ارز، مبلغ، نرخ، کارمزد یا تاریخ تغییر کند، معامله باطل و نسخه‌ی اصلاحی جایگزین آن می‌شود.
    /// خروجی شناسه‌ی نسخه‌ی جدید است؛ null یعنی فقط اطلاعات توصیفی به‌روز شد.
    /// </summary>
    public async Task<long?> EditTradeAsync(CurrentUser user, long tradeId, TradeInput input, TradeType type, DateTime? occurredOn, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(user);
        var old = await _repository.GetTradeAsync(tradeId, ct)
            ?? throw new BusinessRuleException("معامله‌ی انتخابی یافت نشد.");
        if (old.IsVoided)
        {
            throw new BusinessRuleException("معامله‌ی باطل‌شده قابل ویرایش نیست.");
        }

        var currency = await LoadCurrencyAsync(input.CurrencyCode, ct);
        var sameDay = occurredOn is null || occurredOn.Value.Date == old.OccurredAt.Date;
        var occurredAt = sameDay ? old.OccurredAt : OccurrenceRules.Resolve(occurredOn, now);
        var financialChanged = type != old.Type
            || currency.Code != old.CurrencyCode
            || input.Amount != old.Amount
            || input.Rate != old.Rate
            || input.FeeIrr != old.FeeIrr
            || !sameDay;

        if (!financialChanged)
        {
            var (customer, nationalCode, note) = TradePlanner.CleanDetails(input.CustomerName, input.NationalCode, input.Note);
            await _repository.UpdateTradeDetailsAsync(tradeId, old.BranchId, customer, nationalCode, note, user.Id, now, ct);
            return null;
        }

        var ledger = await _repository.GetBranchLedgerAsync(old.BranchId, ct);
        var scoped = input with { BranchId = old.BranchId };
        var posting = LedgerPlanner.PlanTrade(ledger, currency, scoped, type, user.Id, occurredAt, now,
            new DocRef(LedgerDocKind.Trade, tradeId), ReplacementReason);
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
