using AccountingSystem.Core.Domain;
using AccountingSystem.WinForms.Tabs;

namespace AccountingSystem.WinForms.Views;

internal sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<IRefreshable> _refreshers = new();

    public MainForm(AppServices services, CurrentUser user)
    {
        Text = $"سیستم حسابداری صرافی - {user.FullName} ({RoleText(user.Role)})";
        ClientSize = new Size(1200, 760);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Tahoma", 9f);

        AddTab("داشبورد", new DashboardTab(services));
        AddTab("خرید و فروش ارز", new TradeTab(services, user));
        AddTab("نرخ و ارزها", new RatesTab(services, user));
        AddTab("صندوق‌ها", new CashTab(services, user));
        AddTab("اسناد حسابداری", new JournalTab(services));
        if (user.Role == UserRole.Admin)
        {
            AddTab("کاربران", new UsersTab(services, user));
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

    private static string RoleText(UserRole role) => role == UserRole.Admin ? "مدیر" : "کاربر صندوق";
}
