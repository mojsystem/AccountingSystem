using System.Data;
using System.Globalization;
using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data;

/// <summary>
/// پیاده‌سازی IAccountingRepository با ADO.NET و SQL Server (هدف: SQL Server 2019).
/// هر ثبت داخل یک تراکنش انجام می‌شود. ابتدا نسخه‌ی دفتر شعبه یک واحد بالا می‌رود؛ این کار شعبه را قفل می‌کند
/// و ثبت همزمان دیگر را با ConcurrencyConflictException رد می‌کند. سپس سندها، صندوق‌ها و موجودی‌ها
/// با «مقایسه‌ی مقدار قبلی» به‌روز می‌شوند. در پایان، موجودی تراکمی هر حرکت صندوق (BalanceAfter) از نو محاسبه می‌شود.
/// </summary>
public sealed class SqlAccountingRepository : IAccountingRepository
{
    private const int DuplicateKeyError = 2627;
    private const int UniqueIndexError = 2601;

    private const string OpeningSelect = @"
SELECT o.Id, o.BranchId, br.Name, o.CurrencyCode, o.Quantity, o.RateIrr, o.CostIrr, o.OccurredAt, u.Username, o.IsVoided, o.VoidReason
FROM dbo.OpeningBalances o
INNER JOIN dbo.Branches br ON br.Id = o.BranchId
INNER JOIN dbo.Users u ON u.Id = o.CreatedBy";

