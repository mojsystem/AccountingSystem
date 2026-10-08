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
/// </summary>
public sealed class SqlAccountingRepository : IAccountingRepository
{
    private const int DuplicateKeyError = 2627;
    private const int UniqueIndexError = 2601;

    private readonly string _connectionString;

    public SqlAccountingRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
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
                    "INSERT INTO dbo.CashBoxes (CurrencyCode, Name, Balance, UpdatedAt) VALUES (@code, @boxName, 0, @now);",
                    new SqlParameter("@code", currency.Code),
                    new SqlParameter("@boxName", "صندوق " + currency.Name),
                    new SqlParameter("@now", now));

                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.CurrencyInventory (CurrencyCode, TotalCostIrr, UpdatedAt) VALUES (@code, 0, @now);",
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

    public async Task<TradeSnapshot?> GetTradeSnapshotAsync(string currencyCode, CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.Code, c.Name, c.DecimalPlaces, c.IsActive,
       (SELECT b.Balance FROM dbo.CashBoxes b WHERE b.CurrencyCode = N'IRR') AS IrrBalance,
       (SELECT f.Balance FROM dbo.CashBoxes f WHERE f.CurrencyCode = c.Code) AS ForeignBalance,
       (SELECT i.TotalCostIrr FROM dbo.CurrencyInventory i WHERE i.CurrencyCode = c.Code) AS ForeignCostIrr
FROM dbo.Currencies c
WHERE c.Code = @code;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
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
        return new TradeSnapshot(currency, irrBalance, foreignBalance, foreignCost);
    }

    public async Task<IReadOnlyList<RateInfo>> GetLatestRatesAsync(CancellationToken ct = default)
    {
        const string sql = @"
WITH LatestRates AS
(
    SELECT r.CurrencyCode, r.BuyRateIrr, r.SellRateIrr, r.CreatedAt,
           ROW_NUMBER() OVER (PARTITION BY r.CurrencyCode ORDER BY r.Id DESC) AS RowNo
    FROM dbo.ExchangeRates r
)
SELECT l.CurrencyCode, c.Name, l.BuyRateIrr, l.SellRateIrr, l.CreatedAt
FROM LatestRates l
INNER JOIN dbo.Currencies c ON c.Code = l.CurrencyCode
WHERE l.RowNo = 1
ORDER BY l.CurrencyCode;";
        var result = new List<RateInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RateInfo(reader.GetString(0).Trim(), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDateTime(4)));
        }
        return result;
    }

    public async Task AddRateAsync(string currencyCode, decimal buyRateIrr, decimal sellRateIrr, int userId, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await ExecuteAsync(conn, null, ct,
            "INSERT INTO dbo.ExchangeRates (CurrencyCode, BuyRateIrr, SellRateIrr, CreatedAt, CreatedBy) VALUES (@code, @buy, @sell, @now, @userId);",
            new SqlParameter("@code", currencyCode),
            Money("@buy", buyRateIrr),
            Money("@sell", sellRateIrr),
            new SqlParameter("@now", now),
            new SqlParameter("@userId", userId));
    }

    public async Task<IReadOnlyList<CashBoxInfo>> GetCashBoxesAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT b.Id, b.CurrencyCode, b.Name, b.Balance, b.UpdatedAt
FROM dbo.CashBoxes b
ORDER BY CASE WHEN b.CurrencyCode = N'IRR' THEN 0 ELSE 1 END, b.CurrencyCode;";
        var result = new List<CashBoxInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CashBoxInfo(reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetString(2), reader.GetDecimal(3), reader.GetDateTime(4)));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetInventoryCostsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT CurrencyCode, TotalCostIrr FROM dbo.CurrencyInventory;";
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0).Trim()] = reader.GetDecimal(1);
        }
        return result;
    }

    public Task<long?> PostAsync(PostingDraft posting, CancellationToken ct = default)
    {
        return WithTransactionAsync<long?>(async (conn, tx) =>
        {
            long? tradeId = null;
            if (posting.Trade is not null)
            {
                tradeId = await InsertTradeAsync(conn, tx, posting.Trade, ct);
            }

            foreach (var movement in posting.CashMovements)
            {
                await ApplyCashMovementAsync(conn, tx, posting, movement, tradeId, ct);
            }

            foreach (var inventory in posting.Inventory)
            {
                await ApplyInventoryAsync(conn, tx, inventory, posting.OccurredAt, ct);
            }

            await InsertJournalAsync(conn, tx, posting, tradeId, ct);
            return tradeId;
        }, ct);
    }

    public async Task<IReadOnlyList<TradeInfo>> GetTradesAsync(DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        const string sql = @"
SELECT t.Id, t.TradeType, t.CurrencyCode, t.Amount, t.Rate, t.IrrAmount, t.CostIrr, t.ProfitIrr,
       t.CustomerName, t.NationalCode, t.Note, t.OccurredAt, u.Username
FROM dbo.CurrencyTransactions t
INNER JOIN dbo.Users u ON u.Id = t.CreatedBy
WHERE t.OccurredAt >= @from AND t.OccurredAt < @to
ORDER BY t.OccurredAt DESC, t.Id DESC;";
        var result = new List<TradeInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TradeInfo(
                reader.GetInt64(0),
                reader.GetString(1) == "BUY" ? TradeType.Buy : TradeType.Sell,
                reader.GetString(2).Trim(),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                ReadNullableString(reader, 8),
                ReadNullableString(reader, 9),
                ReadNullableString(reader, 10),
                reader.GetDateTime(11),
                reader.GetString(12)));
        }
        return result;
    }

    public async Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        const string sql = @"
