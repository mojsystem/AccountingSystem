using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>ثبت و ابطال معاملات خرید و فروش ارز (با کنترل موجودی و ایجاد سند حسابداری).</summary>
public sealed class CurrencyTradeService
{
    private readonly IAccountingRepository _repository;

    public CurrencyTradeService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public async Task<long> BuyFromCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default)
    {
        var branchId = BranchScope.RequireBranch(user, input.BranchId);
        var snapshot = await LoadSnapshotAsync(branchId, input.CurrencyCode, ct);
        var posting = TradePlanner.PlanBuy(input, snapshot, user.Id, now);
        return await SaveAsync(posting, ct);
    }

    public async Task<long> SellToCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default)
    {
        var branchId = BranchScope.RequireBranch(user, input.BranchId);
        var snapshot = await LoadSnapshotAsync(branchId, input.CurrencyCode, ct);
        var posting = TradePlanner.PlanSell(input, snapshot, user.Id, now);
        return await SaveAsync(posting, ct);
    }

    /// <summary>
    /// ابطال معامله (فقط مدیر). سند معکوس ثبت می‌شود و معامله‌ی اصلی به‌عنوان باطل‌شده علامت می‌خورد.
    /// </summary>
    public async Task<long> VoidTradeAsync(CurrentUser user, long tradeId, string reason, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(user);
        var context = await _repository.GetTradeForVoidAsync(tradeId, ct)
            ?? throw new BusinessRuleException("معامله‌ی انتخابی یافت نشد.");
        var posting = TradePlanner.PlanVoid(context, reason, user.Id, now);
        return await SaveAsync(posting, ct);
    }

    private async Task<TradeSnapshot> LoadSnapshotAsync(int branchId, string currencyCode, CancellationToken ct)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var snapshot = await _repository.GetTradeSnapshotAsync(branchId, code, ct);
        return snapshot ?? throw new BusinessRuleException("ارز یا شعبه‌ی انتخابی یافت نشد.");
    }

    private async Task<long> SaveAsync(PostingDraft posting, CancellationToken ct)
    {
        var tradeId = await _repository.PostAsync(posting, ct);
        return tradeId ?? throw new InvalidOperationException("شناسه‌ی معامله ایجاد نشد.");
    }
}
