using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>گزارش‌ها: داشبورد، معاملات و اسناد حسابداری. همه‌ی گزارش‌ها طبق دسترسی شعبه‌ی کاربر فیلتر می‌شوند.</summary>
public sealed class ReportService
{
    private readonly IAccountingRepository _repository;
    private readonly PermissionService _permissions;

    public ReportService(IAccountingRepository repository)
    {
        _repository = repository;
        _permissions = new PermissionService(repository);
    }

    public async Task<DashboardInfo> GetDashboardAsync(CurrentUser actor, int? branchId, DateTime now, CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        var currencies = await _repository.GetCurrenciesAsync(ct);
        var boxes = await _repository.GetCashBoxesAsync(scope, ct);
        var inventory = await _repository.GetInventoryAsync(scope, ct);
        var rates = await _repository.GetLatestRatesAsync(scope, ct);
        var dayStart = now.Date;
        var trades = (await _repository.GetTradesAsync(scope, dayStart, dayStart.AddDays(1), ct))
            .Where(t => !t.IsVoided)
            .ToList();

        var positions = BuildPositions(currencies, boxes, inventory, rates);
        var irrBalance = boxes.Where(b => b.CurrencyCode == CurrencyCodes.Irr).Sum(b => b.Balance);
        var buys = trades.Where(t => t.Type == TradeType.Buy).ToList();
        var sells = trades.Where(t => t.Type == TradeType.Sell).ToList();
        decimal? foreignValue = positions.Any(p => p.ValueIrr.HasValue)
            ? positions.Where(p => p.ValueIrr.HasValue).Sum(p => p.ValueIrr!.Value)
            : null;

        return new DashboardInfo(
            irrBalance,
            foreignValue,
            buys.Count,
            buys.Sum(t => t.IrrAmount),
            sells.Count,
            sells.Sum(t => t.IrrAmount),
            sells.Sum(t => t.ProfitIrr),
            trades.Sum(t => t.FeeIrr),
            positions,
            rates);
    }

    public async Task<IReadOnlyList<TradeInfo>> GetTradesAsync(CurrentUser actor, int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        return await _repository.GetTradesAsync(scope, fromInclusive, toExclusive, ct);
    }

    public async Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(CurrentUser actor, int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        return await _repository.GetJournalAsync(scope, fromInclusive, toExclusive, ct);
    }

    /// <summary>مانده‌ی بدهکار/بستانکار اشخاص در شعبه‌ی انتخابی یا همه‌ی شعبه‌های قابل مشاهده.</summary>
    public async Task<IReadOnlyList<CustomerBalanceReportRow>> GetCustomerBalancesAsync(
        CurrentUser actor,
        int? branchId,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        return await _repository.GetCustomerBalancesAsync(scope, toExclusive, ct);
    }

    /// <summary>گردش معین شخص با مانده‌ی اول دوره و ریز بدهکار/بستانکار در بازه‌ی انتخابی.</summary>
    public async Task<CustomerLedgerReport> GetCustomerLedgerAsync(
        CurrentUser actor,
        int? branchId,
        int customerId,
        DateTime fromInclusive,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        if (toExclusive <= fromInclusive)
        {
            throw new BusinessRuleException("بازه‌ی گزارش معین اشخاص معتبر نیست.");
        }

        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        var customer = await _repository.GetCustomerAsync(customerId, ct)
            ?? throw new BusinessRuleException("مشتری انتخاب‌شده پیدا نشد.");
        var data = await _repository.GetCustomerLedgerAsync(customerId, scope, fromInclusive, toExclusive, ct);
        var closing = data.Lines.Count > 0 ? data.Lines[^1].BalanceIrr : data.OpeningBalanceIrr;
        return new CustomerLedgerReport(customer, data.OpeningBalanceIrr, data.Lines, closing);
    }

