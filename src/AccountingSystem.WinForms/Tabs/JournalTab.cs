using AccountingSystem.Core.Common;

namespace AccountingSystem.WinForms.Tabs;

internal sealed class JournalTab : UserControl, IRefreshable
{
    private readonly AppServices _services;
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly Button _show = new() { Text = "نمایش", AutoSize = true };
    private readonly DataGridView _grid = UiHelpers.CreateGrid();

    public JournalTab(AppServices services)
    {
        _services = services;

        var inputs = UiHelpers.CreateInputPanel();
        inputs.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("از تاریخ:"), _from,
            UiHelpers.MakeLabel("تا تاریخ:"), _to,
            _show,
        });

        Controls.Add(_grid);
        Controls.Add(inputs);

        _show.Click += async (_, _) =>
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                UiHelpers.ShowError(this, ex);
            }
        };
    }

    public async Task RefreshAsync()
    {
        var from = _from.Value.Date;
        var to = _to.Value.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var entries = await _services.Reports.GetJournalAsync(from, to.AddDays(1));
        var rows = entries.SelectMany(e => e.Lines.Select(l => new[]
        {
            e.Id.ToString(),
            PersianDate.FormatDate(e.OccurredAt),
            e.Description,
            l.AccountCode,
            l.AccountName,
            MoneyMath.FormatAmount(l.Debit, 0),
            MoneyMath.FormatAmount(l.Credit, 0),
        }));

        UiHelpers.Fill(_grid, new[] { "شماره سند", "تاریخ (شمسی)", "شرح", "کد حساب", "نام حساب", "بدهکار (ریال)", "بستانکار (ریال)" }, rows);
    }
}
