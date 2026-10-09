using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.WinForms;

internal interface IRefreshable
{
    Task RefreshAsync();
}

/// <summary>آیتم ComboBox با مقدار فنی و متن نمایشی.</summary>
internal sealed class ComboItem
{
    public ComboItem(string value, string text)
    {
        Value = value;
        Text = text;
    }

    public string Value { get; }

    public string Text { get; }

    public override string ToString() => Text;
}

internal static class UiHelpers
{
    public static void ShowError(IWin32Window owner, Exception ex)
    {
        var message = ex is BusinessRuleException or ConcurrencyConflictException
            ? ex.Message
            : "خطای غیرمنتظره: " + ex.Message;
        MessageBox.Show(owner, message, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public static void ShowInfo(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "پیام", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = SystemColors.Window,
    };

    public static void Fill(DataGridView grid, string[] headers, IEnumerable<string[]> rows)
    {
        var table = new DataTable();
        for (var i = 0; i < headers.Length; i++)
        {
            table.Columns.Add("c" + i, typeof(string));
        }
        foreach (var row in rows)
        {
            table.Rows.Add(row);
        }

        grid.DataSource = table;
        for (var i = 0; i < headers.Length && i < grid.Columns.Count; i++)
        {
            grid.Columns[i].HeaderText = headers[i];
        }
    }

    public static Label MakeLabel(string text, Point? location = null)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(6, 10, 2, 0) };
        if (location.HasValue)
        {
            label.Location = location.Value;
        }
        return label;
    }

    public static FlowLayoutPanel CreateInputPanel() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8),
        WrapContents = true,
    };

    /// <summary>انتخاب آیتمی که مقدار آن برابر value است؛ در غیر این صورت اولین آیتم.</summary>
    public static void SelectByValue(ComboBox box, string? value)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboItem item && item.Value == value)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// فهرست شعبه‌ها. فهرست ورودی را فراخواننده از قبل فیلتر می‌کند (فقط شعبه‌هایی که کاربر وظیفه‌ی لازم را دارد).
    /// «همه‌ی شعبه‌ها» فقط برای مدیر سیستم نمایش داده می‌شود.
    /// </summary>
    public static void FillBranches(ComboBox box, IReadOnlyList<BranchInfo> branches, CurrentUser user, bool includeAll, string? selectedValue)
    {
        box.Items.Clear();
        var allowAll = includeAll && user.Role == UserRole.Admin;
        if (allowAll)
        {
            box.Items.Add(new ComboItem(string.Empty, "همه‌ی شعبه‌ها"));
        }
        foreach (var branch in branches)
        {
            box.Items.Add(new ComboItem(branch.Id.ToString(CultureInfo.InvariantCulture), $"{branch.Code} - {branch.Name}"));
        }
        box.Enabled = true;
        var fallback = allowAll ? string.Empty : user.BranchId?.ToString(CultureInfo.InvariantCulture);
        SelectByValue(box, selectedValue ?? fallback);
        if (box.SelectedIndex < 0 && box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    /// <summary>شناسه‌ی شعبه‌ی انتخاب‌شده؛ null یعنی «همه‌ی شعبه‌ها».</summary>
    public static int? SelectedBranchId(ComboBox box)
    {
        if (box.SelectedItem is ComboItem item
            && item.Value.Length > 0
            && int.TryParse(item.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }
        return null;
    }

    /// <summary>شعبه‌ی لازم برای ثبت عملیات؛ اگر انتخاب نشده باشد خطای قابل نمایش برمی‌گرداند.</summary>
    public static int RequiredBranchId(ComboBox box) =>
        SelectedBranchId(box) ?? throw new BusinessRuleException("شعبه را انتخاب کنید.");

    /// <summary>بازه‌ی شمسی دو کادر متن را به بازه‌ی میلادی [از، تا) تبدیل می‌کند.</summary>
    public static (DateTime From, DateTime To) ParseJalaliRange(TextBox from, TextBox to)
    {
        if (!PersianDate.TryParseRange(from.Text, to.Text, DateTime.Now, out var fromInclusive, out var toExclusive, out var error))
        {
            throw new BusinessRuleException(error ?? "بازه‌ی تاریخ نامعتبر است.");
        }
        return (fromInclusive, toExclusive);
    }

    /// <summary>کادر تاریخ شمسی را با امروز پر می‌کند.</summary>
    public static void SetJalaliToday(params TextBox[] boxes)
    {
        var today = PersianDate.FormatDate(DateTime.Now);
        foreach (var box in boxes)
        {
            box.Text = today;
        }
    }

    /// <summary>
    /// گفت‌وگوی کوچک برای گرفتن یک متن (مثلاً دلیل ابطال). اگر کاربر انصراف دهد، null برمی‌گرداند.
    /// </summary>
    public static string? PromptText(IWin32Window owner, string title, string label)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(440, 130),
            Font = new Font("Tahoma", 9f),
            RightToLeft = RightToLeft.Yes,
            RightToLeftLayout = true,
        };
        var text = new TextBox { Location = new Point(12, 40), Width = 410, MaxLength = 200 };
        var ok = new Button { Text = "تأیید", DialogResult = DialogResult.OK, Location = new Point(230, 82), Width = 90 };
        var cancel = new Button { Text = "انصراف", DialogResult = DialogResult.Cancel, Location = new Point(332, 82), Width = 90 };
        form.Controls.AddRange(new Control[] { new Label { Text = label, Location = new Point(12, 14), AutoSize = true }, text, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? text.Text.Trim() : null;
    }

    /// <summary>رسید HTML را در یک فایل موقت می‌نویسد و با مرورگر پیش‌فرض باز می‌کند (از آنجا می‌توان چاپ کرد).</summary>
    public static void OpenHtml(string html, string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>ذخیره‌ی فایل خروجی Excel با گفت‌وگوی ذخیره.</summary>
    public static void SaveExcel(IWin32Window owner, byte[] bytes, string defaultName)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "فایل Excel (*.xlsx)|*.xlsx",
            FileName = defaultName,
            AddExtension = true,
        };
        if (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            File.WriteAllBytes(dialog.FileName, bytes);
            ShowInfo(owner, "فایل Excel ذخیره شد.");
        }
    }
}