    /// <summary>فهرست سرفصل‌ها برای انتخاب در گزارش؛ هر کاربر مجاز به خواندن حداقل یک شعبه است.</summary>
    public async Task<IReadOnlyList<AccountInfo>> GetAccountReportOptionsAsync(CurrentUser actor, CancellationToken ct = default)
    {
        await _permissions.ResolveReadBranchAsync(actor, null, ct);
        return await _repository.GetAccountsAsync(ct);
    }

    /// <summary>فهرست صندوق‌ها در محدوده‌ی شعبه‌های قابل مشاهده.</summary>
    public async Task<IReadOnlyList<CashBoxInfo>> GetCashBoxReportOptionsAsync(CurrentUser actor, int? branchId, CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        return await _repository.GetCashBoxesAsync(scope, ct);
    }

    /// <summary>فهرست حساب‌های بانکی در محدوده‌ی شعبه‌های قابل مشاهده.</summary>
    public async Task<IReadOnlyList<BankAccountInfo>> GetBankAccountReportOptionsAsync(CurrentUser actor, int? branchId, CancellationToken ct = default)
    {
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        return await _repository.GetBankAccountsAsync(scope, ct);
    }

    /// <summary>مانده‌ی بدهکار/بستانکار یک حساب تا پایان تاریخ؛ انتخاب roll-up فرزندان مستقل است.</summary>
    public async Task<AccountBalanceReport> GetAccountBalanceReportAsync(
        CurrentUser actor,
        int? branchId,
        string accountCode,
        bool includeDescendants,
        DateTime asOf,
        CancellationToken ct = default)
    {
        var account = await FindReportAccountAsync(actor, accountCode, ct);
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        var toExclusive = asOf.Date.AddDays(1);
        var balance = await _repository.GetAccountBalanceAsync(account.Code, includeDescendants, scope, toExclusive, ct);
        return new AccountBalanceReport(account, includeDescendants, asOf.Date, balance);
    }

    /// <summary>معین حساب در بازه؛ حساب‌های فرزند فقط در صورت انتخاب roll-up جمع می‌شوند.</summary>
    public async Task<AccountLedgerReport> GetAccountLedgerReportAsync(
        CurrentUser actor,
        int? branchId,
        string accountCode,
        bool includeDescendants,
        DateTime fromInclusive,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        if (toExclusive <= fromInclusive)
        {
            throw new BusinessRuleException("بازه‌ی گزارش معین حساب معتبر نیست.");
        }

        var account = await FindReportAccountAsync(actor, accountCode, ct);
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        var data = await _repository.GetAccountLedgerAsync(account.Code, includeDescendants, scope, fromInclusive, toExclusive, ct);
        var closing = data.Lines.Count > 0 ? data.Lines[^1].BalanceIrr : data.OpeningBalanceIrr;
        return new AccountLedgerReport(account, includeDescendants, fromInclusive, toExclusive,
            data.OpeningBalanceIrr, data.Lines, closing);
    }

    /// <summary>معین یک صندوق یا همه‌ی صندوق‌های قابل مشاهده؛ fromInclusive تهی یعنی گزارش مانده تا تاریخ است.</summary>
    public async Task<IReadOnlyList<CashBoxLedgerReport>> GetCashBoxLedgerReportsAsync(
        CurrentUser actor,
        int? branchId,
        int? cashBoxId,
        DateTime? fromInclusive,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        ValidateOperationalLedgerRange(fromInclusive, toExclusive);
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        if (cashBoxId is { } selectedId)
        {
            var boxes = await _repository.GetCashBoxesAsync(scope, ct);
            if (boxes.All(box => box.Id != selectedId))
            {
                throw new BusinessRuleException("صندوق انتخاب‌شده در محدوده‌ی دسترسی پیدا نشد.");
            }
        }

        var data = await _repository.GetCashBoxLedgersAsync(scope, cashBoxId, fromInclusive, toExclusive, ct);
        return data.Select(item => new CashBoxLedgerReport(
            item.CashBox,
            item.DecimalPlaces,
            fromInclusive,
            toExclusive,
            item.OpeningBalance,
            item.Lines,
            item.Lines.Count > 0 ? item.Lines[^1].Balance : item.OpeningBalance)).ToList();
    }

