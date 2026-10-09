using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Receipts;

namespace AccountingSystem.Core.Services;

/// <summary>ساخت رسید HTML برای یک معامله. کاربر صندوق فقط رسید شعبه‌ی خودش را می‌بیند.</summary>
public sealed class ReceiptService
{
    private readonly IAccountingRepository _repository;

    public ReceiptService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public async Task<string> RenderTradeReceiptAsync(CurrentUser actor, long tradeId, CancellationToken ct = default)
    {
        var trade = await _repository.GetTradeAsync(tradeId, ct)
            ?? throw new BusinessRuleException("معامله‌ی انتخابی یافت نشد.");
        if (actor.Role != UserRole.Admin && actor.BranchId != trade.BranchId)
        {
            throw new BusinessRuleException("دسترسی به رسید این شعبه برای کاربر جاری مجاز نیست.");
        }

        var currencies = await _repository.GetCurrenciesAsync(ct);
        var currency = currencies.FirstOrDefault(c => c.Code == trade.CurrencyCode)
            ?? new CurrencyInfo(trade.CurrencyCode, trade.CurrencyCode, 2, true);
        return ReceiptHtml.Render(new ReceiptData(trade, currency.Name, currency.DecimalPlaces));
    }
}
