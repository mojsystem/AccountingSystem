using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Services;

namespace AccountingSystem.WinForms;

/// <summary>همان سرویس‌های هسته‌ی مشترک که نسخه وب نیز استفاده می‌کند.</summary>
internal sealed class AppServices
{
    public AppServices(IAccountingRepository repository, IDatabaseMaintenance maintenance)
    {
        Branches = new BranchService(repository);
        Trades = new CurrencyTradeService(repository);
        Admin = new CurrencyAdminService(repository);
        Manual = new ManualJournalService(repository);
        Reports = new ReportService(repository);
        Receipts = new ReceiptService(repository);
        Users = new UserService(repository);
        Permissions = new PermissionService(repository);
        Accounts = new AccountService(repository);
        Customers = new CustomerService(repository, Permissions);
        Backup = new BackupService(maintenance);
    }

    public BranchService Branches { get; }

    public CurrencyTradeService Trades { get; }

    public CurrencyAdminService Admin { get; }

    public ManualJournalService Manual { get; }

    public ReportService Reports { get; }

    public ReceiptService Receipts { get; }

    public UserService Users { get; }

    public PermissionService Permissions { get; }

    public AccountService Accounts { get; }

    public CustomerService Customers { get; }

    public BackupService Backup { get; }
}
