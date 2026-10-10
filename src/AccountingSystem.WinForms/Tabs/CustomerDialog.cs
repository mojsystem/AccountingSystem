using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// فرم ثبت یا ویرایش مشتری. کد مشتری را سیستم می‌سازد و فقط نمایش داده می‌شود.
/// اطلاعات پیش از بسته شدن فرم اعتبارسنجی می‌شود تا ورودی کاربر از بین نرود.
/// </summary>
internal sealed class CustomerDialog : Form
{
    private readonly TextBox _name = new() { MaxLength = 100 };
    private readonly TextBox _nationalCode = new() { MaxLength = 20 };
    private readonly TextBox _mobile = new() { MaxLength = 20 };
    private readonly TextBox _phone = new() { MaxLength = 20 };
    private readonly TextBox _city = new() { MaxLength = 60 };
    private readonly TextBox _address = new() { MaxLength = 250, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _sheba1 = new() { MaxLength = 40 };
    private readonly TextBox _sheba2 = new() { MaxLength = 40 };
    private readonly TextBox _card1 = new() { MaxLength = 19 };
    private readonly TextBox _card2 = new() { MaxLength = 19 };
    private readonly TextBox _note = new() { MaxLength = 250, Multiline = true, ScrollBars = ScrollBars.Vertical };

    public CustomerDialog(CustomerInfo? existing = null)
    {
        Text = existing is null ? "مشتری تازه" : $"ویرایش «{existing.FullName}»";
        Font = Theme.BodyFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 620);
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;

        if (existing is not null)
        {
            _name.Text = existing.FullName;
            _nationalCode.Text = existing.NationalCode ?? string.Empty;
            _mobile.Text = existing.Mobile ?? string.Empty;
            _phone.Text = existing.Phone ?? string.Empty;
            _city.Text = existing.City ?? string.Empty;
            _address.Text = existing.Address ?? string.Empty;
            _sheba1.Text = existing.Sheba1 ?? string.Empty;
            _sheba2.Text = existing.Sheba2 ?? string.Empty;
            _card1.Text = existing.CardNumber1 ?? string.Empty;
            _card2.Text = existing.CardNumber2 ?? string.Empty;
            _note.Text = existing.Note ?? string.Empty;
        }

        var code = new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.BoldFont,
            TextAlign = ContentAlignment.MiddleRight,
            Text = existing is null
                ? "کد مشتری: پس از ثبت، به‌صورت خودکار ساخته می‌شود"
                : $"کد مشتری: {existing.CustomerCode}  (غیرقابل تغییر)",
            ForeColor = existing is null ? Theme.MutedText : Theme.Text,
        };

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            RightToLeft = RightToLeft.Yes,
            Margin = Padding.Empty,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 19));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 21));
        AddField(fields, 0, 0, "نام و نام خانوادگی:", _name);
        AddField(fields, 0, 1, "کد ملی یا شناسه (اختیاری، یکتا):", _nationalCode);
        AddField(fields, 1, 0, "شماره‌ی موبایل:", _mobile);
        AddField(fields, 1, 1, "تلفن ثابت:", _phone);
        AddField(fields, 2, 0, "شهر:", _city);
        AddField(fields, 2, 1, "نشانی:", _address);
        AddField(fields, 3, 0, "شماره‌ی شبا ۱ (IR و ۲۴ رقم، اختیاری):", _sheba1);
        AddField(fields, 3, 1, "شماره‌ی شبا ۲ (اختیاری):", _sheba2);
        AddField(fields, 4, 0, "شماره‌ی کارت ۱ (۱۶ رقم، اختیاری):", _card1);
        AddField(fields, 4, 1, "شماره‌ی کارت ۲ (۱۶ رقم، اختیاری):", _card2);
        var noteField = CreateField("یادداشت:", _note);
        fields.Controls.Add(noteField, 0, 5);
        fields.SetColumnSpan(noteField, 2);

        var ok = new Button { Text = "ذخیره", AutoSize = true };
        var cancel = new Button { Text = "انصراف", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            Margin = Padding.Empty,
        };
        buttons.Controls.AddRange(new Control[] { ok, cancel });

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16, 12, 16, 12),
            RightToLeft = RightToLeft.Yes,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(code, 0, 0);
        layout.Controls.Add(fields, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Accept();
        Theme.Apply(this);
    }

    /// <summary>اطلاعات تمیزشده‌ی مشتری پس از تأیید؛ null یعنی انصراف.</summary>
    public CustomerInput? Result { get; private set; }

    private static void AddField(TableLayoutPanel fields, int row, int column, string caption, TextBox input) =>
        fields.Controls.Add(CreateField(caption, input), column, row);

    private static Control CreateField(string caption, TextBox input)
    {
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 0, 0);
        input.RightToLeft = RightToLeft.Yes;
        input.TextAlign = HorizontalAlignment.Right;

        var label = new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight,
            RightToLeft = RightToLeft.Yes,
            Margin = Padding.Empty,
        };
        var field = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(3),
            Margin = new Padding(6, 4, 6, 4),
            RightToLeft = RightToLeft.Yes,
        };
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        return field;
    }

    private void Accept()
    {
        try
        {
            Result = CustomerRules.Clean(new CustomerInput(
                _name.Text,
                _nationalCode.Text,
                _phone.Text,
                _address.Text,
                _note.Text,
                _mobile.Text,
                _city.Text,
                _sheba1.Text,
                _sheba2.Text,
                _card1.Text,
                _card2.Text));
            DialogResult = DialogResult.OK;
        }
        catch (BusinessRuleException ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
