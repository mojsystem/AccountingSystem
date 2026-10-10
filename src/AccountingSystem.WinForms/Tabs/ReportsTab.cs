using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>گزارش مانده‌ی اشخاص و معین تفصیلی مشتریان.</summary>
internal sealed class ReportsTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly ComboBox _reportType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly ComboBox _customer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _asOf = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۱۵" };
    private readonly TextBox _from = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۰۱" };
    private readonly TextBox _to = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۳۰" };
    private readonly Label _asOfLabel = UiHelpers.MakeLabel("مانده تا تاریخ (شمسی):");
    private readonly Label _fromLabel = UiHelpers.MakeLabel("از تاریخ (شمسی):");
    private readonly Label _toLabel = UiHelpers.MakeLabel("تا تاریخ (شمسی):");
    private readonly Label _customerLabel = UiHelpers.MakeLabel("شخص:");
    private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly Button _excel = new() { Text = "خروجی Excel", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<CustomerBalanceReportRow> _balances = Array.Empty<CustomerBalanceReportRow>();
    private CustomerLedgerReport? _ledger;
    private bool _filling;

    public ReportsTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;
        UiHelpers.SetJalaliToday(_asOf, _from, _to);
        _reportType.Items.Add(new ComboItem("BALANCES", "مانده‌ی بدهکار و بستانکار اشخاص"));
        _reportType.Items.Add(new ComboItem("LEDGER", "معین شخص"));
        UiHelpers.SelectByValue(_reportType, "BALANCES");

        var filters = UiHelpers.CreateInputPanel();
        filters.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("نوع گزارش:"), _reportType,
            UiHelpers.MakeLabel("شعبه:"), _branch,
            _asOfLabel, _asOf,
            _customerLabel, _customer,
            _fromLabel, _from,
            _toLabel, _to,
            _show, _excel,
        });

        Controls.Add(_grid);
        Controls.Add(_summary);
        Controls.Add(filters);
        UpdateModeFields();

        _reportType.SelectedIndexChanged += (_, _) => UpdateModeFields();
        _show.Click += async (_, _) => await SafeRefreshAsync();
        _excel.Click += async (_, _) => await ExportExcelAsync();
    }

    public async Task RefreshAsync()
    {
        var branchRows = await _services.Permissions.GetBranchesAsync(_user);
        var previousBranch = (_branch.SelectedItem as ComboItem)?.Value;
        UiHelpers.FillBranches(_branch, branchRows, _user, includeAll: true, selectedValue: previousBranch);

        var customerRows = await _services.Customers.SearchAsync(_user, null, take: 5000);
        var previousCustomer = (_customer.SelectedItem as ComboItem)?.Value;
        _filling = true;
        try
        {
            _customer.Items.Clear();
            foreach (var customer in customerRows)
            {
                _customer.Items.Add(new ComboItem(
                    customer.Id.ToString(CultureInfo.InvariantCulture),
                    $"{customer.FullName} · {customer.CustomerCode}"));
            }
            UiHelpers.SelectByValue(_customer, previousCustomer);
        }
        finally
        {
            _filling = false;
        }
        UpdateModeFields();

        if (IsBalancesMode)
        {
            if (!PersianDate.TryParseDate(_asOf.Text, out var asOf))
            {
                throw new BusinessRuleException("تاریخ مانده را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
            }
            _balances = await _services.Reports.GetCustomerBalancesAsync(
                _user, UiHelpers.SelectedBranchId(_branch), asOf.Date.AddDays(1));
            _ledger = null;
            UiHelpers.Fill(_grid,
                new[] { "کد شخص", "نام شخص", "مانده بدهکار (ریال)", "مانده بستانکار (ریال)", "وضعیت" },
                _balances.Select(balance => new[]
                {
                    balance.CustomerCode,
                    balance.FullName,
                    MoneyMath.FormatAmount(balance.DebitBalanceIrr, 0),
                    MoneyMath.FormatAmount(balance.CreditBalanceIrr, 0),
                    balance.BalanceSide,
                }));
            _summary.Text = $"تعداد اشخاص: {_balances.Count}   ·   جمع بدهکار: {MoneyMath.FormatAmount(_balances.Sum(x => x.DebitBalanceIrr), 0)} ریال   ·   جمع بستانکار: {MoneyMath.FormatAmount(_balances.Sum(x => x.CreditBalanceIrr), 0)} ریال";
            return;
        }

        if (_customer.SelectedItem is not ComboItem selected
            || !int.TryParse(selected.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customerId))
        {
            _ledger = null;
            UiHelpers.Fill(_grid, new[] { "زمان", "شعبه", "شماره سند", "منشأ", "شرح", "معین", "بدهکار", "بستانکار", "مانده" }, Array.Empty<string[]>());
            _summary.Text = "برای نمایش معین، شخص را انتخاب کنید.";
            return;
        }

        var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
        _ledger = await _services.Reports.GetCustomerLedgerAsync(
            _user, UiHelpers.SelectedBranchId(_branch), customerId, from, to);
        var report = _ledger;
        var rows = new List<string[]>
        {
            new[]
            {
                string.Empty, string.Empty, string.Empty, string.Empty, "مانده‌ی ابتدای دوره", string.Empty,
                MoneyMath.FormatAmount(Math.Max(0m, report.OpeningBalanceIrr), 0),
                MoneyMath.FormatAmount(Math.Max(0m, -report.OpeningBalanceIrr), 0),
                FormatBalance(report.OpeningBalanceIrr),
            },
        };
        rows.AddRange(report.Lines.Select(line => new[]
        {
            PersianDate.FormatDateTime(line.OccurredAt),
            line.BranchName,
            line.JournalEntryId.ToString(CultureInfo.InvariantCulture),
            SourceText(line.SourceType),
            (line.IsVoided ? "[باطل‌شده] " : string.Empty) + line.Description,
            line.AccountCode,
            MoneyMath.FormatAmount(line.Debit, 0),
            MoneyMath.FormatAmount(line.Credit, 0),
            FormatBalance(line.BalanceIrr),
        }));
        UiHelpers.Fill(_grid,
            new[] { "زمان (شمسی)", "شعبه", "شماره سند", "منشأ", "شرح", "معین", "بدهکار (ریال)", "بستانکار (ریال)", "مانده" },
            rows);
        _summary.Text = $"معین {report.Customer.FullName} · {report.Customer.CustomerCode}   |   مانده‌ی پایان دوره: {FormatBalance(report.ClosingBalanceIrr)}";
    }

    private bool IsBalancesMode => _reportType.SelectedItem is ComboItem item && item.Value == "BALANCES";

    private void UpdateModeFields()
    {
        var balances = IsBalancesMode;
        _asOfLabel.Visible = balances;
        _asOf.Visible = balances;
        _customerLabel.Visible = !balances;
        _customer.Visible = !balances;
        _fromLabel.Visible = !balances;
        _from.Visible = !balances;
        _toLabel.Visible = !balances;
        _to.Visible = !balances;
        if (!_filling)
        {
            _ledger = null;
        }
    }

    private static string FormatBalance(decimal balance) =>
        $"{MoneyMath.FormatAmount(Math.Abs(balance), 0)} {(balance > 0 ? "بدهکار" : balance < 0 ? "بستانکار" : "تسویه")}";

    private static string SourceText(string sourceType) => sourceType switch
    {
        SourceTypes.Trade => "معامله",
        SourceTypes.Opening => "موجودی افتتاحیه",
        SourceTypes.Void => "ابطال",
        SourceTypes.Adjust => "تعدیل",
        SourceTypes.Manual => "سند دستی",
        SourceTypes.BankOpening => "افتتاحیه‌ی حساب بانکی",
        _ => sourceType,
    };

    private async Task ExportExcelAsync()
    {
        try
        {
            await RefreshAsync();
            if (IsBalancesMode)
            {
                UiHelpers.SaveExcel(this, WorkbookBuilder.CustomerBalances(_balances), "customer-balances.xlsx");
                return;
            }
            if (_ledger is null)
            {
                throw new BusinessRuleException("ابتدا شخص را انتخاب کنید تا معین او آماده شود.");
            }
            UiHelpers.SaveExcel(this, WorkbookBuilder.CustomerLedger(_ledger),
                $"customer-ledger-{_ledger.Customer.CustomerCode}.xlsx");
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
