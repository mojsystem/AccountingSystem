using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>گزارش‌ها: داشبورد، موجودی ارزی، معاملات و اسناد حسابداری.</summary>
public sealed class ReportService
{
    private readonly IAccountingRepository _repository;

    public ReportService(IAccountingRepository repository)
    {
        _repository = repository;
    }

    public async Task<DashboardInfo> GetDashboardAsync(DateTime now, CancellationToken ct = default)
    {
        var currencies = await _repository.GetCurrenciesAsync(ct);
        var boxes = await _repository.GetCashBoxesAsync(ct);
        var costs = await _repository.GetInventoryCostsAsync(ct);
        var rates = await _repository.GetLatestRatesAsync(ct);
        var dayStart = now.Date;
        var trades = await _repository.GetTradesAsync(dayStart, dayStart.AddDays(1), ct);

        var positions = BuildPositions(currencies, boxes, costs, rates);
        var irrBalance = boxes.FirstOrDefault(b => b.CurrencyCode == CurrencyCodes.Irr)?.Balance ?? 0m;
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
            positions,
            rates);
    }

    public Task<IReadOnlyList<TradeInfo>> GetTradesAsync(DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default) =>
        _repository.GetTradesAsync(fromInclusive, toExclusive, ct);

    public Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default) =>
        _repository.GetJournalAsync(fromInclusive, toExclusive, ct);

    private static IReadOnlyList<PositionInfo> BuildPositions(
        IReadOnlyList<CurrencyInfo> currencies,
        IReadOnlyList<CashBoxInfo> boxes,
        IReadOnlyDictionary<string, decimal> costs,
        IReadOnlyList<RateInfo> rates)
    {
        var positions = new List<PositionInfo>();
        foreach (var currency in currencies.Where(c => c.Code != CurrencyCodes.Irr))
        {
            var quantity = boxes.FirstOrDefault(b => b.CurrencyCode == currency.Code)?.Balance ?? 0m;
            var costIrr = costs.TryGetValue(currency.Code, out var storedCost) ? storedCost : 0m;
            var averageCost = quantity > 0 ? costIrr / quantity : 0m;
            var rate = rates.FirstOrDefault(r => r.CurrencyCode == currency.Code);
            decimal? sellRate = rate?.SellRateIrr;
            decimal? value = sellRate.HasValue ? MoneyMath.RoundIrr(quantity * sellRate.Value) : (decimal?)null;
            decimal? unrealized = value.HasValue ? value.Value - costIrr : (decimal?)null;

            positions.Add(new PositionInfo(
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
