using System.Data;
using System.Globalization;
using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data;

/// <summary>
/// پیاده‌سازی IAccountingRepository با ADO.NET و SQL Server (سازگار با SQL Server 2016).
/// همه‌ی ثبت‌ها داخل یک تراکنش انجام می‌شوند. به‌روزرسانی موجودی‌ها با «مقایسه‌ی مقدار قبلی»
/// انجام می‌شود تا تغییر همزمان داده باعث موجودی منفی یا ثبت نادرست نشود.
/// ترتیب قفل: ابتدا صندوق ریال شعبه، سپس صندوق ارز و در آخر بهای تمام‌شده (در همه‌ی ثبت‌ها یکسان است).
/// </summary>
public sealed class SqlAccountingRepository : IAccountingRepository
{
    private const int DuplicateKeyError = 2627;
    private const int UniqueIndexError = 2601;
    private const string LatestMovementDescription = "فقط آخرین معامله‌ی همین ارز در این شعبه را می‌توان باطل کرد. ابتدا معاملات جدیدتر همین ارز را باطل کنید.";

    private const string TradeSelect = @"
SELECT t.Id, t.BranchId, br.Code, br.Name, t.TradeType, t.CurrencyCode, t.Amount, t.Rate, t.IrrAmount, t.CostIrr, t.ProfitIrr, t.FeeIrr,
       t.CustomerName, t.NationalCode, t.Note, t.OccurredAt, u.Username, t.IsVoided, t.VoidedAt, vu.Username, t.VoidReason
FROM dbo.CurrencyTransactions t
INNER JOIN dbo.Branches br ON br.Id = t.BranchId
INNER JOIN dbo.Users u ON u.Id = t.CreatedBy
LEFT JOIN dbo.Users vu ON vu.Id = t.VoidedBy";

    private readonly string _connectionString;

