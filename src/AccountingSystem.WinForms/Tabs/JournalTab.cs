using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class JournalTab : UserControl, IRefreshable
{
    private static readonly string[] Headers =
    {
        "شماره سند", "زمان (شمسی)", "شعبه", "منبع", "شرح", "ردیف", "کد حساب", "نام حساب", "بدهکار (ریال)", "بستانکار (ریال)",
    };

    private readonly AppServices _services;
    private readonly CurrentUser _user;
    private readonly TextBox _from = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۰۱" };
    private readonly TextBox _to = new() { Width = 110, PlaceholderText = "۱۴۰۵/۰۷/۳۰" };
    private readonly ComboBox _branch = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly Button _excel = new() { Text = "خروجی Excel", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();
    private IReadOnlyList<JournalEntryInfo> _entries = Array.Empty<JournalEntryInfo>();

    public JournalTab(AppServices services, CurrentUser user)
    {
        _services = services;
        _user = user;

        var filters = UiHelpers.CreateInputPanel();
        filters.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("از تاریخ (شمسی):"), _from,
            UiHelpers.MakeLabel("تا تاریخ (شمسی):"), _to,
            UiHelpers.MakeLabel("شعبه:"), _branch,
            _show, _excel,
        });

        Controls.Add(_grid);
        Controls.Add(filters);

        _show.Click += async (_, _) => await SafeRefreshAsync();
        _excel.Click += (_, _) => ExportExcel();
    }

    public async Task RefreshAsync()
    {
        var branches = await _services.Branches.GetBranchesAsync();
        var previous = (_branch.SelectedItem as ComboItem)?.Value;
        UiHelpers.FillBranches(_branch, branches, _user, includeAll: true, selectedValue: previous);

        var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
        _entries = await _services.Reports.GetJournalAsync(_user, UiHelpers.SelectedBranchId(_branch), from, to);

        var rows = new List<string[]>();
        foreach (var entry in _entries)
        {
            foreach (var line in entry.Lines)
            {
                rows.Add(new[]
                {
                    entry.Id.ToString(),
                    PersianDate.FormatDateTime(entry.OccurredAt),
                    entry.BranchName,
                    SourceText(entry.SourceType),
                    entry.Description,
                    line.LineNo.ToString(),
                    line.AccountCode,
                    line.AccountName,
                    MoneyMath.FormatAmount(line.Debit, 0),
                    MoneyMath.FormatAmount(line.Credit, 0),
                });
            }
        }
        UiHelpers.Fill(_grid, Headers, rows);
    }

    private static string SourceText(string sourceType) => sourceType switch
    {
        SourceTypes.Trade => "معامله",
        SourceTypes.Opening => "موجودی اولیه",
        SourceTypes.Void => "ابطال",
        _ => sourceType,
    };

    private void ExportExcel()
    {
        try
        {
            var (from, to) = UiHelpers.ParseJalaliRange(_from, _to);
            var bytes = WorkbookBuilder.Journal(_entries);
            UiHelpers.SaveExcel(this, bytes, $"journal-{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}.xlsx");
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
