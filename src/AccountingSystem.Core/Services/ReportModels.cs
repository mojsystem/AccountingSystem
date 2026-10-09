using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>وضعیت یک ارز در یک شعبه: مقدار، بهای تمام‌شده و سود/زیان شناور (به نرخ فروش روز همان شعبه).</summary>
public sealed record PositionInfo(
    int BranchId,
    string BranchName,
    string CurrencyCode,
    string CurrencyName,
    int DecimalPlaces,
    decimal Quantity,
    decimal CostIrr,
    decimal AverageCostIrr,
    decimal? SellRateIrr,
    decimal? ValueIrr,
    decimal? UnrealizedProfitIrr);

/// <summary>خلاصه‌ی داشبورد برای شعبه‌ی انتخابی (یا همه‌ی شعبه‌ها برای مدیر). معاملات ابطال‌شده در جمع‌ها نیستند.</summary>
public sealed record DashboardInfo(
    decimal IrrBalance,
    decimal? ForeignValueIrr,
    int TodayBuyCount,
    decimal TodayBuyIrr,
    int TodaySellCount,
    decimal TodaySellIrr,
    decimal TodayProfitIrr,
    decimal TodayFeeIrr,
    IReadOnlyList<PositionInfo> Positions,
    IReadOnlyList<RateInfo> Rates);
