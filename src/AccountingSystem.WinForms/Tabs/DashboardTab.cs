using AccountingSystem.Core.Common;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class DashboardTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly Label _summary = new()
    {
        Dock = DockStyle.Top,
        Height = 72,
        Padding = new Padding(12),
        Font = new Font("Tahoma", 10f, FontStyle.Bold),
    };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();

    public DashboardTab(AppServices services)
    {
        _services = services;
        Controls.Add(_grid);
        Controls.Add(_summary);
    }

    public async Task RefreshAsync()
    {
        var d = await _services.Reports.GetDashboardAsync(DateTime.Now);
        var foreignValue = d.ForeignValueIrr.HasValue ? MoneyMath.FormatAmount(d.ForeignValueIrr.Value, 0) : "-";
        var line1 = $"موجودی صندوق ریال: {MoneyMath.FormatAmount(d.IrrBalance, 0)} ریال     |     ارزش موجودی ارزی: {foreignValue} ریال";
        var line2 = $"امروز - خرید: {d.TodayBuyCount} معامله ({MoneyMath.FormatAmount(d.TodayBuyIrr, 0)} ریال)     |     فروش: {d.TodaySellCount} معامله ({MoneyMath.FormatAmount(d.TodaySellIrr, 0)} ریال)     |     سود تحقق‌یافته: {MoneyMath.FormatAmount(d.TodayProfitIrr, 0)} ریال";
        _summary.Text = line1 + Environment.NewLine + line2;

        var rows = d.Positions.Select(p => new[]
        {
            $"{p.CurrencyCode} - {p.CurrencyName}",
            MoneyMath.FormatRate(p.Quantity),
            MoneyMath.FormatAmount(p.CostIrr, 0),
            MoneyMath.FormatAmount(p.AverageCostIrr, 2),
            p.SellRateIrr.HasValue ? MoneyMath.FormatAmount(p.SellRateIrr.Value, 0) : "-",
            p.ValueIrr.HasValue ? MoneyMath.FormatAmount(p.ValueIrr.Value, 0) : "-",
            p.UnrealizedProfitIrr.HasValue ? MoneyMath.FormatAmount(p.UnrealizedProfitIrr.Value, 0) : "-",
        });

        UiHelpers.Fill(
            _grid,
            new[] { "ارز", "موجودی", "بهای تمام‌شده (ریال)", "میانگین قیمت تمام‌شده", "نرخ فروش روز", "ارزش روز (ریال)", "سود/زیان شناور (ریال)" },
            rows);
    }
}
