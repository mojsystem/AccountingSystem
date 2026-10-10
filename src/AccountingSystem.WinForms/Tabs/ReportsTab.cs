using System.Globalization;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>گزارش مانده و معین اشخاص، سرفصل‌ها، حساب‌های بانکی و صندوق‌ها.</summary>
internal sealed class ReportsTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly ComboBox _reportType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly ComboBox _dateMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 165 };
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly ComboBox _customer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
    private readonly ComboBox _cashBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly ComboBox _bankAccount = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly CheckBox _includeDescendants = new() { Text = "تجمیع گردش و مانده‌ی زیرحساب‌ها", AutoSize = true, Checked = true, Margin = new Padding(8, 8, 8, 0) };
    private readonly TextBox _asOf = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۱۵" };
    private readonly TextBox _from = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۰۱" };
    private readonly TextBox _to = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۳۰" };
    private readonly Label _dateModeLabel = UiHelpers.MakeLabel("شکل گزارش:");
    private readonly Label _asOfLabel = UiHelpers.MakeLabel("مانده تا تاریخ (شمسی):");
    private readonly Label _fromLabel = UiHelpers.MakeLabel("از تاریخ (شمسی):");
    private readonly Label _toLabel = UiHelpers.MakeLabel("تا تاریخ (شمسی):");
    private readonly Label _customerLabel = UiHelpers.MakeLabel("شخص:");
    private readonly Label _accountLabel = UiHelpers.MakeLabel("سرفصل:");
    private readonly Label _cashBoxLabel = UiHelpers.MakeLabel("صندوق:");
    private readonly Label _bankAccountLabel = UiHelpers.MakeLabel("حساب بانکی:");
    private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly Button _excel = new() { Text = "خروجی Excel", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();

    private IReadOnlyList<CustomerBalanceReportRow> _balances = Array.Empty<CustomerBalanceReportRow>();
    private CustomerLedgerReport? _customerLedger;
    private AccountBalanceReport? _accountBalance;
    private AccountLedgerReport? _accountLedger;
    private IReadOnlyList<CashBoxLedgerReport> _cashBoxLedgers = Array.Empty<CashBoxLedgerReport>();
    private IReadOnlyList<BankAccountLedgerReport> _bankLedgers = Array.Empty<BankAccountLedgerReport>();
    private bool _filling;

    public ReportsTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;
        UiHelpers.SetJalaliToday(_asOf, _from, _to);
        _reportType.Items.Add(new ComboItem("BALANCES", "مانده اشخاص"));
        _reportType.Items.Add(new ComboItem("LEDGER", "معین شخص"));
        _reportType.Items.Add(new ComboItem("ACCOUNT", "گزارش سرفصل حساب"));
        _reportType.Items.Add(new ComboItem("CASHBOX", "معین صندوق‌ها"));
        _reportType.Items.Add(new ComboItem("BANK", "معین حساب‌های بانکی"));
        UiHelpers.SelectByValue(_reportType, "BALANCES");
        _dateMode.Items.Add(new ComboItem("ASOF", "مانده تا تاریخ"));
        _dateMode.Items.Add(new ComboItem("RANGE", "گردش از/تا"));
        UiHelpers.SelectByValue(_dateMode, "ASOF");

        var filters = UiHelpers.CreateInputPanel();
        filters.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("نوع گزارش:"), _reportType,
            UiHelpers.MakeLabel("شعبه:"), _branch,
            _customerLabel, _customer,
            _accountLabel, _account,
            _includeDescendants,
            _cashBoxLabel, _cashBox,
            _bankAccountLabel, _bankAccount,
            _dateModeLabel, _dateMode,
            _asOfLabel, _asOf,
            _fromLabel, _from,
            _toLabel, _to,
            _show, _excel,
        });

        Controls.Add(_grid);
        Controls.Add(_summary);
        Controls.Add(filters);
        UpdateModeFields();

        _reportType.SelectedIndexChanged += (_, _) => UpdateModeFields();
        _dateMode.SelectedIndexChanged += (_, _) => UpdateModeFields();
        _show.Click += async (_, _) => await SafeRefreshAsync();
        _excel.Click += async (_, _) => await ExportExcelAsync();
    }

    public async Task RefreshAsync()
    {
        var branchRows = await _services.Permissions.GetBranchesAsync(_user);
        var previousBranch = (_branch.SelectedItem as ComboItem)?.Value;
        UiHelpers.FillBranches(_branch, branchRows, _user, includeAll: true, selectedValue: previousBranch);
        var branchId = UiHelpers.SelectedBranchId(_branch);

        var customerRows = await _services.Customers.SearchAsync(_user, null, take: 5000);
        var previousCustomer = (_customer.SelectedItem as ComboItem)?.Value;
        FillCustomers(customerRows, previousCustomer);

        var accountRows = await _services.Reports.GetAccountReportOptionsAsync(_user);
        var previousAccount = (_account.SelectedItem as ComboItem)?.Value;
        FillAccounts(accountRows, previousAccount);

        var boxRows = await _services.Reports.GetCashBoxReportOptionsAsync(_user, branchId);
        var previousBox = (_cashBox.SelectedItem as ComboItem)?.Value;
        FillCashBoxes(boxRows, previousBox);

        var bankRows = await _services.Reports.GetBankAccountReportOptionsAsync(_user, branchId);
        var previousBank = (_bankAccount.SelectedItem as ComboItem)?.Value;
        FillBankAccounts(bankRows, previousBank);

        UpdateModeFields();
        _accountBalance = null;
        _accountLedger = null;
        _cashBoxLedgers = Array.Empty<CashBoxLedgerReport>();
        _bankLedgers = Array.Empty<BankAccountLedgerReport>();

        if (IsBalancesMode)
        {
            if (!PersianDate.TryParseDate(_asOf.Text, out var asOf))
            {
                throw new BusinessRuleException("تاریخ مانده را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
            }
            _balances = await _services.Reports.GetCustomerBalancesAsync(
                _user, branchId, asOf.Date.AddDays(1));
            _customerLedger = null;
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

        if (IsCustomerLedgerMode)
        {
            if (_customer.SelectedItem is not ComboItem customerItem
                || !int.TryParse(customerItem.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customerId))
            {
                _customerLedger = null;
                UiHelpers.Fill(_grid, CustomerLedgerHeaders, Array.Empty<string[]>());
                _summary.Text = "برای نمایش معین، شخص را انتخاب کنید.";
                return;
            }

            var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
            _customerLedger = await _services.Reports.GetCustomerLedgerAsync(
                _user, branchId, customerId, from, to);
            FillCustomerLedger(_customerLedger);
            return;
        }

        if (IsAccountMode)
        {
            if (_account.SelectedItem is not ComboItem accountItem || string.IsNullOrWhiteSpace(accountItem.Value))
            {
                UiHelpers.Fill(_grid, AccountLedgerHeaders, Array.Empty<string[]>());
                _summary.Text = "برای نمایش گزارش، سرفصل را انتخاب کنید.";
                return;
            }

            if (IsDateRangeMode)
            {
                var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
                _accountLedger = await _services.Reports.GetAccountLedgerReportAsync(
                    _user, branchId, accountItem.Value, _includeDescendants.Checked, from, to);
                FillAccountLedger(_accountLedger);
            }
            else
            {
                if (!PersianDate.TryParseDate(_asOf.Text, out var asOf))
                {
                    throw new BusinessRuleException("تاریخ مانده را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
                }
                _accountBalance = await _services.Reports.GetAccountBalanceReportAsync(
                    _user, branchId, accountItem.Value, _includeDescendants.Checked, asOf);
                FillAccountBalance(_accountBalance);
            }
            return;
        }

        DateTime? fromInclusive;
        DateTime toExclusive;
        if (IsDateRangeMode)
        {
            var range = UiHelpers.ParseJalaliRange(_from, _to);
            fromInclusive = range.From;
            toExclusive = range.To;
        }
        else
        {
            if (!PersianDate.TryParseDate(_asOf.Text, out var asOf))
            {
                throw new BusinessRuleException("تاریخ مانده را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
            }
            fromInclusive = null;
            toExclusive = asOf.Date.AddDays(1);
        }

        if (IsCashBoxMode)
        {
            _cashBoxLedgers = await _services.Reports.GetCashBoxLedgerReportsAsync(
                _user, branchId, SelectedOptionalId(_cashBox), fromInclusive, toExclusive);
            FillCashBoxLedgers(_cashBoxLedgers);
        }
        else
        {
            _bankLedgers = await _services.Reports.GetBankAccountLedgerReportsAsync(
                _user, branchId, SelectedOptionalId(_bankAccount), fromInclusive, toExclusive);
            FillBankLedgers(_bankLedgers);
        }
    }

    private bool IsBalancesMode => SelectedReportType == "BALANCES";

    private bool IsCustomerLedgerMode => SelectedReportType == "LEDGER";

    private bool IsAccountMode => SelectedReportType == "ACCOUNT";

    private bool IsCashBoxMode => SelectedReportType == "CASHBOX";

    private bool IsBankMode => SelectedReportType == "BANK";

    private bool IsDateRangeMode => (_dateMode.SelectedItem as ComboItem)?.Value == "RANGE";

    private string SelectedReportType => (_reportType.SelectedItem as ComboItem)?.Value ?? "BALANCES";

    private void FillCustomers(IReadOnlyList<CustomerInfo> customers, string? selected)
    {
        _filling = true;
        try
        {
            _customer.Items.Clear();
            foreach (var customer in customers)
            {
                _customer.Items.Add(new ComboItem(
                    customer.Id.ToString(CultureInfo.InvariantCulture),
                    $"{customer.FullName} · {customer.CustomerCode}"));
            }
            UiHelpers.SelectByValue(_customer, selected);
        }
        finally
        {
            _filling = false;
        }
    }

    private void FillAccounts(IReadOnlyList<AccountInfo> accounts, string? selected)
    {
        _account.Items.Clear();
        _account.Items.Add(new ComboItem(string.Empty, "— انتخاب سرفصل —"));
        foreach (var account in accounts)
        {
            var indent = new string('　', Math.Max(0, account.Level - 1));
            _account.Items.Add(new ComboItem(account.Code, $"{indent}{account.Code} · {account.Name} (سطح {account.Level})"));
        }
        UiHelpers.SelectByValue(_account, selected);
    }

    private void FillCashBoxes(IReadOnlyList<CashBoxInfo> boxes, string? selected)
    {
        _cashBox.Items.Clear();
        _cashBox.Items.Add(new ComboItem(string.Empty, "همه‌ی صندوق‌ها"));
        foreach (var box in boxes)
        {
            _cashBox.Items.Add(new ComboItem(box.Id.ToString(CultureInfo.InvariantCulture),
                $"{box.BranchName} · {box.Name} · {box.CurrencyCode}"));
        }
        UiHelpers.SelectByValue(_cashBox, selected);
    }

    private void FillBankAccounts(IReadOnlyList<BankAccountInfo> accounts, string? selected)
    {
        _bankAccount.Items.Clear();
        _bankAccount.Items.Add(new ComboItem(string.Empty, "همه‌ی حساب‌های بانکی"));
        foreach (var account in accounts)
        {
            _bankAccount.Items.Add(new ComboItem(account.Id.ToString(CultureInfo.InvariantCulture),
                $"{account.BranchName} · {account.Name} · {account.CurrencyCode}"));
        }
        UiHelpers.SelectByValue(_bankAccount, selected);
    }

    private void UpdateModeFields()
    {
        var balances = IsBalancesMode;
        var customerLedger = IsCustomerLedgerMode;
        var account = IsAccountMode;
        var cashBox = IsCashBoxMode;
        var bank = IsBankMode;
        var supportsDateMode = account || cashBox || bank;
        var range = customerLedger || (supportsDateMode && IsDateRangeMode);

        _customerLabel.Visible = customerLedger;
        _customer.Visible = customerLedger;
        _accountLabel.Visible = account;
        _account.Visible = account;
        _includeDescendants.Visible = account;
        _cashBoxLabel.Visible = cashBox;
        _cashBox.Visible = cashBox;
        _bankAccountLabel.Visible = bank;
        _bankAccount.Visible = bank;
        _dateModeLabel.Visible = supportsDateMode;
        _dateMode.Visible = supportsDateMode;
        _asOfLabel.Visible = balances || (supportsDateMode && !IsDateRangeMode);
        _asOf.Visible = _asOfLabel.Visible;
        _fromLabel.Visible = range;
        _from.Visible = range;
        _toLabel.Visible = range;
        _to.Visible = range;

        if (!_filling)
        {
            _customerLedger = null;
            _accountBalance = null;
            _accountLedger = null;
            _cashBoxLedgers = Array.Empty<CashBoxLedgerReport>();
            _bankLedgers = Array.Empty<BankAccountLedgerReport>();
        }
    }

    private static int? SelectedOptionalId(ComboBox box) =>
        box.SelectedItem is ComboItem item
        && int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    private static readonly string[] CustomerLedgerHeaders =
    {
        "زمان (شمسی)", "شعبه", "شماره سند", "منشأ", "شرح", "معین", "بدهکار (ریال)", "بستانکار (ریال)", "مانده",
    };

    private static readonly string[] AccountLedgerHeaders =
    {
        "زمان (شمسی)", "شعبه", "شماره سند", "منشأ", "شرح", "کد حساب", "نام حساب", "بدهکار (ریال)", "بستانکار (ریال)", "مانده",
    };

    private static readonly string[] OperationalHeaders =
    {
        "شعبه", "صندوق / حساب بانکی", "ارز", "زمان (شمسی)", "شناسه مرجع", "منشأ", "شرح", "واریز", "برداشت", "مانده",
    };

    private void FillCustomerLedger(CustomerLedgerReport report)
    {
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
        UiHelpers.Fill(_grid, CustomerLedgerHeaders, rows);
        _summary.Text = $"معین {report.Customer.FullName} · {report.Customer.CustomerCode}   |   مانده‌ی پایان دوره: {FormatBalance(report.ClosingBalanceIrr)}";
    }

    private void FillAccountBalance(AccountBalanceReport report)
    {
        UiHelpers.Fill(_grid,
            new[] { "کد سرفصل", "نام سرفصل", "سطح", "دامنه گزارش", "تاریخ (شمسی)", "مانده بدهکار (ریال)", "مانده بستانکار (ریال)", "مانده خالص" },
            new[]
            {
                new[]
                {
                    report.Account.Code,
                    report.Account.Name,
                    report.Account.Level.ToString(CultureInfo.InvariantCulture),
                    report.IncludesChildren && report.Account.HasChildren ? "با زیرحساب‌ها" : "مستقیم",
                    PersianDate.FormatDate(report.AsOf),
                    MoneyMath.FormatAmount(Math.Max(0m, report.BalanceIrr), 0),
                    MoneyMath.FormatAmount(Math.Max(0m, -report.BalanceIrr), 0),
                    FormatBalance(report.BalanceIrr),
                },
            });
        _summary.Text = $"مانده‌ی {report.Account.Code} · {report.Account.Name} تا {PersianDate.FormatDate(report.AsOf)}: {FormatBalance(report.BalanceIrr)}";
    }

    private void FillAccountLedger(AccountLedgerReport report)
    {
        var rows = new List<string[]>
        {
            new[]
            {
                string.Empty, string.Empty, string.Empty, string.Empty, "مانده‌ی ابتدای دوره", report.Account.Code, report.Account.Name,
                MoneyMath.FormatAmount(Math.Max(0m, report.OpeningBalanceIrr), 0),
                MoneyMath.FormatAmount(Math.Max(0m, -report.OpeningBalanceIrr), 0), FormatBalance(report.OpeningBalanceIrr),
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
            line.AccountName,
            MoneyMath.FormatAmount(line.Debit, 0),
            MoneyMath.FormatAmount(line.Credit, 0),
            FormatBalance(line.BalanceIrr),
        }));
        rows.Add(new[]
        {
            string.Empty, string.Empty, string.Empty, string.Empty, "مانده‌ی پایان دوره", report.Account.Code, report.Account.Name,
            MoneyMath.FormatAmount(Math.Max(0m, report.ClosingBalanceIrr), 0),
            MoneyMath.FormatAmount(Math.Max(0m, -report.ClosingBalanceIrr), 0), FormatBalance(report.ClosingBalanceIrr),
        });
        UiHelpers.Fill(_grid, AccountLedgerHeaders, rows);
        _summary.Text = $"معین {report.Account.Code} · {report.Account.Name} ({(report.IncludesChildren && report.Account.HasChildren ? "با تجمیع زیرحساب‌ها" : "فقط ثبت مستقیم")})   |   مانده‌ی پایان دوره: {FormatBalance(report.ClosingBalanceIrr)}";
    }

    private void FillCashBoxLedgers(IReadOnlyList<CashBoxLedgerReport> reports)
    {
        var rows = new List<string[]>();
        foreach (var report in reports)
        {
            AddOperationalLedgerRows(rows, report.CashBox.BranchName, report.CashBox.Name, report.CashBox.CurrencyCode,
                report.CashBox.CurrencyCode == CurrencyCodes.Irr ? 0 : report.DecimalPlaces,
                report.FromInclusive, report.ToExclusive, report.OpeningBalance, report.ClosingBalance, report.Lines);
        }
        UiHelpers.Fill(_grid, OperationalHeaders, rows);
        _summary.Text = reports.Count == 0
            ? "صندوقی برای این فیلتر پیدا نشد."
            : $"تعداد صندوق‌ها: {reports.Count}   ·   گردش ریزشده به تفکیک صندوق و ارز";
    }

    private void FillBankLedgers(IReadOnlyList<BankAccountLedgerReport> reports)
    {
        var rows = new List<string[]>();
        foreach (var report in reports)
        {
            AddOperationalLedgerRows(rows, report.BankAccount.BranchName, report.BankAccount.Name, report.BankAccount.CurrencyCode,
                report.BankAccount.DecimalPlaces, report.FromInclusive, report.ToExclusive,
                report.OpeningBalance, report.ClosingBalance, report.Lines);
        }
        UiHelpers.Fill(_grid, OperationalHeaders, rows);
        _summary.Text = reports.Count == 0
            ? "حساب بانکی برای این فیلتر پیدا نشد."
            : $"تعداد حساب‌های بانکی: {reports.Count}   ·   گردش ریزشده به تفکیک حساب و ارز";
    }

    private static void AddOperationalLedgerRows(
        List<string[]> rows,
        string branchName,
        string accountName,
        string currencyCode,
        int decimals,
        DateTime? fromInclusive,
        DateTime toExclusive,
        decimal openingBalance,
        decimal closingBalance,
        IReadOnlyList<OperationalLedgerLineInfo> lines)
    {
        if (fromInclusive is null)
        {
            rows.Add(new[]
            {
                branchName, accountName, currencyCode, PersianDate.FormatDate(toExclusive.Date.AddDays(-1)), string.Empty,
                "مانده تا تاریخ", string.Empty, "—", "—", MoneyMath.FormatAmount(closingBalance, decimals),
            });
            return;
        }

        rows.Add(new[]
        {
            branchName, accountName, currencyCode, string.Empty, string.Empty, "مانده‌ی ابتدای دوره", string.Empty,
            "—", "—", MoneyMath.FormatAmount(openingBalance, decimals),
        });
        rows.AddRange(lines.Select(line => new[]
        {
            branchName,
            accountName,
            currencyCode,
            PersianDate.FormatDateTime(line.OccurredAt),
            line.ReferenceId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            SourceText(line.SourceType),
            line.Description,
            MoneyMath.FormatAmount(Math.Max(0m, line.Amount), decimals),
            MoneyMath.FormatAmount(Math.Max(0m, -line.Amount), decimals),
            MoneyMath.FormatAmount(line.Balance, decimals),
        }));
        rows.Add(new[]
        {
            branchName, accountName, currencyCode, string.Empty, string.Empty, "مانده‌ی پایان دوره", string.Empty,
            "—", "—", MoneyMath.FormatAmount(closingBalance, decimals),
        });
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
        SourceTypes.CashTransaction => "دریافت/پرداخت",
        SourceTypes.CashReceipt => "رسید دریافت",
        SourceTypes.CashPayment => "سند پرداخت",
        SourceTypes.CashAdjustment => "تعدیل دریافت/پرداخت",
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
            if (IsCustomerLedgerMode)
            {
                if (_customerLedger is null)
                {
                    throw new BusinessRuleException("ابتدا شخص را انتخاب کنید تا معین او آماده شود.");
                }
                UiHelpers.SaveExcel(this, WorkbookBuilder.CustomerLedger(_customerLedger),
                    $"customer-ledger-{_customerLedger.Customer.CustomerCode}.xlsx");
                return;
            }
            if (IsAccountMode)
            {
                if (_accountBalance is not null)
                {
                    UiHelpers.SaveExcel(this, WorkbookBuilder.AccountBalance(_accountBalance),
                        $"account-balance-{_accountBalance.Account.Code}.xlsx");
                    return;
                }
                if (_accountLedger is not null)
                {
                    UiHelpers.SaveExcel(this, WorkbookBuilder.AccountLedger(_accountLedger),
                        $"account-ledger-{_accountLedger.Account.Code}.xlsx");
                    return;
                }
                throw new BusinessRuleException("ابتدا یک سرفصل را انتخاب کنید تا گزارش آن آماده شود.");
            }
            if (IsCashBoxMode)
            {
                UiHelpers.SaveExcel(this, WorkbookBuilder.CashBoxLedgers(_cashBoxLedgers), "cashbox-ledger.xlsx");
                return;
            }
            if (IsBankMode)
            {
                UiHelpers.SaveExcel(this, WorkbookBuilder.BankAccountLedgers(_bankLedgers), "bank-ledger.xlsx");
            }
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