    private const string JournalSelect = @"
SELECT e.Id, e.OccurredAt, e.Description, e.SourceType, e.SourceId, e.IsVoided, e.BranchId, br.Name, l.LineNumber, l.AccountCode, a.Name, l.Debit, l.Credit
FROM dbo.JournalEntries e
INNER JOIN dbo.Branches br ON br.Id = e.BranchId
INNER JOIN dbo.JournalLines l ON l.JournalEntryId = e.Id
INNER JOIN dbo.Accounts a ON a.Code = l.AccountCode";

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

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT Code, Name, AccountType, IsActive FROM dbo.Accounts ORDER BY Code;";
        var result = new List<AccountInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AccountInfo(reader.GetString(0).Trim(), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }
        return result;
    }

    public async Task<BranchLedger> GetBranchLedgerAsync(int branchId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        // نسخه‌ی دفتر اول خوانده می‌شود. اگر ثبتی بین خواندن‌ها انجام شود، ثبت بعدی با نسخه‌ی قدیمی رد می‌شود.
        var versionValue = await ScalarAsync(conn, null, ct,
            "SELECT LedgerVersion FROM dbo.Branches WHERE Id = @branchId;",
            new SqlParameter("@branchId", branchId));
        if (versionValue is null or DBNull)
        {
            throw new BusinessRuleException("شعبه‌ی انتخابی یافت نشد.");
        }
        var version = Convert.ToInt64(versionValue, CultureInfo.InvariantCulture);

        var events = new List<LedgerEvent>();
        var active = new HashSet<DocRef>();
        await ReadTradeEventsAsync(conn, branchId, events, active, ct);
        await ReadOpeningEventsAsync(conn, branchId, events, active, ct);
        await ReadManualEventsAsync(conn, branchId, events, active, ct);

        var irrBalance = 0m;
        var pools = new Dictionary<string, PoolBalance>(StringComparer.Ordinal);
        const string balanceSql = @"
SELECT b.CurrencyCode, b.Balance, i.TotalCostIrr
FROM dbo.CashBoxes b
LEFT JOIN dbo.CurrencyInventory i ON i.BranchId = b.BranchId AND i.CurrencyCode = b.CurrencyCode
WHERE b.BranchId = @branchId;";
        await using (var cmd = new SqlCommand(balanceSql, conn))
        {
            cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var code = reader.GetString(0).Trim();
                var balance = reader.GetDecimal(1);
                var cost = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
                if (code == CurrencyCodes.Irr)
                {
                    irrBalance = balance;
                }
                else
                {
                    pools[code] = new PoolBalance(balance, cost);
                }
            }
        }

        return new BranchLedger(branchId, version, events, active, irrBalance, pools);
    }

    private static async Task ReadTradeEventsAsync(SqlConnection conn, int branchId, List<LedgerEvent> events, HashSet<DocRef> active, CancellationToken ct)
    {
        const string sql = @"
SELECT t.Id, t.TradeType, t.CurrencyCode, t.Amount, t.IrrAmount, t.FeeIrr, t.CostIrr, t.ProfitIrr, t.OccurredAt, t.Seq
FROM dbo.CurrencyTransactions t
WHERE t.BranchId = @branchId AND t.IsVoided = 0;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var isBuy = reader.GetString(1) == "BUY";
            var code = reader.GetString(2).Trim();
            var amount = reader.GetDecimal(3);
            var irr = reader.GetDecimal(4);
            var fee = reader.GetDecimal(5);
            var cost = reader.GetDecimal(6);
            var profit = reader.GetDecimal(7);
            var occurredAt = reader.GetDateTime(8);
            var seq = reader.GetInt64(9);
            active.Add(new DocRef(LedgerDocKind.Trade, id));
            events.Add(isBuy
                ? new LedgerEvent(LedgerDocKind.Trade, id, LedgerEventKind.Acquire, code, occurredAt, seq,
                    amount, irr, fee, -(irr - fee), cost, profit)
                : new LedgerEvent(LedgerDocKind.Trade, id, LedgerEventKind.Dispose, code, occurredAt, seq,
                    amount, irr, fee, irr + fee, cost, profit));
        }
    }

    private static async Task ReadOpeningEventsAsync(SqlConnection conn, int branchId, List<LedgerEvent> events, HashSet<DocRef> active, CancellationToken ct)
    {
        const string sql = @"
SELECT o.Id, o.CurrencyCode, o.Quantity, o.CostIrr, o.OccurredAt, o.Seq
FROM dbo.OpeningBalances o
WHERE o.BranchId = @branchId AND o.IsVoided = 0;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var code = reader.GetString(1).Trim();
            var quantity = reader.GetDecimal(2);
            var cost = reader.GetDecimal(3);
            var occurredAt = reader.GetDateTime(4);
            var seq = reader.GetInt64(5);
            active.Add(new DocRef(LedgerDocKind.Opening, id));
            events.Add(code == CurrencyCodes.Irr
                ? new LedgerEvent(LedgerDocKind.Opening, id, LedgerEventKind.CashOnly, code, occurredAt, seq,
                    0m, 0m, 0m, quantity, 0m, 0m)
                : new LedgerEvent(LedgerDocKind.Opening, id, LedgerEventKind.Acquire, code, occurredAt, seq,
                    quantity, cost, 0m, 0m, 0m, 0m));
        }
    }

    /// <summary>
    /// سندهای دستی فعال. اثر آن روی صندوق ریال، مجموع بدهکار منهای بستانکار سطرهای حساب 1001 است.
    /// </summary>
    private static async Task ReadManualEventsAsync(SqlConnection conn, int branchId, List<LedgerEvent> events, HashSet<DocRef> active, CancellationToken ct)
    {
        const string sql = @"
SELECT e.Id, e.OccurredAt, e.Seq, COALESCE(SUM(CASE WHEN l.AccountCode = N'1001' THEN l.Debit - l.Credit END), 0)
FROM dbo.JournalEntries e
LEFT JOIN dbo.JournalLines l ON l.JournalEntryId = e.Id
WHERE e.BranchId = @branchId AND e.SourceType = N'MANUAL' AND e.IsVoided = 0
GROUP BY e.Id, e.OccurredAt, e.Seq;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var occurredAt = reader.GetDateTime(1);
            var seq = reader.GetInt64(2);
            var irrDelta = reader.GetDecimal(3);
            active.Add(new DocRef(LedgerDocKind.Manual, id));
            if (irrDelta != 0m)
            {
                events.Add(new LedgerEvent(LedgerDocKind.Manual, id, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                    occurredAt, seq, 0m, 0m, 0m, irrDelta, 0m, 0m));
            }
        }
    }

    public async Task UpdateTradeDetailsAsync(long tradeId, int branchId, string? customerName, string? nationalCode, string? note, int userId, DateTime now, CancellationToken ct = default)
    {
        await WithTransactionAsync<bool>(async (conn, tx) =>
        {
            var affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.CurrencyTransactions
SET CustomerName = @customer, NationalCode = @nationalCode, Note = @note
WHERE Id = @id AND BranchId = @branchId AND IsVoided = 0;",
                new SqlParameter("@customer", (object?)customerName ?? DBNull.Value),
                new SqlParameter("@nationalCode", (object?)nationalCode ?? DBNull.Value),
                new SqlParameter("@note", (object?)note ?? DBNull.Value),
                new SqlParameter("@id", tradeId),
                new SqlParameter("@branchId", branchId));
            if (affected != 1)
            {
                throw new BusinessRuleException("معامله‌ی انتخابی یافت نشد یا باطل شده است.");
            }

            await InsertAuditAsync(conn, tx, userId, now, "TRADE_EDIT_DETAILS", SourceTypes.Trade, tradeId,
                "اطلاعات توصیفی معامله به‌روز شد (بدون اثر مالی)", ct);
            return true;
        }, ct);
    }

    public async Task<IReadOnlyList<OpeningInfo>> GetOpeningsAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        var sql = OpeningSelect + @"
