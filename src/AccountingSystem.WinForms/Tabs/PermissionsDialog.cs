using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms.Tabs;

/// <summary>
/// انتخاب دسترسی‌های ویرایش و ابطال برای یک کاربر صندوق. مدیر همه‌ی دسترسی‌ها را دارد و برای او استفاده نمی‌شود.
/// </summary>
internal sealed class PermissionsDialog : Form
{
    private readonly List<Permission> _items = Enum.GetValues<Permission>().ToList();
    private readonly CheckedListBox _list = new()
    {
        CheckOnClick = true,
        IntegralHeight = false,
        Width = 380,
        Height = 200,
    };

    public PermissionsDialog(string username, IReadOnlySet<Permission> current)
    {
        Text = $"دسترسی‌های ویرایش و ابطال: {username}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 320);

        foreach (var permission in _items)
        {
            _list.Items.Add(PermissionCodes.DisplayName(permission), current.Contains(permission));
        }

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8),
        };
        body.Controls.Add(new Label
        {
            Text = "دسترسی‌های تیک‌خورده فقط برای اسناد شعبه‌ی این کاربر معتبر است و با ذخیره، همان لحظه اعمال می‌شود.",
            AutoSize = true,
            MaximumSize = new Size(380, 0),
        });
        body.Controls.Add(_list);

        var ok = new Button { Text = "ذخیره", AutoSize = true, DialogResult = DialogResult.OK };
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
    }

    /// <summary>دسترسی‌های تیک‌خورده، به ترتیب نمایش.</summary>
    public IReadOnlyList<Permission> Selected =>
        _list.CheckedIndices.Cast<int>().Select(index => _items[index]).ToList();
}
