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
    private readonly Button _newManual = new() { Text = "سند دستی جدید", AutoSize = true };
    private readonly Button _editManual = new() { Text = "ویرایش سند دستی", AutoSize = true };
    private readonly Button _voidManual = new() { Text = "ابطال سند دستی", AutoSize = true };
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
            _newManual, _editManual, _voidManual,
        });
        _newManual.Visible = false; // بر اساس وظیفه‌ی «ثبت سند دستی» در RefreshAsync تنظیم می‌شود.
        // ویرایش و ابطال سند دستی بر اساس دسترسی کاربر در RefreshAsync تنظیم می‌شود.
        _editManual.Visible = false;
        _voidManual.Visible = false;

        Controls.Add(_grid);
        Controls.Add(filters);

        _show.Click += async (_, _) => await SafeRefreshAsync();
        _excel.Click += (_, _) => ExportExcel();
        _newManual.Click += async (_, _) => await CreateManualAsync();
        _editManual.Click += async (_, _) => await EditManualAsync();
        _voidManual.Click += async (_, _) => await VoidManualAsync();
    }

    public async Task RefreshAsync()
    {
        _newManual.Visible = await _services.Permissions.HasAnyAsync(_user, Permission.ManualCreate);
        _editManual.Visible = await _services.Permissions.HasAnyAsync(_user, Permission.ManualEdit);
        _voidManual.Visible = await _services.Permissions.HasAnyAsync(_user, Permission.ManualVoid);

        var branches = await _services.Permissions.GetBranchesAsync(_user);
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
                    entry.IsVoided ? "[باطل شد] " + entry.Description : entry.Description,
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
        SourceTypes.Adjust => "تعدیل بهای تمام‌شده",
        SourceTypes.Manual => "سند دستی",
        SourceTypes.BankOpening => "افتتاحیه‌ی حساب بانکی",
        _ => sourceType,
    };

    /// <summary>سند انتخاب‌شده در جدول. ستون اول جدول شماره‌ی سند است.</summary>
    private JournalEntryInfo? SelectedEntry()
    {
        if (_grid.CurrentRow is null || _grid.CurrentRow.Cells.Count == 0)
        {
            return null;
        }
        if (!long.TryParse(_grid.CurrentRow.Cells[0].Value?.ToString(), out var entryId))
        {
            return null;
        }
        return _entries.FirstOrDefault(e => e.Id == entryId);
    }

    private async Task CreateManualAsync()
    {
        try
        {
            var branchId = UiHelpers.RequiredBranchId(_branch);
            var accounts = await _services.Manual.GetAccountsAsync();
            using var dialog = new ManualEntryDialog(accounts, "سند حسابداری دستی جدید", string.Empty, null, Array.Empty<JournalLineInfo>());
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            var entryId = await _services.Manual.CreateAsync(_user, branchId, dialog.Description, dialog.OccurredOn, dialog.Lines, DateTime.Now);
            UiHelpers.ShowInfo(this, $"سند دستی شماره {entryId} ثبت شد.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task EditManualAsync()
    {
        try
        {
            var selected = SelectedEntry() ?? throw new BusinessRuleException("یک سند دستی را از جدول انتخاب کنید.");
            var entry = await _services.Manual.GetAsync(_user, selected.Id) ?? throw new BusinessRuleException("سند انتخابی یافت نشد.");
            if (entry.SourceType != SourceTypes.Manual || entry.IsVoided)
            {
                throw new BusinessRuleException("فقط سند دستی فعال را می‌توان ویرایش کرد.");
            }
            var accounts = await _services.Manual.GetAccountsAsync();
            using var dialog = new ManualEntryDialog(accounts, $"ویرایش سند دستی شماره {entry.Id}", entry.Description, entry.OccurredAt, entry.Lines);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            var newId = await _services.Manual.EditAsync(_user, entry.Id, dialog.Description, dialog.OccurredOn, dialog.Lines, DateTime.Now);
            UiHelpers.ShowInfo(this, $"سند دستی ویرایش شد؛ نسخه‌ی اصلاحی شماره {newId} ثبت گردید.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private async Task VoidManualAsync()
    {
        try
        {
            var entry = SelectedEntry() ?? throw new BusinessRuleException("یک سند دستی را از جدول انتخاب کنید.");
            if (entry.SourceType != SourceTypes.Manual || entry.IsVoided)
            {
                throw new BusinessRuleException("فقط سند دستی فعال را می‌توان باطل کرد.");
            }
            var reason = UiHelpers.PromptText(this, "ابطال سند دستی", $"دلیل ابطال سند دستی شماره {entry.Id} را وارد کنید:");
            if (string.IsNullOrWhiteSpace(reason))
            {
                return;
            }
            await _services.Manual.VoidAsync(_user, entry.Id, reason, DateTime.Now);
            UiHelpers.ShowInfo(this, "سند دستی باطل شد و سند ابطال ثبت گردید.");
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
