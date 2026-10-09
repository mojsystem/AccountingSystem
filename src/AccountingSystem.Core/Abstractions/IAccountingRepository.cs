using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Abstractions;

/// <summary>قرارداد ذخیره‌سازی. پیاده‌سازی SQL Server در پروژه‌ی AccountingSystem.Data است.</summary>
public interface IAccountingRepository
{
    Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default);

    /// <summary>شعبه‌ی جدید را با صندوق ریال و صندوق همه‌ی ارزها و بهای تمام‌شده‌ی صفر می‌سازد.</summary>
    Task<int> AddBranchAsync(string code, string name, DateTime now, CancellationToken ct = default);

    Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(CancellationToken ct = default);

    /// <summary>ارز جدید را برای همه‌ی شعبه‌ها (صندوق و بهای تمام‌شده) و حساب دفتر کل ثبت می‌کند.</summary>
    Task AddCurrencyAsync(CurrencyInfo currency, int userId, DateTime now, CancellationToken ct = default);

    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default);

    Task<TradeSnapshot?> GetTradeSnapshotAsync(int branchId, string currencyCode, CancellationToken ct = default);

    /// <summary>
    /// دفتر کامل یک شعبه: همه‌ی معاملات، افتتاحیه‌ها و سندهای دستی فعال به‌همراه نسخه‌ی دفتر.
    /// نسخه قبل از خواندن داده‌ها خوانده می‌شود تا ثبت همزمان باعث پذیرفته شدن داده‌ی ناسازگار نشود.
    /// </summary>
    Task<BranchLedger> GetBranchLedgerAsync(int branchId, CancellationToken ct = default);

    /// <summary>آخرین نرخ هر ارز. branchId = null یعنی نرخ همه‌ی شعبه‌ها.</summary>
    Task<IReadOnlyList<RateInfo>> GetLatestRatesAsync(int? branchId, CancellationToken ct = default);

    Task AddRateAsync(int branchId, string currencyCode, decimal buyRateIrr, decimal sellRateIrr, int userId, DateTime now, CancellationToken ct = default);

    Task<IReadOnlyList<CashBoxInfo>> GetCashBoxesAsync(int? branchId, CancellationToken ct = default);

    Task<IReadOnlyList<InventoryInfo>> GetInventoryAsync(int? branchId, CancellationToken ct = default);

    /// <summary>
    /// ثبت atomic تمام تغییرات یک رویداد (ابطال، سند جدید، تعدیل‌ها، صندوق و موجودی).
    /// اگر نسخه‌ی دفتر یا مقدار صندوق تغییر کرده باشد، ConcurrencyConflictException پرتاب می‌شود.
    /// خروجی شناسه‌ی سند جدید (معامله، افتتاحیه یا سند دستی) است؛ برای ابطال null.
    /// </summary>
    Task<long?> PostAsync(PostingDraft posting, CancellationToken ct = default);

    /// <summary>
    /// ویرایش اطلاعات توصیفی معامله (نام مشتری، کد ملی، یادداشت). این تغییر اثر مالی ندارد و بازمحاسبه نمی‌شود.
    /// </summary>
    Task UpdateTradeDetailsAsync(long tradeId, int branchId, string? customerName, string? nationalCode, string? note, int userId, DateTime now, CancellationToken ct = default);

    Task<IReadOnlyList<TradeInfo>> GetTradesAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default);

    Task<TradeInfo?> GetTradeAsync(long tradeId, CancellationToken ct = default);

    Task<IReadOnlyList<OpeningInfo>> GetOpeningsAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default);

    Task<OpeningInfo?> GetOpeningAsync(long openingId, CancellationToken ct = default);

    Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default);

    /// <summary>یک سند حسابداری با سطرهایش؛ null اگر وجود نداشته باشد.</summary>
    Task<JournalEntryInfo?> GetJournalEntryAsync(long entryId, CancellationToken ct = default);

    Task<int> CountUsersAsync(CancellationToken ct = default);

    Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken ct = default);

    Task<IReadOnlyList<UserInfo>> GetUsersAsync(CancellationToken ct = default);

    Task<int> AddUserAsync(string username, string fullName, UserRole role, int? branchId, string passwordHash, DateTime now, CancellationToken ct = default);
}