SELECT e.Id, e.OccurredAt, e.Description, e.SourceType, l.LineNumber, l.AccountCode, a.Name, l.Debit, l.Credit
FROM dbo.JournalEntries e
INNER JOIN dbo.JournalLines l ON l.JournalEntryId = e.Id
INNER JOIN dbo.Accounts a ON a.Code = l.AccountCode
WHERE e.OccurredAt >= @from AND e.OccurredAt < @to
ORDER BY e.OccurredAt DESC, e.Id DESC, l.LineNumber;";
        var order = new List<long>();
        var entries = new Dictionary<long, JournalEntryBuilder>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (!entries.TryGetValue(id, out var builder))
            {
                builder = new JournalEntryBuilder(id, reader.GetDateTime(1), reader.GetString(2), reader.GetString(3));
                entries.Add(id, builder);
                order.Add(id);
            }
            builder.Lines.Add(new JournalLineInfo(
                reader.GetInt32(4),
                reader.GetString(5).Trim(),
                reader.GetString(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8)));
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
        const string sql = "SELECT Id, Username, FullName, Role, IsActive, PasswordHash FROM dbo.Users WHERE Username = @username;";
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
            reader.GetString(5));
    }

    public async Task<IReadOnlyList<UserInfo>> GetUsersAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT Id, Username, FullName, Role, IsActive, CreatedAt FROM dbo.Users ORDER BY Username;";
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
                reader.GetDateTime(5)));
        }
        return result;
    }

    public async Task<int> AddUserAsync(string username, string fullName, UserRole role, string passwordHash, DateTime now, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO dbo.Users (Username, FullName, PasswordHash, Role, IsActive, CreatedAt)
OUTPUT INSERTED.Id
VALUES (@username, @fullName, @passwordHash, @role, 1, @now);";
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@username", username));
            cmd.Parameters.Add(new SqlParameter("@fullName", fullName));
            cmd.Parameters.Add(new SqlParameter("@passwordHash", passwordHash));
            cmd.Parameters.Add(new SqlParameter("@role", role.ToString()));
            cmd.Parameters.Add(new SqlParameter("@now", now));
            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(id, CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("این نام کاربری قبلاً ثبت شده است.");
        }
    }

    private static async Task<long> InsertTradeAsync(SqlConnection conn, SqlTransaction tx, TradeDraft trade, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CurrencyTransactions
    (TradeType, CurrencyCode, Amount, Rate, IrrAmount, CostIrr, ProfitIrr, CustomerName, NationalCode, Note, OccurredAt, CreatedBy)
OUTPUT INSERTED.Id
VALUES (@tradeType, @code, @amount, @rate, @irr, @cost, @profit, @customer, @nationalCode, @note, @occurredAt, @userId);";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@tradeType", trade.Type == TradeType.Buy ? "BUY" : "SELL"));
        cmd.Parameters.Add(new SqlParameter("@code", trade.CurrencyCode));
        cmd.Parameters.Add(Money("@amount", trade.Amount));
        cmd.Parameters.Add(Money("@rate", trade.Rate));
        cmd.Parameters.Add(Money("@irr", trade.IrrAmount));
        cmd.Parameters.Add(Money("@cost", trade.CostIrr));
        cmd.Parameters.Add(Money("@profit", trade.ProfitIrr));
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
WHERE CurrencyCode = @code AND Balance = @expected;";

        int boxId;
        decimal newBalance;
        await using (var cmd = new SqlCommand(updateSql, conn, tx))
        {
            cmd.Parameters.Add(Money("@delta", movement.Delta));
            cmd.Parameters.Add(new SqlParameter("@occurredAt", posting.OccurredAt));
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

    private static async Task ApplyInventoryAsync(SqlConnection conn, SqlTransaction tx, InventoryDraft change, DateTime occurredAt, CancellationToken ct)
    {
        const string sql = @"
UPDATE dbo.CurrencyInventory
SET TotalCostIrr = @newCost, UpdatedAt = @occurredAt
WHERE CurrencyCode = @code AND TotalCostIrr = @expected;";
        var affected = await ExecuteAsync(conn, tx, ct, sql,
            Money("@newCost", change.NewCostIrr),
            new SqlParameter("@occurredAt", occurredAt),
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
INSERT INTO dbo.JournalEntries (OccurredAt, Description, SourceType, SourceId, CreatedBy, CreatedAt)
OUTPUT INSERTED.Id
VALUES (@occurredAt, @description, @sourceType, @sourceId, @userId, @occurredAt);";
        long entryId;
        await using (var cmd = new SqlCommand(headerSql, conn, tx))
        {
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

    private static SqlParameter Money(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = value };

    private static SqlParameter RefIdParam(string name, long? value) =>
        new(name, SqlDbType.BigInt) { Value = (object?)value ?? DBNull.Value };

    private static string? ReadNullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static UserRole ParseRole(string value) => Enum.Parse<UserRole>(value, ignoreCase: true);

    private sealed class JournalEntryBuilder
    {
        public JournalEntryBuilder(long id, DateTime occurredAt, string description, string sourceType)
        {
            Id = id;
            OccurredAt = occurredAt;
            Description = description;
            SourceType = sourceType;
        }

        public long Id { get; }

        public DateTime OccurredAt { get; }

        public string Description { get; }

        public string SourceType { get; }

        public List<JournalLineInfo> Lines { get; } = new();

        public JournalEntryInfo Build() => new(Id, OccurredAt, Description, SourceType, Lines);
    }
}
