using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>مدیریت ارزها، نرخ‌ها، صندوق‌ها و موجودی‌های افتتاحیه.</summary>
public sealed class CurrencyAdminService
{
    private readonly IAccountingRepository _repository;

    public CurrencyAdminService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(CancellationToken ct = default) =>
        _repository.GetCurrenciesAsync(ct);

    public Task<IReadOnlyList<RateInfo>> GetLatestRatesAsync(CancellationToken ct = default) =>
        _repository.GetLatestRatesAsync(ct);

    public Task<IReadOnlyList<CashBoxInfo>> GetCashBoxesAsync(CancellationToken ct = default) =>
        _repository.GetCashBoxesAsync(ct);

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

    public async Task SetRateAsync(CurrentUser actor, string currencyCode, decimal buyRateIrr, decimal sellRateIrr, DateTime now, CancellationToken ct = default)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code == CurrencyCodes.Irr)
        {
            throw new BusinessRuleException("برای ریال نرخ تعریف نمی‌شود.");
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

        await _repository.AddRateAsync(code, buyRateIrr, sellRateIrr, actor.Id, now, ct);
    }

    public async Task OpeningIrrAsync(CurrentUser actor, decimal amountIrr, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var boxes = await _repository.GetCashBoxesAsync(ct);
        var irrBox = boxes.FirstOrDefault(b => b.CurrencyCode == CurrencyCodes.Irr)
            ?? throw new BusinessRuleException("صندوق ریال یافت نشد.");
        var posting = TradePlanner.PlanOpeningIrr(amountIrr, irrBox.Balance, actor.Id, now);
        await _repository.PostAsync(posting, ct);
    }

    public async Task OpeningForeignAsync(CurrentUser actor, string currencyCode, decimal quantity, decimal unitRateIrr, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var snapshot = await _repository.GetTradeSnapshotAsync(code, ct)
            ?? throw new BusinessRuleException("ارز انتخابی یافت نشد.");
        var posting = TradePlanner.PlanOpeningForeign(snapshot.Currency, quantity, unitRateIrr, snapshot, actor.Id, now);
        await _repository.PostAsync(posting, ct);
    }
}