    public SqlAccountingRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT Id, Code, Name, CreatedAt FROM dbo.Branches ORDER BY Id;";
        var result = new List<BranchInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new BranchInfo(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        }
        return result;
    }

    public async Task<int> AddBranchAsync(string code, string name, DateTime now, CancellationToken ct = default)
    {
        try
        {
            return await WithTransactionAsync(async (conn, tx) =>
            {
                var idValue = await ScalarAsync(conn, tx, ct,
                    "INSERT INTO dbo.Branches (Code, Name, CreatedAt) OUTPUT INSERTED.Id VALUES (@code, @name, @now);",
                    new SqlParameter("@code", code),
                    new SqlParameter("@name", name),
                    new SqlParameter("@now", now));
                var branchId = Convert.ToInt32(idValue, CultureInfo.InvariantCulture);

                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.CashBoxes (BranchId, CurrencyCode, Name, Balance, UpdatedAt) SELECT @branchId, c.Code, N'صندوق ' + c.Name, 0, @now FROM dbo.Currencies c;",
                    new SqlParameter("@branchId", branchId),
                    new SqlParameter("@now", now));

                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.CurrencyInventory (BranchId, CurrencyCode, TotalCostIrr, UpdatedAt) SELECT @branchId, c.Code, 0, @now FROM dbo.Currencies c WHERE c.Code <> N'IRR';",
                    new SqlParameter("@branchId", branchId),
                    new SqlParameter("@now", now));

                return branchId;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("این کد شعبه قبلاً ثبت شده است.");
        }
    }

    public async Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT Code, Name, DecimalPlaces, IsActive FROM dbo.Currencies ORDER BY Code;";
        var result = new List<CurrencyInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CurrencyInfo(reader.GetString(0).Trim(), reader.GetString(1), reader.GetByte(2), reader.GetBoolean(3)));
        }
        return result;
    }

    public async Task AddCurrencyAsync(CurrencyInfo currency, int userId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync(async (conn, tx) =>
            {
                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.Currencies (Code, Name, DecimalPlaces, IsActive, CreatedAt) VALUES (@code, @name, @decimals, @active, @now);",
                    new SqlParameter("@code", currency.Code),
                    new SqlParameter("@name", currency.Name),
                    new SqlParameter("@decimals", currency.DecimalPlaces),
                    new SqlParameter("@active", currency.IsActive),
                    new SqlParameter("@now", now));

                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.CashBoxes (BranchId, CurrencyCode, Name, Balance, UpdatedAt) SELECT b.Id, @code, @boxName, 0, @now FROM dbo.Branches b;",
                    new SqlParameter("@code", currency.Code),
                    new SqlParameter("@boxName", "صندوق " + currency.Name),
                    new SqlParameter("@now", now));

                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.CurrencyInventory (BranchId, CurrencyCode, TotalCostIrr, UpdatedAt) SELECT b.Id, @code, 0, @now FROM dbo.Branches b;",
                    new SqlParameter("@code", currency.Code),
                    new SqlParameter("@now", now));

                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.Accounts (Code, Name, AccountType, IsActive) VALUES (@accountCode, @accountName, N'Asset', 1);",
                    new SqlParameter("@accountCode", AccountCodes.ForeignCash(currency.Code)),
                    new SqlParameter("@accountName", "موجودی ارز - " + currency.Name));

                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("این ارز قبلاً ثبت شده است.");
        }
    }

    public async Task<TradeSnapshot?> GetTradeSnapshotAsync(int branchId, string currencyCode, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await QuerySnapshotAsync(conn, branchId, currencyCode, ct);
    }

    public async Task<IReadOnlyList<RateInfo>> GetLatestRatesAsync(int? branchId, CancellationToken ct = default)
    {
        const string sql = @"
WITH LatestRates AS
(
    SELECT r.BranchId, r.CurrencyCode, r.BuyRateIrr, r.SellRateIrr, r.CreatedAt,
           ROW_NUMBER() OVER (PARTITION BY r.BranchId, r.CurrencyCode ORDER BY r.Id DESC) AS RowNo
    FROM dbo.ExchangeRates r
    WHERE (@branchId IS NULL OR r.BranchId = @branchId)
)
SELECT l.BranchId, br.Name, l.CurrencyCode, c.Name, l.BuyRateIrr, l.SellRateIrr, l.CreatedAt
FROM LatestRates l
INNER JOIN dbo.Branches br ON br.Id = l.BranchId
INNER JOIN dbo.Currencies c ON c.Code = l.CurrencyCode
WHERE l.RowNo = 1
ORDER BY br.Id, l.CurrencyCode;";
        var result = new List<RateInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RateInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2).Trim(),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDateTime(6)));
        }
        return result;
    }

    public async Task AddRateAsync(int branchId, string currencyCode, decimal buyRateIrr, decimal sellRateIrr, int userId, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await ExecuteAsync(conn, null, ct,
            "INSERT INTO dbo.ExchangeRates (BranchId, CurrencyCode, BuyRateIrr, SellRateIrr, CreatedAt, CreatedBy) VALUES (@branchId, @code, @buy, @sell, @now, @userId);",
            new SqlParameter("@branchId", branchId),
            new SqlParameter("@code", currencyCode),
            Money("@buy", buyRateIrr),
            Money("@sell", sellRateIrr),
            new SqlParameter("@now", now),
            new SqlParameter("@userId", userId));
    }

    public async Task<IReadOnlyList<CashBoxInfo>> GetCashBoxesAsync(int? branchId, CancellationToken ct = default)
    {
        const string sql = @"
SELECT b.Id, b.BranchId, br.Name, b.CurrencyCode, b.Name, b.Balance, b.UpdatedAt
FROM dbo.CashBoxes b
INNER JOIN dbo.Branches br ON br.Id = b.BranchId
WHERE (@branchId IS NULL OR b.BranchId = @branchId)
ORDER BY br.Id, CASE WHEN b.CurrencyCode = N'IRR' THEN 0 ELSE 1 END, b.CurrencyCode;";
        var result = new List<CashBoxInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CashBoxInfo(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3).Trim(),
                reader.GetString(4),
                reader.GetDecimal(5),
                reader.GetDateTime(6)));
        }
        return result;
    }

    public async Task<IReadOnlyList<InventoryInfo>> GetInventoryAsync(int? branchId, CancellationToken ct = default)
    {
        const string sql = "SELECT BranchId, CurrencyCode, TotalCostIrr FROM dbo.CurrencyInventory WHERE (@branchId IS NULL OR BranchId = @branchId);";
        var result = new List<InventoryInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new InventoryInfo(reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetDecimal(2)));
        }
        return result;
    }

    public Task<long?> PostAsync(PostingDraft posting, CancellationToken ct = default)
    {
        return WithTransactionAsync<long?>(async (conn, tx) =>
        {
            long? tradeId = null;
            if (posting.Void is { } voidDraft)
            {
                await EnsureLatestActivePoolTradeAsync(conn, tx, posting.BranchId, voidDraft.CurrencyCode, voidDraft.TradeId, ct);
                await MarkTradeVoidedAsync(conn, tx, posting, voidDraft, ct);
                tradeId = voidDraft.TradeId;
            }
            else if (posting.Trade is { } trade)
            {
                tradeId = await InsertTradeAsync(conn, tx, posting.BranchId, trade, ct);
            }

            foreach (var movement in posting.CashMovements)
            {
                await ApplyCashMovementAsync(conn, tx, posting, movement, tradeId, ct);
            }

            foreach (var inventory in posting.Inventory)
            {
                await ApplyInventoryAsync(conn, tx, posting.BranchId, inventory, posting.OccurredAt, ct);
            }

            await InsertJournalAsync(conn, tx, posting, tradeId, ct);
            return tradeId;
        }, ct);
    }

    public async Task<IReadOnlyList<TradeInfo>> GetTradesAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        var sql = TradeSelect + @"
