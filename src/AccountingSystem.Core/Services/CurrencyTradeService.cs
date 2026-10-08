using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>ثبت معاملات خرید و فروش ارز (با کنترل موجودی و ایجاد سند حسابداری).</summary>
public sealed class CurrencyTradeService
{
    private readonly IAccountingRepository _repository;

    public CurrencyTradeService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public async Task<long> BuyFromCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default)
    {
        var snapshot = await LoadSnapshotAsync(input.CurrencyCode, ct);
        var posting = TradePlanner.PlanBuy(input, snapshot, user.Id, now);
        return await SaveAsync(posting, ct);
    }

    public async Task<long> SellToCustomerAsync(TradeInput input, CurrentUser user, DateTime now, CancellationToken ct = default)
    {
        var snapshot = await LoadSnapshotAsync(input.CurrencyCode, ct);
        var posting = TradePlanner.PlanSell(input, snapshot, user.Id, now);
        return await SaveAsync(posting, ct);
    }

    private async Task<TradeSnapshot> LoadSnapshotAsync(string currencyCode, CancellationToken ct)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var snapshot = await _repository.GetTradeSnapshotAsync(code, ct);
        return snapshot ?? throw new BusinessRuleException("ارز انتخابی یافت نشد.");
    }

    private async Task<long> SaveAsync(PostingDraft posting, CancellationToken ct)
    {
        var tradeId = await _repository.PostAsync(posting, ct);
        return tradeId ?? throw new InvalidOperationException("شناسه معامله ایجاد نشد.");
    }
}
