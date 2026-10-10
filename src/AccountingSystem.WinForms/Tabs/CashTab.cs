using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class CashTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;

    private readonly ComboBox _filterBranch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly Button _excel = new() { Text = "خروجی Excel", AutoSize = true };

    private readonly FlowLayoutPanel _openingPanel = UiHelpers.CreateInputPanel();
    private readonly ComboBox _openBranch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _openIrr = new() { Width = 150 };
    private readonly Button _saveOpeningIrr = new() { Text = "ثبت موجودی اولیه ریال", AutoSize = true };
    private readonly ComboBox _openCurrency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TextBox _openQuantity = new() { Width = 120 };
    private readonly TextBox _openUnitRate = new() { Width = 120 };
    private readonly TextBox _openDate = new() { Width = 120, PlaceholderText = "امروز" };
    private readonly Button _saveOpeningForeign = new() { Text = "ثبت موجودی اولیه ارز", AutoSize = true };

    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<CashBoxInfo> _boxes = Array.Empty<CashBoxInfo>();
    private IReadOnlyList<BranchInfo> _branches = Array.Empty<BranchInfo>();
    private IReadOnlyList<BranchInfo> _readableBranches = Array.Empty<BranchInfo>();
    private IReadOnlyDictionary<string, int> _decimals = new Dictionary<string, int>();

    public CashTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var filters = UiHelpers.CreateInputPanel();
        filters.Controls.AddRange(new Control[] { UiHelpers.MakeLabel("شعبه:"), _filterBranch, _show, _excel });

        _openingPanel.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("شعبه:"), _openBranch,
            UiHelpers.MakeLabel("موجودی اولیه ریال (ریال):"), _openIrr,
            _saveOpeningIrr,
            UiHelpers.MakeLabel("ارز:"), _openCurrency,
            UiHelpers.MakeLabel("مقدار:"), _openQuantity,
            UiHelpers.MakeLabel("نرخ هر واحد (ریال):"), _openUnitRate,
            _saveOpeningForeign,
        });
        _openingPanel.Visible = false; // بر اساس وظیفه‌ی «ثبت موجودی افتتاحیه» در RefreshAsync تنظیم می‌شود.

        _openingPanel.Controls.Add(UiHelpers.MakeLabel("تاریخ (شمسی، اختیاری):"));
        _openingPanel.Controls.Add(_openDate);
        Controls.Add(_grid);
        Controls.Add(_openingPanel);
        Controls.Add(filters);

        _show.Click += async (_, _) => await SafeRefreshAsync();
        _excel.Click += (_, _) => ExportExcel();
        _saveOpeningIrr.Click += async (_, _) => await SaveOpeningIrrAsync();
        _saveOpeningForeign.Click += async (_, _) => await SaveOpeningForeignAsync();
    }

    public async Task RefreshAsync()
    {
        var currencies = await _services.Admin.GetCurrenciesAsync();
        _decimals = currencies.ToDictionary(c => c.Code, c => c.DecimalPlaces);
        _branches = await _services.Permissions.GetBranchesAsync(_user, Permission.OpeningCreate);
        _readableBranches = await _services.Permissions.GetBranchesAsync(_user);
        _openingPanel.Visible = _branches.Count > 0;
        _boxes = await _services.Admin.GetCashBoxesAsync(_user, UiHelpers.SelectedBranchId(_filterBranch));

        var previousFilter = (_filterBranch.SelectedItem as ComboItem)?.Value;
        UiHelpers.FillBranches(_filterBranch, _readableBranches, _user, includeAll: true, selectedValue: previousFilter);

        if (_branches.Count > 0)
        {
            var previousOpen = (_openBranch.SelectedItem as ComboItem)?.Value;
            UiHelpers.FillBranches(_openBranch, _branches, _user, includeAll: false, selectedValue: previousOpen);

            var previousCurrency = (_openCurrency.SelectedItem as ComboItem)?.Value;
            _openCurrency.Items.Clear();
            foreach (var c in currencies.Where(x => x.IsActive && x.Code != CurrencyCodes.Irr))
            {
                _openCurrency.Items.Add(new ComboItem(c.Code, $"{c.Code} - {c.Name}"));
            }
            UiHelpers.SelectByValue(_openCurrency, previousCurrency);
        }

        var rows = _boxes.Select(b => new[]
        {
            b.BranchName,
            b.Name,
            b.CurrencyCode,
            MoneyMath.FormatAmount(b.Balance, _decimals.GetValueOrDefault(b.CurrencyCode, 0)),
            PersianDate.FormatDateTime(b.UpdatedAt),
        });
        UiHelpers.Fill(_grid, new[] { "شعبه", "نام صندوق", "ارز", "موجودی", "آخرین تغییر (شمسی)" }, rows);
    }

    /// <summary>تاریخ شمسی موجودی افتتاحیه. خالی یعنی امروز؛ تاریخ گذشته تا ۳۰ روز قبل مجاز است.</summary>
    private DateTime? ParseOpeningDate()
    {
        if (string.IsNullOrWhiteSpace(_openDate.Text))
        {
            return null;
        }
        if (!PersianDate.TryParseDate(_openDate.Text, out var date))
        {
            throw new BusinessRuleException("تاریخ افتتاحیه را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
        }
        return date;
    }

    private async Task SaveOpeningIrrAsync()
    {
        try
        {
            var branchId = UiHelpers.RequiredBranchId(_openBranch);
            if (!InputParser.TryParseDecimal(_openIrr.Text, out var amount))
            {
                throw new BusinessRuleException("مبلغ ریال را به‌درستی وارد کنید.");
            }

            var occurredOn = ParseOpeningDate();
            await _services.Admin.RecordOpeningAsync(_user, branchId, CurrencyCodes.Irr, amount, null, DateTime.Now, occurredOn);
            _openIrr.Clear();
            _openDate.Clear();
            UiHelpers.ShowInfo(this, "موجودی اولیه‌ی ریال ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task SaveOpeningForeignAsync()
    {
        try
        {
            var branchId = UiHelpers.RequiredBranchId(_openBranch);
            if (_openCurrency.SelectedItem is not ComboItem item)
            {
                throw new BusinessRuleException("ارز را انتخاب کنید.");
            }
            if (!InputParser.TryParseDecimal(_openQuantity.Text, out var quantity))
            {
                throw new BusinessRuleException("مقدار ارز را به‌درستی وارد کنید.");
            }
            if (!InputParser.TryParseDecimal(_openUnitRate.Text, out var unitRate))
            {
                throw new BusinessRuleException("نرخ هر واحد را به‌درستی وارد کنید.");
            }

            var occurredOn = ParseOpeningDate();
            await _services.Admin.RecordOpeningAsync(_user, branchId, item.Value, quantity, unitRate, DateTime.Now, occurredOn);
            _openDate.Clear();
            _openQuantity.Clear();
            _openUnitRate.Clear();
            UiHelpers.ShowInfo(this, "موجودی اولیه‌ی ارز ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private void ExportExcel()
    {
        try
        {
            var bytes = WorkbookBuilder.CashBoxes(_boxes);
            UiHelpers.SaveExcel(this, bytes, $"cash-boxes-{PersianDate.FormatDate(DateTime.Now).Replace('/', '-')}.xlsx");
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
