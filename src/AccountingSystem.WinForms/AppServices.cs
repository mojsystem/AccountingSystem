using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Services;

namespace AccountingSystem.WinForms;

/// <summary>همان سرویس‌های هسته‌ی مشترک که نسخه وب نیز استفاده می‌کند.</summary>
internal sealed class AppServices
{
    public AppServices(IAccountingRepository repository)
    {
        Trades = new CurrencyTradeService(repository);
        Admin = new CurrencyAdminService(repository);
        Reports = new ReportService(repository);
        Users = new UserService(repository);
    }

    public CurrencyTradeService Trades { get; }

    public CurrencyAdminService Admin { get; }

    public ReportService Reports { get; }

    public UserService Users { get; }
}
