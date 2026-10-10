using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>مدیریت حساب‌های بانکی نام‌دار، به تفکیک شعبه و ارز.</summary>
public sealed class BankAccountService
{
    private readonly IAccountingRepository _repository;
    private readonly PermissionService _permissions;

    public BankAccountService(IAccountingRepository repository)
    {
        _repository = repository;
        _permissions = new PermissionService(repository);
    }

    public async Task<IReadOnlyList<BankAccountInfo>> GetBankAccountsAsync(
        CurrentUser actor,
        int? branchId = null,
        CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        return await _repository.GetBankAccountsAsync(scope, ct);
    }

    /// <summary>
    /// حساب را همراه با موجودی افتتاحیه ایجاد می‌کند. ایجاد حساب‌های بانکی فقط برای مدیر مجاز است.
    /// موجودی افتتاحیه در همان تراکنش به دفتر کل و گزارش رویدادها متصل می‌شود.
    /// </summary>
    public async Task<int> CreateAsync(
        CurrentUser actor,
        int branchId,
        string? name,
        string? currencyCode,
        decimal openingBalance,
        decimal? openingRateIrr,
        DateTime now,
        CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        if (branchId <= 0 || (await _repository.GetBranchesAsync(ct)).All(branch => branch.Id != branchId))
        {
            throw new BusinessRuleException("شعبه‌ی حساب بانکی را انتخاب کنید.");
        }

        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length is < 2 or > 100)
        {
            throw new BusinessRuleException("نام حساب بانکی باید بین ۲ تا ۱۰۰ نویسه باشد.");
        }

        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currency = (await _repository.GetCurrenciesAsync(ct))
            .FirstOrDefault(item => item.Code == code && item.IsActive)
            ?? throw new BusinessRuleException("ارز فعال حساب بانکی را انتخاب کنید.");
        if (openingBalance < 0m || openingBalance != MoneyMath.RoundTo(openingBalance, currency.DecimalPlaces))
        {
            throw new BusinessRuleException($"موجودی افتتاحیه‌ی {code} باید نامنفی و حداکثر تا {currency.DecimalPlaces} رقم اعشار باشد.");
        }

        decimal openingCostIrr;
        if (code == CurrencyCodes.Irr)
        {
            if (openingBalance != MoneyMath.RoundIrr(openingBalance))
            {
                throw new BusinessRuleException("موجودی افتتاحیه‌ی حساب ریالی باید عدد صحیح ریال باشد.");
            }
            openingCostIrr = openingBalance;
        }
        else if (openingBalance == 0m)
        {
            openingCostIrr = 0m;
        }
        else
        {
            if (openingRateIrr is null)
            {
                throw new BusinessRuleException("برای افتتاحیه‌ی حساب ارزی، نرخ ریالی هر واحد را وارد کنید.");
            }
            TradePlanner.ValidateRate(openingRateIrr.Value);
            openingCostIrr = MoneyMath.RoundIrr(openingBalance * openingRateIrr.Value);
            if (openingCostIrr <= 0m)
            {
                throw new BusinessRuleException("بهای ریالی موجودی افتتاحیه باید بزرگ‌تر از صفر باشد.");
            }
        }

        return await _repository.AddBankAccountAsync(
            new BankAccountRecord(branchId, cleanName, code, openingBalance, openingCostIrr),
            actor.Id,
            now,
            ct);
    }
}