WHERE o.OccurredAt >= @from AND o.OccurredAt < @to AND (@branchId IS NULL OR o.BranchId = @branchId)
ORDER BY o.OccurredAt DESC, o.Id DESC;";
        var result = new List<OpeningInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadOpening(reader));
        }
        return result;
    }

    public async Task<OpeningInfo?> GetOpeningAsync(long openingId, CancellationToken ct = default)
    {
        var sql = OpeningSelect + " WHERE o.Id = @id;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@id", openingId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadOpening(reader) : null;
    }

    private static OpeningInfo ReadOpening(SqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3).Trim(),
        reader.GetDecimal(4),
        reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5),
        reader.GetDecimal(6),
        reader.GetDateTime(7),
        reader.GetString(8),
        reader.GetBoolean(9),
        ReadNullableString(reader, 10));

    public async Task<IReadOnlyList<JournalEntryInfo>> GetJournalAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        var sql = JournalSelect + @"
WHERE e.OccurredAt >= @from AND e.OccurredAt < @to AND (@branchId IS NULL OR e.BranchId = @branchId)
ORDER BY e.OccurredAt DESC, e.Id DESC, l.LineNumber;";
        await using var conn = await OpenAsync(ct);
        return await ReadJournalAsync(conn, sql, ct,
            new SqlParameter("@from", fromInclusive),
            new SqlParameter("@to", toExclusive),
            NullableInt("@branchId", branchId));
    }

    public async Task<JournalEntryInfo?> GetJournalEntryAsync(long entryId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var entries = await ReadJournalAsync(conn, JournalSelect + " WHERE e.Id = @id ORDER BY l.LineNumber;", ct,
            new SqlParameter("@id", entryId));
        return entries.Count == 0 ? null : entries[0];
    }

    private static async Task<IReadOnlyList<JournalEntryInfo>> ReadJournalAsync(SqlConnection conn, string sql, CancellationToken ct, params SqlParameter[] parameters)
    {
        var order = new List<long>();
        var entries = new Dictionary<long, JournalEntryBuilder>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (!entries.TryGetValue(id, out var builder))
            {
                builder = new JournalEntryBuilder(
                    id,
                    reader.GetDateTime(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
                    reader.GetBoolean(5),
                    reader.GetInt32(6),
                    reader.GetString(7));
                entries.Add(id, builder);
                order.Add(id);
            }
            builder.Lines.Add(new JournalLineInfo(
                reader.GetInt32(8),
                reader.GetString(9).Trim(),
                reader.GetString(10),
                reader.GetDecimal(11),
                reader.GetDecimal(12)));
        }
        return order.Select(id => entries[id].Build()).ToList();
    }

    public Task<long?> PostAsync(PostingDraft posting, CancellationToken ct = default)
    {
        return WithTransactionAsync<long?>(async (conn, tx) =>
        {
            await BumpLedgerVersionAsync(conn, tx, posting.BranchId, posting.ExpectedVersion, ct);

            if (posting.Void is { } voidDraft)
            {
                await MarkVoidedAsync(conn, tx, posting.BranchId, voidDraft, posting.Now, posting.UserId, ct);
                await InsertVoidJournalAsync(conn, tx, posting.BranchId, voidDraft, posting.Now, posting.UserId, ct);
            }

            long? newId = null;
            if (posting.Trade is { } trade)
            {
                newId = await InsertTradeAsync(conn, tx, posting.BranchId, trade, ct);
            }
            else if (posting.Opening is { } opening)
            {
                newId = await InsertOpeningAsync(conn, tx, posting.BranchId, opening, posting.Now, ct);
            }

            foreach (var journal in posting.Journals)
            {
                // سند دستی شناسه‌ی خودش را دارد (شناسه‌ی سطر سند)؛ بقیه به سند معامله یا افتتاحیه وصل می‌شوند.
                var isManual = journal.SourceType == SourceTypes.Manual;
                var sourceId = isManual ? (long?)null : ResolveId(journal.Source.Id, newId);
                var entryId = await InsertJournalAsync(conn, tx, posting.BranchId, journal, sourceId, posting.UserId, posting.Now, ct);
                if (isManual && journal.Source.Id == 0)
                {
                    newId = entryId;
                }
            }

            foreach (var update in posting.CostUpdates)
            {
                var affected = await ExecuteAsync(conn, tx, ct,
                    "UPDATE dbo.CurrencyTransactions SET CostIrr = @cost, ProfitIrr = @profit WHERE Id = @id AND BranchId = @branchId AND IsVoided = 0;",
                    Money("@cost", update.CostIrr),
                    Money("@profit", update.ProfitIrr),
                    new SqlParameter("@id", update.TradeId),
                    new SqlParameter("@branchId", posting.BranchId));
                if (affected != 1)
                {
                    throw new ConcurrencyConflictException();
                }
            }

            await ApplyCashMovementsAsync(conn, tx, posting, newId, ct);

            foreach (var inventory in posting.Inventory)
            {
                await ApplyInventoryAsync(conn, tx, posting.BranchId, inventory, posting.Now, ct);
            }

            await RecalculateBalancesAfterAsync(conn, tx, posting.BranchId, ct);
            await InsertAuditAsync(conn, tx, posting.UserId, posting.Now, posting.Action, EntityTypeOf(posting.Entity.Kind),
                ResolveId(posting.Entity.Id, newId), posting.AuditDetails, ct);
            return newId;
        }, ct);
    }

    /// <summary>
    /// نسخه‌ی دفتر شعبه را یک واحد بالا می‌برد. UPDATE همزمان ردیف شعبه را قفل می‌کند و نسخه‌ی قدیمی را رد می‌کند.
    /// </summary>
    private static async Task BumpLedgerVersionAsync(SqlConnection conn, SqlTransaction tx, int branchId, long expectedVersion, CancellationToken ct)
    {
        var affected = await ExecuteAsync(conn, tx, ct,
            "UPDATE dbo.Branches SET LedgerVersion = LedgerVersion + 1 WHERE Id = @branchId AND LedgerVersion = @expected;",
            new SqlParameter("@branchId", branchId),
            new SqlParameter("@expected", expectedVersion));
        if (affected != 1)
        {
            throw new ConcurrencyConflictException();
        }
    }

    /// <summary>
    /// سند اصلی را باطل علامت می‌زند. سطرهای سند اصلی حذف نمی‌شوند؛ برای معامله و افتتاحیه سطرهای سند هم علامت می‌خورند.
    /// </summary>
    private static async Task MarkVoidedAsync(SqlConnection conn, SqlTransaction tx, int branchId, VoidDraft voidDraft, DateTime now, int userId, CancellationToken ct)
    {
        var id = voidDraft.Doc.Id;
        var parameters = new[]
        {
            new SqlParameter("@now", now),
            new SqlParameter("@userId", userId),
            new SqlParameter("@reason", voidDraft.Reason),
            new SqlParameter("@id", id),
            new SqlParameter("@branchId", branchId),
        };

        int affected;
        switch (voidDraft.Doc.Kind)
        {
            case LedgerDocKind.Trade:
                affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.CurrencyTransactions
SET IsVoided = 1, VoidedAt = @now, VoidedBy = @userId, VoidReason = @reason
WHERE Id = @id AND BranchId = @branchId AND IsVoided = 0;", CloneParameters(parameters));
                await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.JournalEntries SET IsVoided = 1
WHERE BranchId = @branchId AND SourceType IN (N'TRADE', N'ADJUST') AND SourceId = @id;", CloneParameters(parameters));
                break;

            case LedgerDocKind.Opening:
                affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.OpeningBalances
SET IsVoided = 1, VoidedAt = @now, VoidedBy = @userId, VoidReason = @reason
WHERE Id = @id AND BranchId = @branchId AND IsVoided = 0;", CloneParameters(parameters));
                await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.JournalEntries SET IsVoided = 1
WHERE BranchId = @branchId AND SourceType = N'OPENING' AND SourceId = @id;", CloneParameters(parameters));
                break;

            default:
                affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.JournalEntries SET IsVoided = 1
WHERE Id = @id AND BranchId = @branchId AND SourceType = N'MANUAL' AND IsVoided = 0;", CloneParameters(parameters));
                break;
        }

        if (affected != 1)
        {
            throw new BusinessRuleException("این سند قبلاً باطل شده است.");
        }
    }

    /// <summary>
    /// سند معکوس سند باطل‌شده. سطرها از جمع خالص حساب‌های سند اصلی ساخته می‌شوند (شامل تعدیل‌های بعدی آن)،
    /// پس بعد از ابطال، مانده‌ی هر حساب برای این سند دقیقاً صفر می‌شود.
    /// </summary>
    private static async Task InsertVoidJournalAsync(SqlConnection conn, SqlTransaction tx, int branchId, VoidDraft voidDraft, DateTime now, int userId, CancellationToken ct)
    {
        var entryId = await InsertJournalHeaderAsync(conn, tx, branchId, voidDraft.Description, voidDraft.OccurredAt,
            SourceTypes.Void, voidDraft.Doc.Id, userId, now, null, ct);

        var filter = voidDraft.Doc.Kind switch
        {
            LedgerDocKind.Trade => "e.SourceType IN (N'TRADE', N'ADJUST') AND e.SourceId = @docId",
            LedgerDocKind.Opening => "e.SourceType = N'OPENING' AND e.SourceId = @docId",
            _ => "e.Id = @docId AND e.SourceType = N'MANUAL'",
        };
        var sql = $@"
INSERT INTO dbo.JournalLines (JournalEntryId, LineNumber, AccountCode, Debit, Credit)
SELECT @entryId, ROW_NUMBER() OVER (ORDER BY n.AccountCode), n.AccountCode,
       CASE WHEN n.Net < 0 THEN -n.Net ELSE 0 END,
       CASE WHEN n.Net > 0 THEN n.Net ELSE 0 END
FROM
(
    SELECT l.AccountCode, SUM(l.Debit - l.Credit) AS Net
    FROM dbo.JournalLines l
    INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
    WHERE e.BranchId = @branchId AND {filter}
    GROUP BY l.AccountCode
) n
WHERE n.Net <> 0;";
        await ExecuteAsync(conn, tx, ct, sql,
            new SqlParameter("@entryId", entryId),
            new SqlParameter("@branchId", branchId),
            new SqlParameter("@docId", voidDraft.Doc.Id));
    }

    private static async Task<long> InsertJournalAsync(SqlConnection conn, SqlTransaction tx, int branchId, JournalDraft journal, long? sourceId, int userId, DateTime now, CancellationToken ct)
    {
        var entryId = await InsertJournalHeaderAsync(conn, tx, branchId, journal.Description, journal.OccurredAt,
            journal.SourceType, sourceId, userId, now, journal.ReplacesId, ct);

        var lineNo = 0;
        foreach (var line in journal.Lines)
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
        return entryId;
    }

    private static async Task<long> InsertJournalHeaderAsync(SqlConnection conn, SqlTransaction tx, int branchId, string description, DateTime occurredAt,
        string sourceType, long? sourceId, int userId, DateTime createdAt, long? replacesId, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.JournalEntries (BranchId, OccurredAt, Description, SourceType, SourceId, CreatedBy, CreatedAt, ReplacesId)
OUTPUT INSERTED.Id
VALUES (@branchId, @occurredAt, @description, @sourceType, @sourceId, @userId, @createdAt, @replacesId);";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@occurredAt", occurredAt));
        cmd.Parameters.Add(new SqlParameter("@description", description));
        cmd.Parameters.Add(new SqlParameter("@sourceType", sourceType));
        cmd.Parameters.Add(RefIdParam("@sourceId", sourceId));
        cmd.Parameters.Add(new SqlParameter("@userId", userId));
        cmd.Parameters.Add(new SqlParameter("@createdAt", createdAt));
        cmd.Parameters.Add(RefIdParam("@replacesId", replacesId));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertTradeAsync(SqlConnection conn, SqlTransaction tx, int branchId, TradeDraft trade, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CurrencyTransactions
    (BranchId, TradeType, CurrencyCode, Amount, Rate, IrrAmount, CostIrr, ProfitIrr, FeeIrr, CustomerName, NationalCode, Note, OccurredAt, CreatedBy, ReplacesId)
OUTPUT INSERTED.Id
VALUES (@branchId, @tradeType, @code, @amount, @rate, @irr, @cost, @profit, @fee, @customer, @nationalCode, @note, @occurredAt, @userId, @replacesId);";
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
        cmd.Parameters.Add(RefIdParam("@replacesId", trade.ReplacesId));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertOpeningAsync(SqlConnection conn, SqlTransaction tx, int branchId, OpeningDraft opening, DateTime now, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.OpeningBalances (BranchId, CurrencyCode, Quantity, RateIrr, CostIrr, OccurredAt, CreatedBy, CreatedAt, ReplacesId)
OUTPUT INSERTED.Id
VALUES (@branchId, @code, @quantity, @rate, @cost, @occurredAt, @userId, @now, @replacesId);";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@code", opening.CurrencyCode));
        cmd.Parameters.Add(Money("@quantity", opening.Quantity));
        cmd.Parameters.Add(MoneyOrNull("@rate", opening.RateIrr));
        cmd.Parameters.Add(Money("@cost", opening.CostIrr));
        cmd.Parameters.Add(new SqlParameter("@occurredAt", opening.OccurredAt));
        cmd.Parameters.Add(new SqlParameter("@userId", opening.UserId));
        cmd.Parameters.Add(new SqlParameter("@now", now));
        cmd.Parameters.Add(RefIdParam("@replacesId", opening.ReplacesId));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// حرکت‌های صندوق را به‌تفکیک صندوق جمع می‌کند، مانده را یک‌بار با مقایسه‌ی مقدار قبلی به‌روز می‌کند
    /// و همه‌ی حرکت‌ها را با شناسه‌ی سند مرجع ثبت می‌کند.
    /// </summary>
    private static async Task ApplyCashMovementsAsync(SqlConnection conn, SqlTransaction tx, PostingDraft posting, long? newId, CancellationToken ct)
    {
        foreach (var group in posting.CashMovements.GroupBy(m => m.CurrencyCode))
        {
            var expected = group.First().ExpectedBalance;
            if (group.Any(m => m.ExpectedBalance != expected))
            {
                throw new InvalidOperationException("مانده‌ی قبلی یک صندوق در یک ثبت با هم برابر نیست.");
            }

            var box = await ReadCashBoxAsync(conn, tx, posting.BranchId, group.Key, ct);
            if (box.Balance != expected)
            {
                throw new ConcurrencyConflictException();
            }

            var delta = group.Sum(m => m.Delta);
            if (delta != 0m)
            {
                var affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.CashBoxes SET Balance = Balance + @delta, UpdatedAt = @now
WHERE Id = @boxId AND Balance = @expected;",
                    Money("@delta", delta),
                    new SqlParameter("@now", posting.Now),
                    new SqlParameter("@boxId", box.Id),
                    Money("@expected", expected));
                if (affected != 1)
                {
                    throw new ConcurrencyConflictException();
                }
            }

            foreach (var movement in group)
            {
                var refId = movement.Ref.Id != 0 ? movement.Ref.Id : ResolveId(0, newId);
                await ExecuteAsync(conn, tx, ct, @"
INSERT INTO dbo.CashMovements (CashBoxId, Amount, BalanceAfter, RefType, RefId, Description, OccurredAt, CreatedBy)
VALUES (@boxId, @amount, 0, @refType, @refId, @description, @occurredAt, @userId);",
                    new SqlParameter("@boxId", box.Id),
                    Money("@amount", movement.Delta),
                    new SqlParameter("@refType", movement.RefType),
                    RefIdParam("@refId", refId),
                    new SqlParameter("@description", movement.Description),
                    new SqlParameter("@occurredAt", movement.OccurredAt),
                    new SqlParameter("@userId", posting.UserId));
            }
        }
    }

    private static async Task<(int Id, decimal Balance)> ReadCashBoxAsync(SqlConnection conn, SqlTransaction tx, int branchId, string currencyCode, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT Id, Balance FROM dbo.CashBoxes WITH (UPDLOCK, HOLDLOCK) WHERE BranchId = @branchId AND CurrencyCode = @code;",
            conn,
            tx);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@code", currencyCode));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new BusinessRuleException($"صندوق {currencyCode} برای این شعبه یافت نشد.");
        }
        return (reader.GetInt32(0), reader.GetDecimal(1));
    }

    /// <summary>
    /// موجودی تراکمی هر حرکت صندوق را از روی همه‌ی حرکت‌های شعبه به ترتیب زمان دوباره حساب می‌کند.
    /// با تغییر تاریخچه (ثبت با تاریخ گذشته یا ابطال) ستون BalanceAfter باید از نو محاسبه شود.
    /// </summary>
    private static async Task RecalculateBalancesAfterAsync(SqlConnection conn, SqlTransaction tx, int branchId, CancellationToken ct)
    {
        const string sql = @"
UPDATE m
SET m.BalanceAfter = x.Running
FROM dbo.CashMovements m
INNER JOIN
(
    SELECT m2.Id,
           SUM(m2.Amount) OVER (PARTITION BY m2.CashBoxId ORDER BY m2.OccurredAt, m2.Id ROWS UNBOUNDED PRECEDING) AS Running
    FROM dbo.CashMovements m2
    INNER JOIN dbo.CashBoxes b2 ON b2.Id = m2.CashBoxId
    WHERE b2.BranchId = @branchId
) x ON x.Id = m.Id
WHERE m.BalanceAfter <> x.Running;";
        await ExecuteAsync(conn, tx, ct, sql, new SqlParameter("@branchId", branchId));
    }

    private static async Task InsertAuditAsync(SqlConnection conn, SqlTransaction tx, int userId, DateTime now, string action,
        string entityType, long? entityId, string details, CancellationToken ct)
    {
        await ExecuteAsync(conn, tx, ct, @"
INSERT INTO dbo.AuditLog (OccurredAt, UserId, Action, EntityType, EntityId, Details)
VALUES (@now, @userId, @action, @entityType, @entityId, @details);",
            new SqlParameter("@now", now),
            new SqlParameter("@userId", userId),
            new SqlParameter("@action", action),
            new SqlParameter("@entityType", entityType),
            RefIdParam("@entityId", entityId),
            new SqlParameter("@details", details));
    }

    private static long ResolveId(long id, long? newId) =>
        id != 0 ? id : newId ?? throw new InvalidOperationException("شناسه‌ی سند جدید پیش از ثبت سطرهای وابسته تعیین نشد.");

    private static string EntityTypeOf(LedgerDocKind kind) => kind switch
    {
        LedgerDocKind.Trade => SourceTypes.Trade,
        LedgerDocKind.Opening => SourceTypes.Opening,
        _ => SourceTypes.Manual,
    };

    private static SqlParameter MoneyOrNull(string name, decimal? value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = (object?)value ?? DBNull.Value };

    /// <summary>هر پارامتر فقط یک بار در یک دستور استفاده می‌شود؛ این تابع نسخه‌ی تازه‌ای از آرایه می‌سازد.</summary>
    private static SqlParameter[] CloneParameters(SqlParameter[] parameters) =>
        parameters.Select(p => new SqlParameter(p.ParameterName, p.Value)).ToArray();

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

    private sealed class JournalEntryBuilder
    {
        public JournalEntryBuilder(long id, DateTime occurredAt, string description, string sourceType, long? sourceId, bool isVoided, int branchId, string branchName)
        {
            Id = id;
            OccurredAt = occurredAt;
            Description = description;
            SourceType = sourceType;
            SourceId = sourceId;
            IsVoided = isVoided;
            BranchId = branchId;
            BranchName = branchName;
        }

        public long Id { get; }

        public DateTime OccurredAt { get; }

        public string Description { get; }

        public string SourceType { get; }

        public long? SourceId { get; }

        public bool IsVoided { get; }

        public int BranchId { get; }

        public string BranchName { get; }

        public List<JournalLineInfo> Lines { get; } = new();

        public JournalEntryInfo Build() => new(Id, OccurredAt, Description, SourceType, BranchId, BranchName, Lines, SourceId, IsVoided);
    }
}
