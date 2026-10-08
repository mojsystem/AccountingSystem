using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Abstractions;

/// <summary>قرارداد ذخیره‌سازی. پیاده‌سازی SQL Server در پروژه‌ی AccountingSystem.Data است.</summary>
public interface IAccountingRepository
{
    Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(CancellationToken ct = default);

    Task AddCurrencyAsync(CurrencyInfo currency, int userId, DateTime now, CancellationToken ct = default);

    Task<TradeSnapshot?> GetTradeSnapshotAsync(string currencyCode, CancellationToken ct = default);

    Task<IReadOnlyList<RateInfo>> GetLatestRatesAsync(CancellationToken ct = default);

    Task AddRateAsync(string currencyCode, decimal buyRateIrr, decimal sellRateIrr, int userId, DateTime now, CancellationToken ct = default);

    Task<IReadOnlyList<CashBoxInfo>> GetCashBoxesAsync(CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, decimal>> GetInventoryCostsAsync(CancellationToken ct = default);

    /// <summary>ثبت atomic تمام تغییرات. در صورت تغییر همزمان داده، ConcurrencyConflictException پرتاب می‌شود.</summary>
    Task<long?> PostAsync(PostingDraft posting, CancellationToken ct = default);

    Task<IReadOnlyList<TradeInfo>> GetTradesAsync(DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default);

    Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default);

    Task<int> CountUsersAsync(CancellationToken ct = default);

    Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken ct = default);

    Task<IReadOnlyList<UserInfo>> GetUsersAsync(CancellationToken ct = default);

    Task<int> AddUserAsync(string username, string fullName, UserRole role, string passwordHash, DateTime now, CancellationToken ct = default);
}
