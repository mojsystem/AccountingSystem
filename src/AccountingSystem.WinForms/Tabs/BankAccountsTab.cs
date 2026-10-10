using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>مدیریت حساب‌های بانکی نام‌دار و موجودی‌های افتتاحیه (فقط مدیر).</summary>
internal sealed class BankAccountsTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _name = new() { Width = 220, MaxLength = 100 };
    private readonly ComboBox _currency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox _openingBalance = new() { Width = 120, Text = "0" };
    private readonly TextBox _openingRate = new() { Width = 120 };
    private readonly Label _openingRateLabel = UiHelpers.MakeLabel("نرخ افتتاحیه (ریال برای هر واحد):");
    private readonly Button _create = new() { Text = "ایجاد حساب و ثبت افتتاحیه", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private IReadOnlyList<CurrencyInfo> _currencies = Array.Empty<CurrencyInfo>();
    private bool _filling;

    public BankAccountsTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("شعبه:"), _branch,
            UiHelpers.MakeLabel("نام حساب:"), _name,
            UiHelpers.MakeLabel("ارز:"), _currency,
            UiHelpers.MakeLabel("موجودی افتتاحیه:"), _openingBalance,
            _openingRateLabel, _openingRate,
            _create,
        });
        Controls.Add(_grid);
        Controls.Add(inputs);

        _currency.SelectedIndexChanged += (_, _) => UpdateRateVisibility();
        _create.Click += async (_, _) => await CreateAsync();
    }

    public async Task RefreshAsync()
    {
        _branches = await _services.Branches.GetBranchesAsync();
        _currencies = (await _services.Admin.GetCurrenciesAsync()).Where(currency => currency.IsActive).ToList();
        var accounts = await _services.BankAccounts.GetBankAccountsAsync(_user);

        _filling = true;
        try
        {
            var previousBranch = (_branch.SelectedItem as ComboItem)?.Value;
            _branch.Items.Clear();
            foreach (var branch in _branches)
            {
                _branch.Items.Add(new ComboItem(branch.Id.ToString(CultureInfo.InvariantCulture), $"{branch.Code} - {branch.Name}"));
            }
            UiHelpers.SelectByValue(_branch, previousBranch ?? _branches.FirstOrDefault()?.Id.ToString(CultureInfo.InvariantCulture));

            var previousCurrency = (_currency.SelectedItem as ComboItem)?.Value;
            _currency.Items.Clear();
            foreach (var currency in _currencies)
            {
                _currency.Items.Add(new ComboItem(currency.Code, $"{currency.Code} - {currency.Name}"));
            }
            UiHelpers.SelectByValue(_currency, previousCurrency ?? CurrencyCodes.Irr);
        }
        finally
        {
            _filling = false;
        }

        UpdateRateVisibility();
        UiHelpers.Fill(_grid,
            new[] { "شعبه", "نام حساب", "ارز", "موجودی افتتاحیه", "بهای افتتاحیه (ریال)", "موجودی فعلی", "بهای فعلی (ریال)", "ایجادکننده", "زمان ایجاد (شمسی)" },
            accounts.Select(account => new[]
            {
                account.BranchName,
                account.Name,
                account.CurrencyCode,
                MoneyMath.FormatAmount(account.OpeningBalance, account.DecimalPlaces),
                MoneyMath.FormatAmount(account.OpeningCostIrr, 0),
                MoneyMath.FormatAmount(account.Balance, account.DecimalPlaces),
                MoneyMath.FormatAmount(account.CostIrr, 0),
                account.CreatedBy,
                PersianDate.FormatDateTime(account.CreatedAt),
            }));
    }

    private void UpdateRateVisibility()
    {
        var showRate = !_filling && SelectedCurrencyCode() is { } code && code != CurrencyCodes.Irr;
        _openingRateLabel.Visible = showRate;
        _openingRate.Visible = showRate;
    }

    private string? SelectedCurrencyCode() => (_currency.SelectedItem as ComboItem)?.Value;

    private async Task CreateAsync()
    {
        try
        {
            var branchId = UiHelpers.RequiredBranchId(_branch);
            var currencyCode = SelectedCurrencyCode()
                ?? throw new BusinessRuleException("ارز حساب بانکی را انتخاب کنید.");
            if (!InputParser.TryParseDecimal(_openingBalance.Text, out var openingBalance))
            {
                throw new BusinessRuleException("موجودی افتتاحیه را به‌درستی وارد کنید.");
            }
            decimal? openingRate = null;
            if (currencyCode != CurrencyCodes.Irr && !string.IsNullOrWhiteSpace(_openingRate.Text))
            {
                if (!InputParser.TryParseDecimal(_openingRate.Text, out var parsedRate))
                {
                    throw new BusinessRuleException("نرخ افتتاحیه را به‌درستی وارد کنید.");
                }
                openingRate = parsedRate;
            }

            var id = await _services.BankAccounts.CreateAsync(_user, branchId, _name.Text, currencyCode,
                openingBalance, openingRate, DateTime.Now);
            _name.Clear();
            _openingBalance.Text = "0";
            _openingRate.Clear();
            UiHelpers.ShowInfo(this, $"حساب بانکی شماره {id} ایجاد شد و افتتاحیه‌ی آن در دفتر ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