    /// <summary>معین یک حساب بانکی نام‌دار یا همه‌ی حساب‌های بانکی قابل مشاهده.</summary>
    public async Task<IReadOnlyList<BankAccountLedgerReport>> GetBankAccountLedgerReportsAsync(
        CurrentUser actor,
        int? branchId,
        int? bankAccountId,
        DateTime? fromInclusive,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        ValidateOperationalLedgerRange(fromInclusive, toExclusive);
        var scope = await _permissions.ResolveReadBranchAsync(actor, branchId, ct);
        if (bankAccountId is { } selectedId)
        {
            var accounts = await _repository.GetBankAccountsAsync(scope, ct);
            if (accounts.All(account => account.Id != selectedId))
            {
                throw new BusinessRuleException("حساب بانکی انتخاب‌شده در محدوده‌ی دسترسی پیدا نشد.");
            }
        }

        var data = await _repository.GetBankAccountLedgersAsync(scope, bankAccountId, fromInclusive, toExclusive, ct);
        return data.Select(item => new BankAccountLedgerReport(
            item.BankAccount,
            fromInclusive,
            toExclusive,
            item.OpeningBalance,
            item.Lines,
            item.Lines.Count > 0 ? item.Lines[^1].Balance : item.OpeningBalance)).ToList();
    }

    private async Task<AccountInfo> FindReportAccountAsync(CurrentUser actor, string accountCode, CancellationToken ct)
    {
        var accounts = await GetAccountReportOptionsAsync(actor, ct);
        return accounts.FirstOrDefault(account => account.Code == accountCode)
            ?? throw new BusinessRuleException("سرفصل انتخاب‌شده پیدا نشد.");
    }

    private static void ValidateOperationalLedgerRange(DateTime? fromInclusive, DateTime toExclusive)
    {
        if (toExclusive == DateTime.MinValue || (fromInclusive is { } from && toExclusive <= from))
        {
            throw new BusinessRuleException("بازه‌ی گزارش معین صندوق یا حساب بانکی معتبر نیست.");
        }
    }

    private static IReadOnlyList<PositionInfo> BuildPositions(
        IReadOnlyList<CurrencyInfo> currencies,
        IReadOnlyList<CashBoxInfo> boxes,
        IReadOnlyList<InventoryInfo> inventory,
        IReadOnlyList<RateInfo> rates)
    {
        var positions = new List<PositionInfo>();
        foreach (var box in boxes.Where(b => b.CurrencyCode != CurrencyCodes.Irr))
        {
            var currency = currencies.FirstOrDefault(c => c.Code == box.CurrencyCode);
            if (currency is null)
            {
                continue;
            }

            var quantity = box.Balance;
            var costIrr = inventory.FirstOrDefault(i => i.BranchId == box.BranchId && i.CurrencyCode == box.CurrencyCode)?.TotalCostIrr ?? 0m;
            var averageCost = quantity > 0 ? costIrr / quantity : 0m;
            var rate = rates.FirstOrDefault(r => r.BranchId == box.BranchId && r.CurrencyCode == box.CurrencyCode);
            decimal? sellRate = rate?.SellRateIrr;
            decimal? value = sellRate.HasValue ? MoneyMath.RoundIrr(quantity * sellRate.Value) : (decimal?)null;
            decimal? unrealized = value.HasValue ? value.Value - costIrr : (decimal?)null;

            positions.Add(new PositionInfo(
                box.BranchId,
                box.BranchName,
                currency.Code,
                currency.Name,
                currency.DecimalPlaces,
                quantity,
                costIrr,
                averageCost,
                sellRate,
                value,
                unrealized));
        }
        return positions;
    }
}
