using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>وضعیت یک ارز در موجودی: مقدار، بهای تمام‌شده و سود/زیان شناور (به نرخ فروش روز).</summary>
public sealed record PositionInfo(
    string CurrencyCode,
    string CurrencyName,
    int DecimalPlaces,
    decimal Quantity,
    decimal CostIrr,
    decimal AverageCostIrr,
    decimal? SellRateIrr,
    decimal? ValueIrr,
    decimal? UnrealizedProfitIrr);

public sealed record DashboardInfo(
    decimal IrrBalance,
    decimal? ForeignValueIrr,
    int TodayBuyCount,
    decimal TodayBuyIrr,
    int TodaySellCount,
    decimal TodaySellIrr,
    decimal TodayProfitIrr,
    IReadOnlyList<PositionInfo> Positions,
    IReadOnlyList<RateInfo> Rates);
