using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// فرم ثبت یا ویرایش مشتری. اطلاعات پیش از بسته شدن فرم اعتبارسنجی می‌شود تا ورودی کاربر از بین نرود.
/// </summary>
internal sealed class CustomerDialog : Form
{
    private readonly TextBox _name = new() { Width = 340, MaxLength = 100 };
    private readonly TextBox _nationalCode = new() { Width = 340, MaxLength = 20 };
    private readonly TextBox _phone = new() { Width = 340, MaxLength = 20 };
    private readonly TextBox _address = new() { Width = 340, MaxLength = 250 };
    private readonly TextBox _note = new() { Width = 340, MaxLength = 250 };

    public CustomerDialog(CustomerInfo? existing = null)
    {
        Text = existing is null ? "مشتری تازه" : $"ویرایش «{existing.FullName}»";
        Font = new Font("Tahoma", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        if (existing is not null)
        {
            _name.Text = existing.FullName;
            _nationalCode.Text = existing.NationalCode ?? string.Empty;
            _phone.Text = existing.Phone ?? string.Empty;
            _address.Text = existing.Address ?? string.Empty;
            _note.Text = existing.Note ?? string.Empty;
        }

        var fields = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16, 12, 16, 4),
        };
        fields.Controls.AddRange(new Control[]
        {
            UiHelpers.MakeLabel("نام و نام خانوادگی:"), _name,
            UiHelpers.MakeLabel("کد ملی یا شناسه (اختیاری):"), _nationalCode,
            UiHelpers.MakeLabel("تلفن:"), _phone,
            UiHelpers.MakeLabel("نشانی:"), _address,
            UiHelpers.MakeLabel("یادداشت:"), _note,
        });

        var ok = new Button { Text = "ذخیره", AutoSize = true };
        var cancel = new Button { Text = "انصراف", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(16, 8, 16, 12),
        };
        buttons.Controls.AddRange(new Control[] { ok, cancel });

        Controls.Add(fields);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Accept();
    }

    /// <summary>اطلاعات تمیزشده‌ی مشتری پس از تأیید؛ null یعنی انصراف.</summary>
    public CustomerInput? Result { get; private set; }

    private void Accept()
    {
        try
        {
            Result = CustomerRules.Clean(new CustomerInput(_name.Text, _nationalCode.Text, _phone.Text, _address.Text, _note.Text));
            DialogResult = DialogResult.OK;
        }
        catch (BusinessRuleException ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }
}
