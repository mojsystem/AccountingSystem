using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class DashboardTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button _refresh = new() { Text = "نمایش", AutoSize = true };
    private readonly Label _summary = new()
    {
        Dock = DockStyle.Top,
        Height = 90,
        Padding = new Padding(12),
        Font = new Font("Tahoma", 10f, FontStyle.Bold),
    };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();

    public DashboardTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[] { UiHelpers.MakeLabel("شعبه:"), _branch, _refresh });

        Controls.Add(_grid);
        Controls.Add(_summary);
        Controls.Add(inputs);

        _refresh.Click += async (_, _) => await SafeRefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _branches = await _services.Permissions.GetBranchesAsync(_user);
        var previous = (_branch.SelectedItem as ComboItem)?.Value;
        UiHelpers.FillBranches(_branch, _branches, _user, includeAll: true, selectedValue: previous);

        var d = await _services.Reports.GetDashboardAsync(_user, UiHelpers.SelectedBranchId(_branch), DateTime.Now);
        var foreignValue = d.ForeignValueIrr.HasValue ? MoneyMath.FormatAmount(d.ForeignValueIrr.Value, 0) : "-";
        var line1 = $"موجودی صندوق ریال: {MoneyMath.FormatAmount(d.IrrBalance, 0)} ریال     |     ارزش موجودی ارزی: {foreignValue} ریال";
        var line2 = $"امروز - خرید: {d.TodayBuyCount} معامله ({MoneyMath.FormatAmount(d.TodayBuyIrr, 0)} ریال)     |     فروش: {d.TodaySellCount} معامله ({MoneyMath.FormatAmount(d.TodaySellIrr, 0)} ریال)";
        var line3 = $"سود تحقق‌یافته: {MoneyMath.FormatAmount(d.TodayProfitIrr, 0)} ریال     |     درآمد کارمزد: {MoneyMath.FormatAmount(d.TodayFeeIrr, 0)} ریال";
        _summary.Text = line1 + Environment.NewLine + line2 + Environment.NewLine + line3;

        var rows = d.Positions.Select(p => new[]
        {
            p.BranchName,
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
            new[] { "شعبه", "ارز", "موجودی", "بهای تمام‌شده (ریال)", "میانگین قیمت تمام‌شده", "نرخ فروش روز", "ارزش روز (ریال)", "سود/زیان شناور (ریال)" },
            rows);
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