WHERE t.OccurredAt >= @from AND t.OccurredAt < @to AND (@branchId IS NULL OR t.BranchId = @branchId)
ORDER BY t.OccurredAt DESC, t.Id DESC;";
        var result = new List<TradeInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadTrade(reader));
        }
        return result;
    }

    public async Task<TradeInfo?> GetTradeAsync(long tradeId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await QueryTradeAsync(conn, tradeId, ct);
    }

    public async Task<VoidContext?> GetTradeForVoidAsync(long tradeId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var trade = await QueryTradeAsync(conn, tradeId, ct);
        if (trade is null)
        {
            return null;
        }

        var snapshot = await QuerySnapshotAsync(conn, trade.BranchId, trade.CurrencyCode, ct)
            ?? throw new BusinessRuleException("ارز یا شعبه‌ی معامله یافت نشد.");
        var latest = await LatestPoolMovementAsync(conn, null, trade.BranchId, trade.CurrencyCode, ct);
        var isLatest = latest is not null && latest.RefType == SourceTypes.Trade && latest.RefId == tradeId;
        return new VoidContext(trade, snapshot, isLatest);
    }

    public async Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        const string sql = @"
SELECT e.Id, e.OccurredAt, e.Description, e.SourceType, e.BranchId, br.Name, l.LineNumber, l.AccountCode, a.Name, l.Debit, l.Credit
FROM dbo.JournalEntries e
INNER JOIN dbo.Branches br ON br.Id = e.BranchId
INNER JOIN dbo.JournalLines l ON l.JournalEntryId = e.Id
INNER JOIN dbo.Accounts a ON a.Code = l.AccountCode
WHERE e.OccurredAt >= @from AND e.OccurredAt < @to AND (@branchId IS NULL OR e.BranchId = @branchId)
ORDER BY e.OccurredAt DESC, e.Id DESC, l.LineNumber;";
        var order = new List<long>();
        var entries = new Dictionary<long, JournalEntryBuilder>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (!entries.TryGetValue(id, out var builder))
            {
                builder = new JournalEntryBuilder(id, reader.GetDateTime(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5));
                entries.Add(id, builder);
                order.Add(id);
            }
            builder.Lines.Add(new JournalLineInfo(
                reader.GetInt32(6),
                reader.GetString(7).Trim(),
                reader.GetString(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10)));
        }
        return order.Select(id => entries[id].Build()).ToList();
    }

    public async Task<int> CountUsersAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Users;", conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        const string sql = @"
