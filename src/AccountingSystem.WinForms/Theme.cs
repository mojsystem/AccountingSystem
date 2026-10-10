namespace AccountingSystem.WinForms;

/// <summary>
/// ظاهر یکدست برنامه‌ی ویندوزی: فونت، رنگ‌ها، دکمه‌ها، جدول‌ها و تب‌ها.
/// دکمه‌هایی که با «ثبت»، «ذخیره»، «ایجاد» یا «ورود» شروع می‌شوند دکمه‌ی اصلی (رنگی)
/// و دکمه‌های «ابطال» و «حذف» دکمه‌ی خطر (قرمز) می‌شوند.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(243, 245, 249);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(203, 213, 225);
    public static readonly Color GridLine = Color.FromArgb(226, 232, 240);
    public static readonly Color Text = Color.FromArgb(30, 41, 59);
    public static readonly Color MutedText = Color.FromArgb(100, 116, 139);
    public static readonly Color Primary = Color.FromArgb(15, 118, 110);
    public static readonly Color PrimaryHover = Color.FromArgb(13, 107, 100);
    public static readonly Color Danger = Color.FromArgb(185, 28, 28);
    public static readonly Color HeaderBackground = Color.FromArgb(241, 245, 249);
    public static readonly Color Selection = Color.FromArgb(204, 251, 241);

    private static readonly string[] PrimaryPrefixes = { "ثبت", "ذخیره", "ایجاد", "ورود" };
    private static readonly string[] DangerPrefixes = { "ابطال", "حذف" };

    /// <summary>فونت پایه‌ی برنامه؛ Tahoma برای متن فارسی روی ویندوز خوانا و استاندارد است.</summary>
    public static Font BodyFont { get; } = new("Tahoma", 9.5f);

    public static Font BoldFont { get; } = new("Tahoma", 9.5f, FontStyle.Bold);

    /// <summary>روی فرم یا کنترل اصلی صدا زده می‌شود تا همه‌ی کنترل‌های فرزند هم‌شکل شوند.</summary>
    public static void Apply(Control root)
    {
        root.Font = BodyFont;
        if (root is Form or UserControl)
        {
            root.BackColor = Background;
        }
        ApplyToChildren(root);
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = GridLine;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBackground;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = MutedText;
        grid.ColumnHeadersDefaultCellStyle.Font = BoldFont;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.DefaultCellStyle.SelectionBackColor = Selection;
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Padding = new Padding(6, 2, 6, 2);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 251);
        grid.RowTemplate.Height = 30;
        grid.RowHeadersVisible = false;
    }

    private static void ApplyToChildren(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            switch (child)
            {
                case Button button:
                    StyleButton(button);
                    break;
                case DataGridView grid:
                    StyleGrid(grid);
                    break;
                case TabControl tabs:
                    tabs.Padding = new Point(16, 6);
                    break;
                case TextBox textBox:
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
            }
            ApplyToChildren(child);
        }
    }

    private static void StyleButton(Button button)
    {
        var text = button.Text.Trim();
        var primary = StartsWithAny(text, PrimaryPrefixes);
        var danger = !primary && StartsWithAny(text, DangerPrefixes);

        button.FlatStyle = FlatStyle.Flat;
        button.Cursor = Cursors.Hand;
        button.Padding = new Padding(12, 3, 12, 3);
        button.MinimumSize = new Size(0, 30);
        button.BackColor = primary ? Primary : Surface;
        button.ForeColor = primary ? Color.White : danger ? Danger : Text;
        button.FlatAppearance.BorderColor = primary ? Primary : Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = primary ? PrimaryHover : HeaderBackground;
        button.FlatAppearance.MouseDownBackColor = Border;
    }

    private static bool StartsWithAny(string text, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
