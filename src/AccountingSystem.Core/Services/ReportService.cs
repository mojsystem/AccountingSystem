using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>گزارش‌ها: داشبورد، معاملات و اسناد حسابداری. همه‌ی گزارش‌ها طبق دسترسی شعبه‌ی کاربر فیلتر می‌شوند.</summary>
public sealed class ReportService
{
    private readonly IAccountingRepository _repository;

    public ReportService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public async Task<DashboardInfo> GetDashboardAsync(CurrentUser actor, int? branchId, DateTime now, CancellationToken ct = default)
    {
        var scope = BranchScope.ResolveForReport(actor, branchId);
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

    public Task<IReadOnlyList<TradeInfo>> GetTradesAsync(CurrentUser actor, int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default) =>
        _repository.GetTradesAsync(BranchScope.ResolveForReport(actor, branchId), fromInclusive, toExclusive, ct);

    public Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(CurrentUser actor, int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default) =>
        _repository.GetJournalAsync(BranchScope.ResolveForReport(actor, branchId), fromInclusive, toExclusive, ct);

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
