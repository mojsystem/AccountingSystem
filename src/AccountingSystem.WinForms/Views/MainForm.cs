using AccountingSystem.Core.Domain;
using AccountingSystem.WinForms.Tabs;

namespace AccountingSystem.WinForms.Views;

internal sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly StatusStrip _statusBar = new() { SizingGrip = false };
    private readonly ToolStripStatusLabel _userStatus = new();
    private readonly ToolStripStatusLabel _branchStatus = new();
    private readonly ToolStripStatusLabel _schemaStatus = new();
    private readonly List<IRefreshable> _refreshers = new();

    public MainForm(AppServices services, CurrentUser user, int schemaVersion)
    {
        Text = $"سیستم حسابداری صرافی - {user.FullName} ({RoleText(user.Role)})";
        ClientSize = new Size(1200, 760);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.BodyFont;

        // نوار وضعیت: کاربر، شعبه و نسخه‌ی پایگاه داده همیشه دیده شوند.
        _userStatus.Text = $"کاربر: {user.FullName} · {RoleText(user.Role)}";
        _branchStatus.Text = string.IsNullOrEmpty(user.BranchName) ? "دسترسی: همه‌ی شعبه‌ها" : "شعبه‌ی اصلی: " + user.BranchName;
        _schemaStatus.Text = $"نسخه‌ی پایگاه داده: {schemaVersion}";
        _schemaStatus.Alignment = ToolStripItemAlignment.Right;
        _statusBar.Items.AddRange(new ToolStripItem[]
        {
            _userStatus,
            new ToolStripSeparator(),
            _branchStatus,
            _schemaStatus,
        });
        Controls.Add(_statusBar);

        AddTab("داشبورد", new DashboardTab(services, user));
        AddTab("خرید و فروش ارز", new TradeTab(services, user));
        AddTab("دریافت و پرداخت", new CashTransactionsTab(services, user));
        AddTab("مشتریان", new CustomersTab(services, user));
        AddTab("نرخ‌ها و افزودن ارز", new RatesTab(services, user));
        AddTab("صندوق‌ها", new CashTab(services, user));
        AddTab("موجودی‌های افتتاحیه", new OpeningsTab(services, user));
        AddTab("اسناد حسابداری", new JournalTab(services, user));
        AddTab("گزارش اشخاص", new ReportsTab(services, user));
        if (user.Role == UserRole.Admin)
        {
            AddTab("شعبه‌ها", new BranchesTab(services, user));
            AddTab("کاربران", new UsersTab(services, user));
            AddTab("سرفصل حساب‌ها", new AccountsTab(services, user));
            AddTab("پشتیبان و بازیابی", new BackupTab(services, user));
        }

        // تب‌ها از چپ به راست معکوس اضافه می‌شوند تا «داشبورد» در راست‌ترین جای نوار باشد.
        // چون هر دو فهرست با هم درج می‌شوند، index تب و IRefreshable همیشه هم‌خوان می‌مانند.
        _tabs.SelectedIndex = _tabs.TabPages.Count - 1;

        // کنترل پرشونده باید آخر اضافه شود تا نوار وضعیت پایین پنجره جا باشد.
        Controls.Add(_tabs);
        _tabs.SelectedIndexChanged += async (_, _) => await RefreshSelectedAsync();
        Shown += async (_, _) => await RefreshSelectedAsync();
        Theme.Apply(this);
    }

    private void AddTab(string title, UserControl tab)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        tab.Dock = DockStyle.Fill;
        page.Controls.Add(tab);
        _tabs.TabPages.Insert(0, page);
        _refreshers.Insert(0, (IRefreshable)tab);
    }

    private async Task RefreshSelectedAsync()
    {
        var index = _tabs.SelectedIndex;
        if (index < 0 || index >= _refreshers.Count)
        {
            return;
        }

        try
        {
            await _refreshers[index].RefreshAsync();
        }
        catch (Exception ex)
        {
            UiHelpers.ShowError(this, ex);
        }
    }

    private static string RoleText(UserRole role) => role == UserRole.Admin ? "مدیر" : "کاربر شعبه";
}
