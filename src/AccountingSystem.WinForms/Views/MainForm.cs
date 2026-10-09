using AccountingSystem.Core.Domain;
using AccountingSystem.WinForms.Tabs;

namespace AccountingSystem.WinForms.Views;

internal sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<IRefreshable> _refreshers = new();

    public MainForm(AppServices services, CurrentUser user)
    {
        var branchText = string.IsNullOrEmpty(user.BranchName) ? string.Empty : " - شعبه: " + user.BranchName;
        Text = $"سیستم حسابداری صرافی - {user.FullName} ({RoleText(user.Role)}){branchText}";
        ClientSize = new Size(1200, 760);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 9f);

        AddTab("داشبورد", new DashboardTab(services, user));
        AddTab("خرید و فروش ارز", new TradeTab(services, user));
        AddTab("نرخ و ارزها", new RatesTab(services, user));
        AddTab("صندوق‌ها", new CashTab(services, user));
        AddTab("موجودی‌های افتتاحیه", new OpeningsTab(services, user));
        AddTab("اسناد حسابداری", new JournalTab(services, user));
        if (user.Role == UserRole.Admin)
        {
            AddTab("شعبه‌ها", new BranchesTab(services, user));
            AddTab("کاربران", new UsersTab(services, user));
            AddTab("سرفصل حساب‌ها", new AccountsTab(services, user));
        }

        Controls.Add(_tabs);
        _tabs.SelectedIndexChanged += async (_, _) => await RefreshSelectedAsync();
        Shown += async (_, _) => await RefreshSelectedAsync();
    }

    private void AddTab(string title, UserControl tab)
    {
        var page = new TabPage(title);
        tab.Dock = DockStyle.Fill;
        page.Controls.Add(tab);
        _tabs.TabPages.Add(page);
        _refreshers.Add((IRefreshable)tab);
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
