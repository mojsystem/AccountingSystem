using System.Globalization;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// فرم سند حسابداری دستی. هر سطر به شکل «کد حساب، بدهکار، بستانکار» نوشته می‌شود؛ مثلاً: 6001,15000000,0
/// </summary>
internal sealed class ManualEntryDialog : Form
{
    private readonly TextBox _description = new() { Width = 480, MaxLength = 250 };
    private readonly TextBox _date = new() { Width = 160, PlaceholderText = "امروز" };
    private readonly TextBox _lines = new()
    {
        Multiline = true,
        Width = 520,
        Height = 190,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
    };

    public ManualEntryDialog(IReadOnlyList<AccountInfo> accounts, string title, string description, DateTime? occurredAt, IReadOnlyList<JournalLineInfo> lines)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 480);

        _description.Text = description;
        _date.Text = occurredAt is { } day ? PersianDate.FormatDate(day) : string.Empty;
        _lines.Text = string.Join(Environment.NewLine, lines.Select(l =>
            $"{l.AccountCode},{l.Debit.ToString("0", CultureInfo.InvariantCulture)},{l.Credit.ToString("0", CultureInfo.InvariantCulture)}"));

        var allowed = accounts
            .Where(a => a.IsActive && !a.Code.StartsWith("1101-", StringComparison.Ordinal))
            .Select(a => $"{a.Code} {a.Name}");

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8),
        };
        body.Controls.Add(UiHelpers.MakeLabel("شرح سند:"));
        body.Controls.Add(_description);
        body.Controls.Add(UiHelpers.MakeLabel("تاریخ (شمسی، خالی یعنی امروز؛ تا ۳۰ روز قبل):"));
        body.Controls.Add(_date);
        body.Controls.Add(UiHelpers.MakeLabel("سطرها (هر سطر: کد حساب، بدهکار، بستانکار):"));
        body.Controls.Add(_lines);
        body.Controls.Add(new Label
        {
            Text = "حساب‌های مجاز: " + string.Join("، ", allowed),
            AutoSize = true,
            MaximumSize = new Size(520, 0),
        });

        var ok = new Button { Text = "ثبت", AutoSize = true };
        var cancel = new Button { Text = "انصراف", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            try
            {
                Description = _description.Text.Trim();
                OccurredOn = ParseDate(_date.Text);
                Lines = ParseLines(_lines.Lines);
                DialogResult = DialogResult.OK;
            }
            catch (BusinessRuleException ex)
            {
                UiHelpers.ShowInfo(this, ex.Message);
            }
        };
    }

    public string Description { get; private set; } = string.Empty;

    public DateTime? OccurredOn { get; private set; }

    public IReadOnlyList<JournalLineDraft> Lines { get; private set; } = Array.Empty<JournalLineDraft>();

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (!PersianDate.TryParseDate(text, out var date))
        {
            throw new BusinessRuleException("تاریخ سند را به‌درستی وارد کنید (مثلاً ۱۴۰۵/۰۷/۱۵).");
        }
        return date;
    }

    private static IReadOnlyList<JournalLineDraft> ParseLines(IEnumerable<string> rows)
    {
        var result = new List<JournalLineDraft>();
        foreach (var raw in rows)
        {
            var text = raw.Trim();
            if (text.Length == 0)
            {
                continue;
            }
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || parts[0].Length == 0)
            {
                throw new BusinessRuleException($"سطر «{text}» باید سه بخش داشته باشد: کد حساب، بدهکار، بستانکار.");
            }
            if (!InputParser.TryParseDecimal(parts[1].Length == 0 ? "0" : parts[1], out var debit)
                || !InputParser.TryParseDecimal(parts[2].Length == 0 ? "0" : parts[2], out var credit))
            {
                throw new BusinessRuleException($"مبلغ‌های سطر «{text}» را به‌درستی وارد کنید (عدد ریال).");
            }
            result.Add(new JournalLineDraft(parts[0], debit, credit));
        }
        return result;
    }
}
