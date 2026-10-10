using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class RatesTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _currency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _buyRate = new() { Width = 130 };
    private readonly TextBox _sellRate = new() { Width = 130 };
    private readonly Button _setRate = new() { Text = "ثبت نرخ", AutoSize = true };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly TextBox _newCode = new() { Width = 80, MaxLength = 3 };
    private readonly TextBox _newName = new() { Width = 180 };
    private readonly NumericUpDown _newDecimals = new() { Minimum = 0, Maximum = 4, Value = 2, Width = 70 };
    private readonly Button _addCurrency = new() { Text = "افزودن ارز", AutoSize = true };
    private readonly FlowLayoutPanel _adminPanel = UiHelpers.CreateInputPanel();
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<RateInfo> _rates = Array.Empty<RateInfo>();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private bool _filling;

    public RatesTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("شعبه:"), _branch,
            UiHelpers.MakeLabel("ارز:"), _currency,
            UiHelpers.MakeLabel("نرخ خرید از مشتری (ریال):"), _buyRate,
            UiHelpers.MakeLabel("نرخ فروش به مشتری (ریال):"), _sellRate,
            _setRate,
            _show,
        });

        _adminPanel.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("کد ارز (۳ حرف):"), _newCode,
            UiHelpers.MakeLabel("نام ارز:"), _newName,
            UiHelpers.MakeLabel("ارقام اعشار:"), _newDecimals,
            _addCurrency,
        });
        _adminPanel.Visible = user.Role == UserRole.Admin;

        Controls.Add(_grid);
        Controls.Add(_adminPanel);
        Controls.Add(inputs);

        _currency.SelectedIndexChanged += (_, _) => ShowCurrentRate();
        _branch.SelectedIndexChanged += (_, _) => ShowCurrentRate();
        _setRate.Click += async (_, _) => await SetRateAsync();
        _show.Click += async (_, _) => await SafeRefreshAsync();
        _addCurrency.Click += async (_, _) => await AddCurrencyAsync();
    }

    public async Task RefreshAsync()
    {
        var currencies = await _services.Admin.GetCurrenciesAsync();
        _rates = await _services.Admin.GetLatestRatesAsync(_user, UiHelpers.SelectedBranchId(_branch));
        _branches = await _services.Permissions.GetBranchesAsync(_user, Permission.RateSet);

        _filling = true;
        try
        {
            var previousBranch = (_branch.SelectedItem as ComboItem)?.Value;
            UiHelpers.FillBranches(_branch, _branches, _user, includeAll: false, selectedValue: previousBranch);

            var previous = (_currency.SelectedItem as ComboItem)?.Value;
            _currency.Items.Clear();
            foreach (var c in currencies.Where(x => x.IsActive && x.Code != CurrencyCodes.Irr))
            {
                _currency.Items.Add(new ComboItem(c.Code, $"{c.Code} - {c.Name}"));
            }
            UiHelpers.SelectByValue(_currency, previous);
        }
        finally
        {
            _filling = false;
        }

        ShowCurrentRate();

        var rows = _rates.Select(r => new[]
        {
            r.BranchName,
            $"{r.CurrencyCode} - {r.CurrencyName}",
            MoneyMath.FormatRate(r.BuyRateIrr),
            MoneyMath.FormatRate(r.SellRateIrr),
            PersianDate.FormatDateTime(r.CreatedAt),
        });
        UiHelpers.Fill(_grid, new[] { "شعبه", "ارز", "نرخ خرید از مشتری (ریال)", "نرخ فروش به مشتری (ریال)", "زمان ثبت (شمسی)" }, rows);
    }

    private void ShowCurrentRate()
    {
        if (_filling || _currency.SelectedItem is not ComboItem item || UiHelpers.SelectedBranchId(_branch) is not { } branchId)
        {
            return;
        }
        var rate = _rates.FirstOrDefault(r => r.BranchId == branchId && r.CurrencyCode == item.Value);
        _buyRate.Text = rate is null ? string.Empty : MoneyMath.FormatRate(rate.BuyRateIrr);
        _sellRate.Text = rate is null ? string.Empty : MoneyMath.FormatRate(rate.SellRateIrr);
    }

    private async Task SetRateAsync()
    {
        try
        {
            if (_currency.SelectedItem is not ComboItem item)
            {
                throw new BusinessRuleException("ارز را انتخاب کنید.");
            }
            var branchId = UiHelpers.RequiredBranchId(_branch);
            if (!InputParser.TryParseDecimal(_buyRate.Text, out var buy) || !InputParser.TryParseDecimal(_sellRate.Text, out var sell))
            {
                throw new BusinessRuleException("نرخ خرید و فروش را به‌درستی وارد کنید.");
            }

            await _services.Admin.SetRateAsync(_user, branchId, item.Value, buy, sell, DateTime.Now);
            UiHelpers.ShowInfo(this, "نرخ جدید ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task AddCurrencyAsync()
    {
        try
        {
            await _services.Admin.AddCurrencyAsync(_user, _newCode.Text, _newName.Text, (int)_newDecimals.Value, DateTime.Now);
            _newCode.Clear();
            _newName.Clear();
            UiHelpers.ShowInfo(this, "ارز جدید برای همه‌ی شعبه‌ها اضافه شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
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