SELECT u.Id, u.Username, u.FullName, u.Role, u.IsActive, u.PasswordHash, u.BranchId, br.Name
FROM dbo.Users u
LEFT JOIN dbo.Branches br ON br.Id = u.BranchId
WHERE u.Username = @username;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@username", username));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new UserAccount(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseRole(reader.GetString(3)),
            reader.GetBoolean(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
            ReadNullableString(reader, 7));
    }

    public async Task<IReadOnlyList<UserInfo>> GetUsersAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT u.Id, u.Username, u.FullName, u.Role, u.IsActive, u.CreatedAt, u.BranchId, br.Name
FROM dbo.Users u
LEFT JOIN dbo.Branches br ON br.Id = u.BranchId
ORDER BY u.Username;";
        var result = new List<UserInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new UserInfo(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseRole(reader.GetString(3)),
                reader.GetBoolean(4),
                reader.GetDateTime(5),
                reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
                ReadNullableString(reader, 7)));
        }
        return result;
    }

    public async Task<int> AddUserAsync(string username, string fullName, UserRole role, int? branchId, string passwordHash, DateTime now, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO dbo.Users (Username, FullName, PasswordHash, Role, BranchId, IsActive, CreatedAt)
OUTPUT INSERTED.Id
VALUES (@username, @fullName, @passwordHash, @role, @branchId, 1, @now);";
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@username", username));
            cmd.Parameters.Add(new SqlParameter("@fullName", fullName));
            cmd.Parameters.Add(new SqlParameter("@passwordHash", passwordHash));
            cmd.Parameters.Add(new SqlParameter("@role", role.ToString()));
            cmd.Parameters.Add(NullableInt("@branchId", branchId));
            cmd.Parameters.Add(new SqlParameter("@now", now));
            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(id, CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("این نام کاربری قبلاً ثبت شده است.");
        }
    }

    private static async Task<TradeSnapshot?> QuerySnapshotAsync(SqlConnection conn, int branchId, string currencyCode, CancellationToken ct)
    {
        const string sql = @"
SELECT c.Code, c.Name, c.DecimalPlaces, c.IsActive,
       (SELECT x.Balance FROM dbo.CashBoxes x WHERE x.BranchId = br.Id AND x.CurrencyCode = N'IRR') AS IrrBalance,
       (SELECT f.Balance FROM dbo.CashBoxes f WHERE f.BranchId = br.Id AND f.CurrencyCode = c.Code) AS ForeignBalance,
       (SELECT i.TotalCostIrr FROM dbo.CurrencyInventory i WHERE i.BranchId = br.Id AND i.CurrencyCode = c.Code) AS ForeignCostIrr
FROM dbo.Branches br
CROSS JOIN dbo.Currencies c
WHERE br.Id = @branchId AND c.Code = @code;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@code", currencyCode));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var currency = new CurrencyInfo(reader.GetString(0).Trim(), reader.GetString(1), reader.GetByte(2), reader.GetBoolean(3));
        var irrBalance = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
        var foreignBalance = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5);
        var foreignCost = reader.IsDBNull(6) ? 0m : reader.GetDecimal(6);
        return new TradeSnapshot(branchId, currency, irrBalance, foreignBalance, foreignCost);
    }

    private static async Task<TradeInfo?> QueryTradeAsync(SqlConnection conn, long tradeId, CancellationToken ct)
    {
        var sql = TradeSelect + " WHERE t.Id = @id;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@id", tradeId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return ReadTrade(reader);
    }

    /// <summary>
    /// آخرین حرکت فعال موجودی یک ارز در یک شعبه: معاملات باطل‌نشده و افتتاحیه‌های ارز.
    /// حرکت‌های ابطال (VOID) و معاملات باطل‌شده نادیده گرفته می‌شوند.
    /// </summary>
    private static async Task<PoolMovement?> LatestPoolMovementAsync(SqlConnection conn, SqlTransaction? tx, int branchId, string currencyCode, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (1) m.RefType, m.RefId
FROM dbo.CashMovements m
INNER JOIN dbo.CashBoxes b ON b.Id = m.CashBoxId
LEFT JOIN dbo.CurrencyTransactions t ON t.Id = m.RefId AND m.RefType = N'TRADE'
WHERE b.BranchId = @branchId AND b.CurrencyCode = @code
  AND m.RefType IN (N'TRADE', N'OPENING')
  AND (m.RefType = N'OPENING' OR t.IsVoided = 0)
ORDER BY m.Id DESC;";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@code", currencyCode));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var refId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        return new PoolMovement(reader.GetString(0), refId);
    }

    /// <summary>
    /// قبل از ابطال، صندوق‌های شعبه قفل می‌شوند و بررسی می‌شود که معامله هنوز آخرین حرکت همان ارز باشد.
    /// قفل‌ها با ترتیب ثابت گرفته می‌شوند تا ثبت‌های همزمان روی همان شعبه بن‌بست ایجاد نکنند.
    /// </summary>
    private static async Task EnsureLatestActivePoolTradeAsync(SqlConnection conn, SqlTransaction tx, int branchId, string currencyCode, long tradeId, CancellationToken ct)
    {
        await ExecuteAsync(conn, tx, ct,
            "UPDATE dbo.CashBoxes SET Balance = Balance WHERE BranchId = @branchId AND CurrencyCode = N'IRR';",
            new SqlParameter("@branchId", branchId));
        await ExecuteAsync(conn, tx, ct,
            "UPDATE dbo.CashBoxes SET Balance = Balance WHERE BranchId = @branchId AND CurrencyCode = @code;",
            new SqlParameter("@branchId", branchId),
            new SqlParameter("@code", currencyCode));

        var latest = await LatestPoolMovementAsync(conn, tx, branchId, currencyCode, ct);
        if (latest is null || latest.RefType != SourceTypes.Trade || latest.RefId != tradeId)
        {
            throw new BusinessRuleException(LatestMovementDescription);
        }
    }

    private static async Task MarkTradeVoidedAsync(SqlConnection conn, SqlTransaction tx, PostingDraft posting, VoidDraft voidDraft, CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.CurrencyTransactions
SET IsVoided = 1, VoidedAt = @now, VoidedBy = @userId, VoidReason = @reason
WHERE Id = @tradeId AND BranchId = @branchId AND IsVoided = 0;";
        var affected = await ExecuteAsync(conn, tx, ct, sql,
            new SqlParameter("@now", posting.OccurredAt),
            new SqlParameter("@userId", posting.UserId),
            new SqlParameter("@reason", voidDraft.Reason),
            new SqlParameter("@tradeId", voidDraft.TradeId),
            new SqlParameter("@branchId", posting.BranchId));
        if (affected != 1)
        {
            throw new BusinessRuleException("این معامله قبلاً باطل شده است.");
        }
    }

    private static async Task<long> InsertTradeAsync(SqlConnection conn, SqlTransaction tx, int branchId, TradeDraft trade, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CurrencyTransactions
    (BranchId, TradeType, CurrencyCode, Amount, Rate, IrrAmount, CostIrr, ProfitIrr, FeeIrr, CustomerName, NationalCode, Note, OccurredAt, CreatedBy)
OUTPUT INSERTED.Id
VALUES (@branchId, @tradeType, @code, @amount, @rate, @irr, @cost, @profit, @fee, @customer, @nationalCode, @note, @occurredAt, @userId);";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@tradeType", trade.Type == TradeType.Buy ? "BUY" : "SELL"));
        cmd.Parameters.Add(new SqlParameter("@code", trade.CurrencyCode));
        cmd.Parameters.Add(Money("@amount", trade.Amount));
        cmd.Parameters.Add(Money("@rate", trade.Rate));
        cmd.Parameters.Add(Money("@irr", trade.IrrAmount));
        cmd.Parameters.Add(Money("@cost", trade.CostIrr));
        cmd.Parameters.Add(Money("@profit", trade.ProfitIrr));
        cmd.Parameters.Add(Money("@fee", trade.FeeIrr));
        cmd.Parameters.Add(new SqlParameter("@customer", (object?)trade.CustomerName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nationalCode", (object?)trade.NationalCode ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@note", (object?)trade.Note ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@occurredAt", trade.OccurredAt));
        cmd.Parameters.Add(new SqlParameter("@userId", trade.UserId));
        var id = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    private static async Task ApplyCashMovementAsync(SqlConnection conn, SqlTransaction tx, PostingDraft posting, CashMovementDraft movement, long? tradeId, CancellationToken ct)
    {
        const string updateSql = @"
UPDATE dbo.CashBoxes
SET Balance = Balance + @delta, UpdatedAt = @occurredAt
OUTPUT INSERTED.Id, INSERTED.Balance
WHERE BranchId = @branchId AND CurrencyCode = @code AND Balance = @expected;";

        int boxId;
        decimal newBalance;
        await using (var cmd = new SqlCommand(updateSql, conn, tx))
        {
            cmd.Parameters.Add(Money("@delta", movement.Delta));
            cmd.Parameters.Add(new SqlParameter("@occurredAt", posting.OccurredAt));
            cmd.Parameters.Add(new SqlParameter("@branchId", posting.BranchId));
            cmd.Parameters.Add(new SqlParameter("@code", movement.CurrencyCode));
            cmd.Parameters.Add(Money("@expected", movement.ExpectedBalance));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new ConcurrencyConflictException();
            }
            boxId = reader.GetInt32(0);
            newBalance = reader.GetDecimal(1);
        }

        await ExecuteAsync(conn, tx, ct,
            @"INSERT INTO dbo.CashMovements (CashBoxId, Amount, BalanceAfter, RefType, RefId, Description, OccurredAt, CreatedBy)
              VALUES (@boxId, @amount, @balanceAfter, @refType, @refId, @description, @occurredAt, @userId);",
            new SqlParameter("@boxId", boxId),
            Money("@amount", movement.Delta),
            Money("@balanceAfter", newBalance),
            new SqlParameter("@refType", posting.SourceType),
            RefIdParam("@refId", tradeId),
            new SqlParameter("@description", movement.Description),
            new SqlParameter("@occurredAt", posting.OccurredAt),
            new SqlParameter("@userId", posting.UserId));
    }

    private static async Task ApplyInventoryAsync(SqlConnection conn, SqlTransaction tx, int branchId, InventoryDraft change, DateTime occurredAt, CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.CurrencyInventory
SET TotalCostIrr = @newCost, UpdatedAt = @occurredAt
WHERE BranchId = @branchId AND CurrencyCode = @code AND TotalCostIrr = @expected;";
        var affected = await ExecuteAsync(conn, tx, ct, sql,
            Money("@newCost", change.NewCostIrr),
            new SqlParameter("@occurredAt", occurredAt),
            new SqlParameter("@branchId", branchId),
            new SqlParameter("@code", change.CurrencyCode),
            Money("@expected", change.ExpectedCostIrr));
        if (affected != 1)
        {
            throw new ConcurrencyConflictException();
        }
    }

    private static async Task InsertJournalAsync(SqlConnection conn, SqlTransaction tx, PostingDraft posting, long? tradeId, CancellationToken ct)
    {
        const string headerSql = @"
INSERT INTO dbo.JournalEntries (BranchId, OccurredAt, Description, SourceType, SourceId, CreatedBy, CreatedAt)
OUTPUT INSERTED.Id
VALUES (@branchId, @occurredAt, @description, @sourceType, @sourceId, @userId, @occurredAt);";
        long entryId;
        await using (var cmd = new SqlCommand(headerSql, conn, tx))
        {
            cmd.Parameters.Add(new SqlParameter("@branchId", posting.BranchId));
            cmd.Parameters.Add(new SqlParameter("@occurredAt", posting.OccurredAt));
            cmd.Parameters.Add(new SqlParameter("@description", posting.Journal.Description));
            cmd.Parameters.Add(new SqlParameter("@sourceType", posting.SourceType));
            cmd.Parameters.Add(RefIdParam("@sourceId", tradeId));
            cmd.Parameters.Add(new SqlParameter("@userId", posting.UserId));
            entryId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        var lineNo = 0;
        foreach (var line in posting.Journal.Lines)
        {
            lineNo++;
            await ExecuteAsync(conn, tx, ct,
                "INSERT INTO dbo.JournalLines (JournalEntryId, LineNumber, AccountCode, Debit, Credit) VALUES (@entryId, @lineNo, @account, @debit, @credit);",
                new SqlParameter("@entryId", entryId),
                new SqlParameter("@lineNo", lineNo),
                new SqlParameter("@account", line.AccountCode),
                Money("@debit", line.Debit),
                Money("@credit", line.Credit));
        }
    }

    private async Task<T> WithTransactionAsync<T>(Func<SqlConnection, SqlTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        try
        {
            var result = await work(conn, tx);
            tx.Commit();
            return result;
        }
        catch
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // اگر اتصال قطع شده باشد، Rollback انجام شده است؛ خطای اصلی را برمی‌گردانیم.
            }
            throw;
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<int> ExecuteAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct, string sql, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct, string sql, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static TradeInfo ReadTrade(SqlDataReader reader)
    {
        return new TradeInfo(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4) == "BUY" ? TradeType.Buy : TradeType.Sell,
            reader.GetString(5).Trim(),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8),
            reader.GetDecimal(9),
            reader.GetDecimal(10),
            reader.GetDecimal(11),
            ReadNullableString(reader, 12),
            ReadNullableString(reader, 13),
            ReadNullableString(reader, 14),
            reader.GetDateTime(15),
            reader.GetString(16),
            reader.GetBoolean(17),
            reader.IsDBNull(18) ? (DateTime?)null : reader.GetDateTime(18),
            ReadNullableString(reader, 19),
            ReadNullableString(reader, 20));
    }

    private static SqlParameter Money(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = value };

    private static SqlParameter RefIdParam(string name, long? value) =>
        new(name, SqlDbType.BigInt) { Value = (object?)value ?? DBNull.Value };

    private static SqlParameter NullableInt(string name, int? value) =>
        new(name, SqlDbType.Int) { Value = (object?)value ?? DBNull.Value };

    private static string? ReadNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static UserRole ParseRole(string value) => Enum.Parse<UserRole>(value, ignoreCase: true);

    private sealed record PoolMovement(string RefType, long? RefId);

    private sealed class JournalEntryBuilder
    {
        public JournalEntryBuilder(long id, DateTime occurredAt, string description, string sourceType, int branchId, string branchName)
        {
            Id = id;
            OccurredAt = occurredAt;
            Description = description;
            SourceType = sourceType;
            BranchId = branchId;
            BranchName = branchName;
        }

        public long Id { get; }

        public DateTime OccurredAt { get; }

        public string Description { get; }

        public string SourceType { get; }

        public int BranchId { get; }

        public string BranchName { get; }

        public List<JournalLineInfo> Lines { get; } = new();

        public JournalEntryInfo Build() => new(Id, OccurredAt, Description, SourceType, BranchId, BranchName, Lines);
    }
}
