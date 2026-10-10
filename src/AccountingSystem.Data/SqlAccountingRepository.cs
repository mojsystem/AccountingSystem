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
    private const int ForeignKeyError = 547;
    private const long LedgerSeqFactor = 1000;

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
       t.CustomerName, t.NationalCode, t.Note, t.OccurredAt, u.Username, t.IsVoided, t.VoidedAt, vu.Username, t.VoidReason, t.CustomerId,
       t.SettlementMode, t.RateMode, t.CrossRate, t.CustomerOffsetIrr, t.SettlementCurrencyCode, t.PaymentMethod
FROM dbo.CurrencyTransactions t
INNER JOIN dbo.Branches br ON br.Id = t.BranchId
INNER JOIN dbo.Users u ON u.Id = t.CreatedBy
LEFT JOIN dbo.Users vu ON vu.Id = t.VoidedBy";

    private const string CashTransactionSelect = @"
SELECT t.Id, t.BranchId, br.Code, br.Name, t.Direction, t.CustomerId, c.CustomerCode, c.FullName,
       t.CurrencyCode, cur.Name, cur.DecimalPlaces, t.Amount, t.BalanceCurrencyCode, t.BalanceAmount,
       t.RateMode, t.RateIrr, t.IrrAmount, t.CostIrr, t.ProfitIrr, t.Note, t.OccurredAt,
       u.Username, t.IsVoided, t.VoidedAt, vu.Username, t.VoidReason
FROM dbo.CashTransactions t
INNER JOIN dbo.Branches br ON br.Id = t.BranchId
INNER JOIN dbo.Customers c ON c.Id = t.CustomerId
INNER JOIN dbo.Currencies cur ON cur.Code = t.CurrencyCode
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

                foreach (var preset in RolePresets.All)
                {
                    await InsertRoleAsync(conn, tx, branchId, preset.Name, preset.Permissions, null, now, ct);
                }

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

    public async Task AddAccountAsync(AccountRecord account, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync<bool>(async (conn, tx) =>
            {
                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive) VALUES (@code, @name, @type, @level, @parent, 0, @active);",
                    AccountParameters(account, null));
                await InsertAuditAsync(conn, tx, actorId, now, "ACCOUNT_CREATE", "ACCOUNT", null,
                    $"حساب {account.Code} «{account.Name}» ({AccountRules.LevelName(account.Level)}) ایجاد شد", ct);
                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException($"حسابی با کد «{account.Code}» از قبل وجود دارد.");
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyError)
        {
            throw new BusinessRuleException("حساب پدر معتبر نیست.");
        }
    }

    public async Task UpdateAccountAsync(string originalCode, AccountRecord account, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync<bool>(async (conn, tx) =>
            {
                var rows = await ExecuteAsync(conn, tx, ct,
                    "UPDATE dbo.Accounts SET Code = @code, Name = @name, AccountType = @type, Level = @level, ParentCode = @parent, IsActive = @active WHERE Code = @original;",
                    AccountParameters(account, originalCode));
                if (rows != 1)
                {
                    throw new BusinessRuleException("حساب مورد نظر پیدا نشد.");
                }
                var status = account.IsActive ? "فعال" : "غیرفعال";
                await InsertAuditAsync(conn, tx, actorId, now, "ACCOUNT_UPDATE", "ACCOUNT", null,
                    $"حساب {originalCode} ویرایش شد: کد {account.Code}، «{account.Name}»، {status}", ct);
                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException($"حسابی با کد «{account.Code}» از قبل وجود دارد.");
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyError)
        {
            throw new BusinessRuleException("حساب پدر معتبر نیست.");
        }
    }

    public async Task DeleteAccountAsync(string code, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync<bool>(async (conn, tx) =>
            {
                var rows = await ExecuteAsync(conn, tx, ct,
                    "DELETE FROM dbo.Accounts WHERE Code = @code AND IsSystem = 0;",
                    new SqlParameter("@code", code));
                if (rows != 1)
                {
                    throw new BusinessRuleException("حساب مورد نظر پیدا نشد یا حساب پایه‌ی موتور حسابداری است.");
                }
                await InsertAuditAsync(conn, tx, actorId, now, "ACCOUNT_DELETE", "ACCOUNT", null, $"حساب {code} حذف شد", ct);
                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyError)
        {
            throw new BusinessRuleException("این حساب در سند استفاده شده یا زیرمجموعه دارد و حذف نمی‌شود؛ به‌جای آن غیرفعال کنید.");
        }
    }

    private static SqlParameter[] AccountParameters(AccountRecord account, string? originalCode)
    {
        var parameters = new List<SqlParameter>
        {
            new SqlParameter("@code", account.Code),
            new SqlParameter("@name", account.Name),
            new SqlParameter("@type", account.AccountType),
            new SqlParameter("@level", account.Level),
            new SqlParameter("@parent", (object?)account.ParentCode ?? DBNull.Value),
            new SqlParameter("@active", account.IsActive),
        };
        if (originalCode is not null)
        {
            parameters.Add(new SqlParameter("@original", originalCode));
        }
        return parameters.ToArray();
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
                    "INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive) VALUES (@accountCode, @accountName, N'Asset', 4, N'1101', 1, 1);",
                    new SqlParameter("@accountCode", AccountCodes.ForeignCash(currency.Code)),
                    new SqlParameter("@accountName", "موجودی ارز - " + currency.Name));

                await ExecuteAsync(conn, tx, ct,
                    "INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive) VALUES (@accountCode, @accountName, N'Asset', 4, N'1102', 1, 1);",
                    new SqlParameter("@accountCode", AccountCodes.ForeignBank(currency.Code)),
                    new SqlParameter("@accountName", "موجودی بانکی - " + currency.Name));

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

    public async Task<IReadOnlyList<RateInfo>> GetRatesAtAsync(int branchId, DateTime asOf, CancellationToken ct = default)
    {
        const string sql = @"
WITH LatestRates AS
(
    SELECT r.BranchId, r.CurrencyCode, r.BuyRateIrr, r.SellRateIrr, r.CreatedAt,
           ROW_NUMBER() OVER (PARTITION BY r.BranchId, r.CurrencyCode ORDER BY r.CreatedAt DESC, r.Id DESC) AS RowNo
    FROM dbo.ExchangeRates r
    WHERE r.BranchId = @branchId AND r.CreatedAt <= @asOf
)
SELECT l.BranchId, br.Name, l.CurrencyCode, c.Name, l.BuyRateIrr, l.SellRateIrr, l.CreatedAt
FROM LatestRates l
INNER JOIN dbo.Branches br ON br.Id = l.BranchId
INNER JOIN dbo.Currencies c ON c.Code = l.CurrencyCode
WHERE l.RowNo = 1
ORDER BY l.CurrencyCode;";
        var result = new List<RateInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@asOf", asOf));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RateInfo(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2).Trim(), reader.GetString(3),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDateTime(6)));
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
            new SqlParameter("@now", OccurrenceRules.Truncate(now)),
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

    public async Task<IReadOnlyList<BankAccountInfo>> GetBankAccountsAsync(int? branchId, CancellationToken ct = default)
    {
        const string sql = @"
SELECT b.Id, b.BranchId, br.Name, b.Name, b.CurrencyCode, c.Name, c.DecimalPlaces,
       b.OpeningBalance, b.OpeningCostIrr, b.Balance, b.CostIrr, b.CreatedAt, u.Username
FROM dbo.BankAccounts b
INNER JOIN dbo.Branches br ON br.Id = b.BranchId
INNER JOIN dbo.Currencies c ON c.Code = b.CurrencyCode
INNER JOIN dbo.Users u ON u.Id = b.CreatedBy
WHERE (@branchId IS NULL OR b.BranchId = @branchId)
ORDER BY br.Id, b.CurrencyCode, b.Name, b.Id;";
        var result = new List<BankAccountInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new BankAccountInfo(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4).Trim(), reader.GetString(5), reader.GetByte(6),
                reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10),
                reader.GetDateTime(11), reader.GetString(12)));
        }
        return result;
    }

    public async Task<int> AddBankAccountAsync(BankAccountRecord account, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            return await WithTransactionAsync(async (conn, tx) =>
            {
                var nowAt = OccurrenceRules.Truncate(now);
                var id = Convert.ToInt32(await ScalarAsync(conn, tx, ct, @"
INSERT INTO dbo.BankAccounts
    (BranchId, Name, CurrencyCode, OpeningBalance, OpeningCostIrr, Balance, CostIrr, CreatedAt, CreatedBy, UpdatedAt)
OUTPUT INSERTED.Id
VALUES (@branchId, @name, @currencyCode, @openingBalance, @openingCost, @openingBalance, @openingCost, @now, @actorId, @now);",
                    new SqlParameter("@branchId", account.BranchId),
                    new SqlParameter("@name", account.Name),
                    new SqlParameter("@currencyCode", account.CurrencyCode),
                    Money("@openingBalance", account.OpeningBalance),
                    Money("@openingCost", account.OpeningCostIrr),
                    new SqlParameter("@now", nowAt),
                    new SqlParameter("@actorId", actorId)), CultureInfo.InvariantCulture);

                var ledgerVersionUpdated = await ExecuteAsync(conn, tx, ct,
                    "UPDATE dbo.Branches SET LedgerVersion = LedgerVersion + 1 WHERE Id = @branchId;",
                    new SqlParameter("@branchId", account.BranchId));
                if (ledgerVersionUpdated != 1)
                {
                    throw new BusinessRuleException("شعبه‌ی حساب بانکی در دسترس نیست.");
                }

                if (account.OpeningCostIrr > 0m)
                {
                    var accountCode = account.CurrencyCode == CurrencyCodes.Irr
                        ? AccountCodes.BankCash
                        : AccountCodes.ForeignBank(account.CurrencyCode);
                    var journal = new JournalDraft(
                        $"موجودی افتتاحیه‌ی حساب بانکی «{account.Name}» ({account.CurrencyCode})",
                        nowAt,
                        new[]
                        {
                            new JournalLineDraft(accountCode, account.OpeningCostIrr, 0m),
                            new JournalLineDraft(AccountCodes.OpeningCapital, 0m, account.OpeningCostIrr),
                        },
                        SourceTypes.BankOpening,
                        new DocRef(LedgerDocKind.Opening, id));
                    await InsertJournalAsync(conn, tx, account.BranchId, journal, id, actorId, nowAt, ct);
                }

                await InsertAuditAsync(conn, tx, actorId, nowAt, "BANK_ACCOUNT_CREATE", "BANK_ACCOUNT", id,
                    $"حساب بانکی «{account.Name}» برای شعبه‌ی {account.BranchId} و ارز {account.CurrencyCode} ایجاد شد؛ موجودی افتتاحیه {account.OpeningBalance}، بهای ریالی {account.OpeningCostIrr}", ct);
                return id;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("حساب بانکی با همین نام و ارز در این شعبه از قبل ثبت شده است.");
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyError)
        {
            throw new BusinessRuleException("شعبه، ارز یا کاربر انتخاب‌شده برای حساب بانکی معتبر نیست.");
        }
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
        const string sql = @"
SELECT a.Code, a.Name, a.AccountType, a.Level, a.ParentCode, a.IsSystem, a.IsActive,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.Accounts c WHERE c.ParentCode = a.Code) THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.JournalLines l WHERE l.AccountCode = a.Code) THEN 1 ELSE 0 END
FROM dbo.Accounts a
ORDER BY a.Code;";
        var result = new List<AccountInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AccountInfo(
                reader.GetString(0).Trim(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetByte(3),
                reader.IsDBNull(4) ? null : reader.GetString(4).Trim(),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetInt32(7) == 1,
                reader.GetInt32(8) == 1));
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
        await ReadCashTransactionEventsAsync(conn, branchId, events, active, ct);
        var storedBankPools = await ReadBankAccountOpeningEventsAsync(conn, branchId, events, ct);

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

        return new BranchLedger(branchId, version, events, active, irrBalance, pools, storedBankPools);
    }

    private static async Task<Dictionary<int, PoolBalance>> ReadBankAccountOpeningEventsAsync(SqlConnection conn, int branchId, List<LedgerEvent> events, CancellationToken ct)
    {
        const string sql = @"
SELECT Id, CurrencyCode, OpeningBalance, OpeningCostIrr, CreatedAt, Balance, CostIrr
FROM dbo.BankAccounts
WHERE BranchId = @branchId;";
        var result = new Dictionary<int, PoolBalance>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);
            var code = reader.GetString(1).Trim();
            var openingBalance = reader.GetDecimal(2);
            var openingCost = reader.GetDecimal(3);
            var createdAt = reader.GetDateTime(4);
            result.Add(id, new PoolBalance(reader.GetDecimal(5), reader.GetDecimal(6)));
            if (openingBalance > 0m || openingCost > 0m)
            {
                events.Add(new LedgerEvent(LedgerDocKind.Opening, -id, LedgerEventKind.Acquire, code, createdAt,
                    long.MinValue + id, openingBalance, openingCost, 0m, 0m, openingCost, 0m,
                    BankAccountId: id));
            }
        }
        return result;
    }

    private static async Task ReadTradeEventsAsync(SqlConnection conn, int branchId, List<LedgerEvent> events, HashSet<DocRef> active, CancellationToken ct)
    {
        const string sql = @"
SELECT t.Id, t.TradeType, t.CurrencyCode, t.Amount, t.IrrAmount, t.FeeIrr, t.CostIrr, t.ProfitIrr, t.OccurredAt, t.Seq
FROM dbo.CurrencyTransactions t
WHERE t.BranchId = @branchId AND t.IsVoided = 0;";
        await using (var cmd = new SqlCommand(sql, conn))
        {
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
                var seq = checked(reader.GetInt64(9) * LedgerSeqFactor);
                active.Add(new DocRef(LedgerDocKind.Trade, id));
                // جریان وجه نقد معامله از ریزتسویه‌ها بازسازی می‌شود؛ این رویداد فقط موجودی ارز اصلی را تغییر می‌دهد.
                events.Add(isBuy
                    ? new LedgerEvent(LedgerDocKind.Trade, id, LedgerEventKind.Acquire, code, occurredAt, seq,
                        amount, irr, fee, 0m, cost, profit)
                    : new LedgerEvent(LedgerDocKind.Trade, id, LedgerEventKind.Dispose, code, occurredAt, seq,
                        amount, irr, fee, 0m, cost, profit));
            }
        }

        const string settlementSql = @"
SELECT s.TradeId, s.LineNumber, s.Direction, s.CurrencyCode, s.Amount, s.IrrAmount, s.CostIrr, s.ProfitIrr,
       t.OccurredAt, t.Seq, t.PaymentMethod, s.BankAccountId
FROM dbo.CurrencyTransactionSettlements s
INNER JOIN dbo.CurrencyTransactions t ON t.Id = s.TradeId
WHERE t.BranchId = @branchId AND t.IsVoided = 0
ORDER BY t.OccurredAt, t.Seq, s.LineNumber;";
        await using var settlementCommand = new SqlCommand(settlementSql, conn);
        settlementCommand.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var settlementReader = await settlementCommand.ExecuteReaderAsync(ct);
        while (await settlementReader.ReadAsync(ct))
        {
            var tradeId = settlementReader.GetInt64(0);
            var lineNumber = settlementReader.GetInt32(1);
            var direction = settlementReader.GetString(2) == "PAY"
                ? TradeSettlementDirection.Payment
                : TradeSettlementDirection.Receipt;
            var code = settlementReader.GetString(3).Trim();
            var amount = settlementReader.GetDecimal(4);
            var valueIrr = settlementReader.GetDecimal(5);
            var cost = settlementReader.GetDecimal(6);
            var profit = settlementReader.GetDecimal(7);
            var occurredAt = settlementReader.GetDateTime(8);
            var seq = checked(settlementReader.GetInt64(9) * LedgerSeqFactor + lineNumber);
            var method = ParsePaymentMethod(settlementReader.GetString(10));
            var bankAccountId = settlementReader.IsDBNull(11) ? (int?)null : settlementReader.GetInt32(11);
            var eventKind = direction == TradeSettlementDirection.Payment ? LedgerEventKind.Dispose : LedgerEventKind.Acquire;
            if (method == TradePaymentMethod.BankTransfer)
            {
                if (bankAccountId is null)
                {
                    throw new InvalidOperationException($"سطر حواله‌ی معامله‌ی {tradeId} حساب بانکی ندارد.");
                }
                events.Add(new LedgerEvent(LedgerDocKind.Trade, tradeId, eventKind, code, occurredAt, seq,
                    amount, valueIrr, 0m, 0m, cost, profit, lineNumber, bankAccountId));
            }
            else if (method == TradePaymentMethod.Cash)
            {
                if (code == CurrencyCodes.Irr)
                {
                    var delta = direction == TradeSettlementDirection.Payment ? -amount : amount;
                    events.Add(new LedgerEvent(LedgerDocKind.Trade, tradeId, LedgerEventKind.CashOnly, code, occurredAt, seq,
                        0m, valueIrr, 0m, delta, cost, profit, lineNumber));
                }
                else
                {
                    events.Add(new LedgerEvent(LedgerDocKind.Trade, tradeId, eventKind, code, occurredAt, seq,
                        amount, valueIrr, 0m, 0m, cost, profit, lineNumber));
                }
            }
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
            var seq = checked(reader.GetInt64(5) * LedgerSeqFactor);
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
            var seq = checked(reader.GetInt64(2) * LedgerSeqFactor);
            var irrDelta = reader.GetDecimal(3);
            active.Add(new DocRef(LedgerDocKind.Manual, id));
            if (irrDelta != 0m)
            {
                events.Add(new LedgerEvent(LedgerDocKind.Manual, id, LedgerEventKind.CashOnly, CurrencyCodes.Irr,
                    occurredAt, seq, 0m, 0m, 0m, irrDelta, 0m, 0m));
            }
        }
    }

    private static async Task ReadCashTransactionEventsAsync(
        SqlConnection conn,
        int branchId,
        List<LedgerEvent> events,
        HashSet<DocRef> active,
        CancellationToken ct)
    {
        const string sql = @"
SELECT Id, Direction, CurrencyCode, Amount, IrrAmount, CostIrr, ProfitIrr, OccurredAt, Seq
FROM dbo.CashTransactions
WHERE BranchId = @branchId AND IsVoided = 0;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var isReceipt = reader.GetString(1) == "RECEIVE";
            var code = reader.GetString(2).Trim();
            var amount = reader.GetDecimal(3);
            var valueIrr = reader.GetDecimal(4);
            var cost = reader.GetDecimal(5);
            var profit = reader.GetDecimal(6);
            var occurredAt = reader.GetDateTime(7);
            var seq = checked(reader.GetInt64(8) * LedgerSeqFactor);
            var reference = new DocRef(LedgerDocKind.CashTransaction, id);
            active.Add(reference);

            events.Add(code == CurrencyCodes.Irr
                ? new LedgerEvent(LedgerDocKind.CashTransaction, id, LedgerEventKind.CashOnly, code, occurredAt, seq,
                    0m, valueIrr, 0m, isReceipt ? amount : -amount, cost, profit)
                : new LedgerEvent(LedgerDocKind.CashTransaction, id,
                    isReceipt ? LedgerEventKind.Acquire : LedgerEventKind.Dispose,
                    code, occurredAt, seq, amount, valueIrr, 0m, 0m, cost, profit));
        }
    }

    public async Task UpdateTradeDetailsAsync(long tradeId, int branchId, int customerId, string customerName, string? nationalCode, string? note, int userId, DateTime now, CancellationToken ct = default)
    {
        await WithTransactionAsync<bool>(async (conn, tx) =>
        {
            var affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.CurrencyTransactions
SET CustomerId = @customerId, CustomerName = @customer, NationalCode = @nationalCode, Note = @note
WHERE Id = @id AND BranchId = @branchId AND IsVoided = 0;",
                new SqlParameter("@customerId", customerId),
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
                await InsertTradeSettlementsAsync(conn, tx, ResolveId(0, newId), trade.Settlements, ct);
            }
            else if (posting.Opening is { } opening)
            {
                newId = await InsertOpeningAsync(conn, tx, posting.BranchId, opening, posting.Now, ct);
            }
            else if (posting.CashTransaction is { } cashTransaction)
            {
                newId = await InsertCashTransactionAsync(conn, tx, posting.BranchId, cashTransaction, posting.Now, ct);
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

            foreach (var update in posting.SettlementCostUpdates ?? Array.Empty<TradeSettlementCostUpdate>())
            {
                var affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE s SET CostIrr = @cost, ProfitIrr = @profit
FROM dbo.CurrencyTransactionSettlements s
INNER JOIN dbo.CurrencyTransactions t ON t.Id = s.TradeId
WHERE s.TradeId = @tradeId AND s.LineNumber = @lineNumber AND t.BranchId = @branchId AND t.IsVoided = 0;",
                    Money("@cost", update.CostIrr),
                    Money("@profit", update.ProfitIrr),
                    new SqlParameter("@tradeId", update.TradeId),
                    new SqlParameter("@lineNumber", update.LineNumber),
                    new SqlParameter("@branchId", posting.BranchId));
                if (affected != 1)
                {
                    throw new ConcurrencyConflictException();
                }
            }

            foreach (var update in posting.CashTransactionCostUpdates ?? Array.Empty<CashTransactionCostUpdate>())
            {
                var affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.CashTransactions SET CostIrr = @cost, ProfitIrr = @profit
WHERE Id = @id AND BranchId = @branchId AND IsVoided = 0;",
                    Money("@cost", update.CostIrr),
                    Money("@profit", update.ProfitIrr),
                    new SqlParameter("@id", update.CashTransactionId),
                    new SqlParameter("@branchId", posting.BranchId));
                if (affected != 1)
                {
                    throw new ConcurrencyConflictException();
                }
            }

            await ApplyCashMovementsAsync(conn, tx, posting, newId, ct);
            await ApplyBankAccountUpdatesAsync(conn, tx, posting, ct);

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

            case LedgerDocKind.CashTransaction:
                affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.CashTransactions
SET IsVoided = 1, VoidedAt = @now, VoidedBy = @userId, VoidReason = @reason
WHERE Id = @id AND BranchId = @branchId AND IsVoided = 0;", CloneParameters(parameters));
                await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.JournalEntries SET IsVoided = 1
WHERE BranchId = @branchId
  AND SourceType IN (N'CASH_RECEIPT', N'CASH_PAYMENT', N'CASH_ADJUST')
  AND SourceId = @id;", CloneParameters(parameters));
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
            LedgerDocKind.CashTransaction => "e.SourceType IN (N'CASH_RECEIPT', N'CASH_PAYMENT', N'CASH_ADJUST') AND e.SourceId = @docId",
            _ => "e.Id = @docId AND e.SourceType = N'MANUAL'",
        };
        var sql = $@"
INSERT INTO dbo.JournalLines
    (JournalEntryId, LineNumber, AccountCode, Debit, Credit, CustomerId, CustomerBalanceCurrencyCode, CustomerBalanceDelta)
SELECT @entryId, ROW_NUMBER() OVER (ORDER BY n.AccountCode, n.CustomerId, n.CustomerBalanceCurrencyCode), n.AccountCode,
       CASE WHEN n.Net < 0 THEN -n.Net ELSE 0 END,
       CASE WHEN n.Net > 0 THEN n.Net ELSE 0 END,
       n.CustomerId, n.CustomerBalanceCurrencyCode,
       CASE WHEN n.CustomerBalanceDelta IS NULL THEN NULL ELSE -n.CustomerBalanceDelta END
FROM
(
    SELECT l.AccountCode, l.CustomerId, l.CustomerBalanceCurrencyCode,
           SUM(l.Debit - l.Credit) AS Net,
           SUM(l.CustomerBalanceDelta) AS CustomerBalanceDelta
    FROM dbo.JournalLines l
    INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
    WHERE e.BranchId = @branchId AND {filter}
    GROUP BY l.AccountCode, l.CustomerId, l.CustomerBalanceCurrencyCode
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
            var isCustomerControl = line.CustomerId is not null
                && (line.AccountCode == AccountCodes.CustomerReceivable || line.AccountCode == AccountCodes.CustomerPayable);
            var balanceCurrency = line.CustomerBalanceCurrencyCode ?? (isCustomerControl ? CurrencyCodes.Irr : null);
            var balanceDelta = line.CustomerBalanceDelta ?? (isCustomerControl ? line.Debit - line.Credit : (decimal?)null);
            await ExecuteAsync(conn, tx, ct,
                "INSERT INTO dbo.JournalLines (JournalEntryId, LineNumber, AccountCode, Debit, Credit, CustomerId, CustomerBalanceCurrencyCode, CustomerBalanceDelta) " +
                "VALUES (@entryId, @lineNo, @account, @debit, @credit, @customerId, @balanceCurrency, @balanceDelta);",
                new SqlParameter("@entryId", entryId),
                new SqlParameter("@lineNo", lineNo),
                new SqlParameter("@account", line.AccountCode),
                Money("@debit", line.Debit),
                Money("@credit", line.Credit),
                NullableInt("@customerId", line.CustomerId),
                new SqlParameter("@balanceCurrency", (object?)balanceCurrency ?? DBNull.Value),
                MoneyOrNull("@balanceDelta", balanceDelta));
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
    (BranchId, TradeType, CurrencyCode, Amount, Rate, IrrAmount, CostIrr, ProfitIrr, FeeIrr, CustomerId, CustomerName, NationalCode, Note, OccurredAt, CreatedBy, ReplacesId,
     SettlementCurrencyCode, SettlementMode, RateMode, CrossRate, CustomerOffsetIrr, PaymentMethod)
OUTPUT INSERTED.Id
VALUES (@branchId, @tradeType, @code, @amount, @rate, @irr, @cost, @profit, @fee, @customerId, @customer, @nationalCode, @note, @occurredAt, @userId, @replacesId,
        @settlementCode, @settlementMode, @rateMode, @crossRate, @customerOffset, @paymentMethod);";
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
        cmd.Parameters.Add(new SqlParameter("@customerId", (object?)trade.CustomerId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@customer", (object?)trade.CustomerName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nationalCode", (object?)trade.NationalCode ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@note", (object?)trade.Note ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@occurredAt", trade.OccurredAt));
        cmd.Parameters.Add(new SqlParameter("@userId", trade.UserId));
        cmd.Parameters.Add(RefIdParam("@replacesId", trade.ReplacesId));
        cmd.Parameters.Add(new SqlParameter("@settlementCode", (object?)trade.SettlementCurrencyCode ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@settlementMode", trade.SettlementMode switch
        {
            TradeSettlementMode.Direct => "DIRECT",
            TradeSettlementMode.Split => "SPLIT",
            TradeSettlementMode.CustomerAccount => "ACCOUNT",
            _ => throw new InvalidOperationException("روش تسویه‌ی ناشناخته است."),
        }));
        cmd.Parameters.Add(new SqlParameter("@rateMode", trade.RateMode switch
        {
            TradeRateMode.Direct => "DIRECT",
            TradeRateMode.Derived => "DERIVED",
            _ => throw new InvalidOperationException("روش نرخ ناشناخته است."),
        }));
        cmd.Parameters.Add(PreciseRate("@crossRate", trade.CrossRate));
        cmd.Parameters.Add(Money("@customerOffset", trade.CustomerOffsetIrr));
        cmd.Parameters.Add(new SqlParameter("@paymentMethod", PaymentMethodCode(trade.PaymentMethod)));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task InsertTradeSettlementsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long tradeId,
        IReadOnlyList<TradeSettlementDraft>? settlements,
        CancellationToken ct)
    {
        if (settlements is null || settlements.Count == 0)
        {
            return;
        }

        const string sql = @"
INSERT INTO dbo.CurrencyTransactionSettlements
    (TradeId, LineNumber, Direction, CurrencyCode, Amount, RateIrr, IrrAmount, CostIrr, ProfitIrr, BankAccountId)
VALUES (@tradeId, @lineNumber, @direction, @currencyCode, @amount, @rateIrr, @irrAmount, @cost, @profit, @bankAccountId);";
        foreach (var settlement in settlements.OrderBy(s => s.LineNumber))
        {
            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add(new SqlParameter("@tradeId", tradeId));
            cmd.Parameters.Add(new SqlParameter("@lineNumber", settlement.LineNumber));
            cmd.Parameters.Add(new SqlParameter("@direction", settlement.Direction == TradeSettlementDirection.Payment ? "PAY" : "RECEIVE"));
            cmd.Parameters.Add(new SqlParameter("@currencyCode", settlement.CurrencyCode));
            cmd.Parameters.Add(Money("@amount", settlement.Amount));
            cmd.Parameters.Add(Money("@rateIrr", settlement.RateIrr));
            cmd.Parameters.Add(Money("@irrAmount", settlement.IrrAmount));
            cmd.Parameters.Add(Money("@cost", settlement.CostIrr));
            cmd.Parameters.Add(Money("@profit", settlement.ProfitIrr));
            cmd.Parameters.Add(NullableInt("@bankAccountId", settlement.BankAccountId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<long> InsertCashTransactionAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int branchId,
        CashTransactionDraft transaction,
        DateTime now,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CashTransactions
    (BranchId, Direction, CustomerId, CurrencyCode, Amount, BalanceCurrencyCode, BalanceAmount,
     RateMode, RateIrr, IrrAmount, CostIrr, ProfitIrr, Note, OccurredAt, CreatedBy, CreatedAt, ReplacesId)
OUTPUT INSERTED.Id
VALUES (@branchId, @direction, @customerId, @currencyCode, @amount, @balanceCurrencyCode, @balanceAmount,
        @rateMode, @rateIrr, @irrAmount, @costIrr, @profitIrr, @note, @occurredAt, @userId, @createdAt, @replacesId);";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@direction", transaction.Direction == CashTransactionDirection.Receipt ? "RECEIVE" : "PAY"));
        cmd.Parameters.Add(new SqlParameter("@customerId", transaction.CustomerId));
        cmd.Parameters.Add(new SqlParameter("@currencyCode", transaction.CurrencyCode));
        cmd.Parameters.Add(Money("@amount", transaction.Amount));
        cmd.Parameters.Add(new SqlParameter("@balanceCurrencyCode", transaction.BalanceCurrencyCode));
        cmd.Parameters.Add(Money("@balanceAmount", transaction.BalanceAmount));
        cmd.Parameters.Add(new SqlParameter("@rateMode", transaction.RateMode == TradeRateMode.Direct ? "DIRECT" : "DERIVED"));
        cmd.Parameters.Add(Money("@rateIrr", transaction.RateIrr));
        cmd.Parameters.Add(Money("@irrAmount", transaction.IrrAmount));
        cmd.Parameters.Add(Money("@costIrr", transaction.CostIrr));
        cmd.Parameters.Add(Money("@profitIrr", transaction.ProfitIrr));
        cmd.Parameters.Add(new SqlParameter("@note", (object?)transaction.Note ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@occurredAt", transaction.OccurredAt));
        cmd.Parameters.Add(new SqlParameter("@userId", transaction.UserId));
        cmd.Parameters.Add(new SqlParameter("@createdAt", now));
        cmd.Parameters.Add(RefIdParam("@replacesId", transaction.ReplacesId));
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

    private static async Task ApplyBankAccountUpdatesAsync(SqlConnection conn, SqlTransaction tx, PostingDraft posting, CancellationToken ct)
    {
        foreach (var update in posting.BankAccountUpdates ?? Array.Empty<BankAccountBalanceDraft>())
        {
            if (update.NewBalance < 0m || update.NewCostIrr < 0m)
            {
                throw new BusinessRuleException("موجودی حساب بانکی پس از ثبت نمی‌تواند منفی باشد.");
            }
            var current = await ReadBankAccountBalanceAsync(conn, tx, posting.BranchId, update.BankAccountId, ct);
            if (current.Balance != update.ExpectedBalance || current.CostIrr != update.ExpectedCostIrr)
            {
                throw new ConcurrencyConflictException();
            }
            var affected = await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.BankAccounts
SET Balance = @newBalance, CostIrr = @newCost, UpdatedAt = @now
WHERE Id = @id AND BranchId = @branchId AND Balance = @expectedBalance AND CostIrr = @expectedCost;",
                Money("@newBalance", update.NewBalance),
                Money("@newCost", update.NewCostIrr),
                new SqlParameter("@now", posting.Now),
                new SqlParameter("@id", update.BankAccountId),
                new SqlParameter("@branchId", posting.BranchId),
                Money("@expectedBalance", update.ExpectedBalance),
                Money("@expectedCost", update.ExpectedCostIrr));
            if (affected != 1)
            {
                throw new ConcurrencyConflictException();
            }
        }
    }

    private static async Task<(decimal Balance, decimal CostIrr)> ReadBankAccountBalanceAsync(
        SqlConnection conn, SqlTransaction tx, int branchId, int bankAccountId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(@"
SELECT Balance, CostIrr
FROM dbo.BankAccounts WITH (UPDLOCK, HOLDLOCK)
WHERE Id = @id AND BranchId = @branchId;", conn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", bankAccountId));
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new BusinessRuleException("حساب بانکی حواله در شعبه‌ی انتخابی یافت نشد.");
        }
        return (reader.GetDecimal(0), reader.GetDecimal(1));
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
        LedgerDocKind.CashTransaction => SourceTypes.CashTransaction,
        _ => SourceTypes.Manual,
    };

    private static SqlParameter MoneyOrNull(string name, decimal? value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = (object?)value ?? DBNull.Value };

    /// <summary>هر پارامتر فقط یک بار در یک دستور استفاده می‌شود؛ این تابع نسخه‌ی تازه‌ای از آرایه می‌سازد.</summary>
    private static SqlParameter[] CloneParameters(SqlParameter[] parameters) =>
        parameters.Select(p => new SqlParameter(p.ParameterName, p.Value)).ToArray();

    public async Task<IReadOnlyList<CashTransactionInfo>> GetCashTransactionsAsync(
        int? branchId,
        DateTime fromInclusive,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        var sql = CashTransactionSelect + @"
WHERE t.OccurredAt >= @from AND t.OccurredAt < @to AND (@branchId IS NULL OR t.BranchId = @branchId)
ORDER BY t.OccurredAt DESC, t.Id DESC;";
        var result = new List<CashTransactionInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadCashTransaction(reader));
        }
        return result;
    }

    public async Task<CashTransactionInfo?> GetCashTransactionAsync(long cashTransactionId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(CashTransactionSelect + " WHERE t.Id = @id;", conn);
        cmd.Parameters.Add(new SqlParameter("@id", cashTransactionId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCashTransaction(reader) : null;
    }

    public async Task<IReadOnlyList<TradeInfo>> GetTradesAsync(int? branchId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        var sql = TradeSelect + @"
WHERE t.OccurredAt >= @from AND t.OccurredAt < @to AND (@branchId IS NULL OR t.BranchId = @branchId)
ORDER BY t.OccurredAt DESC, t.Id DESC;";
        var result = new List<TradeInfo>();
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
            cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
            cmd.Parameters.Add(NullableInt("@branchId", branchId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(ReadTrade(reader));
            }
        }

        var settlements = await ReadTradeSettlementsAsync(conn, branchId, fromInclusive, toExclusive, ct);
        return result.Select(trade => trade with
        {
            Settlements = settlements.TryGetValue(trade.Id, out var lines) ? lines : Array.Empty<TradeSettlementInfo>(),
        }).ToList();
    }

    private static async Task<Dictionary<long, IReadOnlyList<TradeSettlementInfo>>> ReadTradeSettlementsAsync(
        SqlConnection conn,
        int? branchId,
        DateTime fromInclusive,
        DateTime toExclusive,
        CancellationToken ct)
    {
        const string sql = @"
SELECT s.TradeId, s.LineNumber, s.Direction, s.CurrencyCode, s.Amount, s.RateIrr, s.IrrAmount, s.CostIrr, s.ProfitIrr,
       s.BankAccountId, b.Name
FROM dbo.CurrencyTransactionSettlements s
LEFT JOIN dbo.BankAccounts b ON b.Id = s.BankAccountId
INNER JOIN dbo.CurrencyTransactions t ON t.Id = s.TradeId
WHERE t.OccurredAt >= @from AND t.OccurredAt < @to AND (@branchId IS NULL OR t.BranchId = @branchId)
ORDER BY s.TradeId, s.LineNumber;";
        var lists = new Dictionary<long, List<TradeSettlementInfo>>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@from", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@to", toExclusive));
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tradeId = reader.GetInt64(0);
            if (!lists.TryGetValue(tradeId, out var lines))
            {
                lines = new List<TradeSettlementInfo>();
                lists.Add(tradeId, lines);
            }
            lines.Add(ReadTradeSettlement(reader, 1));
        }
        return lists.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TradeSettlementInfo>)pair.Value);
    }

    private static async Task<IReadOnlyList<TradeSettlementInfo>> ReadTradeSettlementsAsync(SqlConnection conn, long tradeId, CancellationToken ct)
    {
        const string sql = @"
SELECT s.LineNumber, s.Direction, s.CurrencyCode, s.Amount, s.RateIrr, s.IrrAmount, s.CostIrr, s.ProfitIrr,
       s.BankAccountId, b.Name
FROM dbo.CurrencyTransactionSettlements s
LEFT JOIN dbo.BankAccounts b ON b.Id = s.BankAccountId
WHERE s.TradeId = @tradeId
ORDER BY s.LineNumber;";
        var result = new List<TradeSettlementInfo>();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@tradeId", tradeId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadTradeSettlement(reader, 0));
        }
        return result;
    }

    private static TradeSettlementInfo ReadTradeSettlement(SqlDataReader reader, int offset) => new(
        reader.GetInt32(offset),
        reader.GetString(offset + 1) == "PAY" ? TradeSettlementDirection.Payment : TradeSettlementDirection.Receipt,
        reader.GetString(offset + 2).Trim(),
        reader.GetDecimal(offset + 3),
        reader.GetDecimal(offset + 4),
        reader.GetDecimal(offset + 5),
        reader.GetDecimal(offset + 6),
        reader.GetDecimal(offset + 7),
        reader.IsDBNull(offset + 8) ? (int?)null : reader.GetInt32(offset + 8),
        ReadNullableString(reader, offset + 9));

    public async Task<TradeInfo?> GetTradeAsync(long tradeId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await QueryTradeAsync(conn, tradeId, ct);
    }

    public async Task<CustomerAccountBalance> GetCustomerAccountBalanceAsync(
        int branchId,
        int customerId,
        DateTime asOf,
        long? excludeTradeId = null,
        long? excludeCashTransactionId = null,
        CancellationToken ct = default)
    {
        const string sql = @"
;WITH TradeLineage AS
(
    SELECT Id, ReplacesId
    FROM dbo.CurrencyTransactions
    WHERE Id = @excludeTradeId
    UNION ALL
    SELECT parent.Id, parent.ReplacesId
    FROM dbo.CurrencyTransactions parent
    INNER JOIN TradeLineage child ON child.ReplacesId = parent.Id
)
SELECT COALESCE(SUM(l.CustomerBalanceDelta), 0)
FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
WHERE e.BranchId = @branchId AND e.OccurredAt <= @asOf
  AND l.CustomerId = @customerId
  AND l.CustomerBalanceCurrencyCode = N'IRR'
  AND l.CustomerBalanceDelta IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM TradeLineage lineage
      WHERE e.SourceId = lineage.Id AND e.SourceType IN (N'TRADE', N'ADJUST', N'VOID')
  )
  AND (@excludeCashTransactionId IS NULL OR NOT (e.SourceId = @excludeCashTransactionId AND e.SourceType IN (N'CASH_RECEIPT', N'CASH_PAYMENT')))
OPTION (MAXRECURSION 32767);";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@customerId", customerId));
        cmd.Parameters.Add(new SqlParameter("@asOf", asOf));
        cmd.Parameters.Add(RefIdParam("@excludeTradeId", excludeTradeId));
        cmd.Parameters.Add(RefIdParam("@excludeCashTransactionId", excludeCashTransactionId));
        var value = Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return new CustomerAccountBalance(branchId, customerId, Math.Max(0m, value), Math.Max(0m, -value));
    }

    public async Task<IReadOnlyList<CustomerCurrencyBalance>> GetCustomerCurrencyBalancesAsync(
        int branchId,
        int customerId,
        DateTime asOf,
        long? excludeTradeId = null,
        long? excludeCashTransactionId = null,
        CancellationToken ct = default)
    {
        const string sql = @"
;WITH TradeLineage AS
(
    SELECT Id, ReplacesId
    FROM dbo.CurrencyTransactions
    WHERE Id = @excludeTradeId
    UNION ALL
    SELECT parent.Id, parent.ReplacesId
    FROM dbo.CurrencyTransactions parent
    INNER JOIN TradeLineage child ON child.ReplacesId = parent.Id
)
SELECT l.CustomerBalanceCurrencyCode, c.Name, c.DecimalPlaces, SUM(l.CustomerBalanceDelta)
FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
INNER JOIN dbo.Currencies c ON c.Code = l.CustomerBalanceCurrencyCode
WHERE e.BranchId = @branchId AND e.OccurredAt <= @asOf
  AND l.CustomerId = @customerId
  AND l.CustomerBalanceCurrencyCode IS NOT NULL
  AND l.CustomerBalanceDelta IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM TradeLineage lineage
      WHERE e.SourceId = lineage.Id AND e.SourceType IN (N'TRADE', N'ADJUST', N'VOID')
  )
  AND (@excludeCashTransactionId IS NULL OR NOT (e.SourceId = @excludeCashTransactionId AND e.SourceType IN (N'CASH_RECEIPT', N'CASH_PAYMENT')))
GROUP BY l.CustomerBalanceCurrencyCode, c.Name, c.DecimalPlaces
HAVING SUM(l.CustomerBalanceDelta) <> 0
ORDER BY l.CustomerBalanceCurrencyCode
OPTION (MAXRECURSION 32767);";
        var result = new List<CustomerCurrencyBalance>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@customerId", customerId));
        cmd.Parameters.Add(new SqlParameter("@asOf", asOf));
        cmd.Parameters.Add(RefIdParam("@excludeTradeId", excludeTradeId));
        cmd.Parameters.Add(RefIdParam("@excludeCashTransactionId", excludeCashTransactionId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CustomerCurrencyBalance(
                branchId,
                customerId,
                reader.GetString(0).Trim(),
                reader.GetString(1),
                reader.GetByte(2),
                reader.GetDecimal(3)));
        }
        return result;
    }

    public async Task<IReadOnlyList<CustomerBalanceReportRow>> GetCustomerBalancesAsync(
        int? branchId,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.Id, c.CustomerCode, c.FullName,
       COALESCE(SUM(CASE WHEN e.Id IS NULL THEN CONVERT(DECIMAL(19,4), 0) ELSE l.Debit - l.Credit END), 0)
FROM dbo.Customers c
LEFT JOIN dbo.JournalLines l
  ON l.CustomerId = c.Id AND l.AccountCode IN (N'1201', N'2101')
 AND l.CustomerBalanceCurrencyCode = N'IRR'
LEFT JOIN dbo.JournalEntries e
  ON e.Id = l.JournalEntryId
 AND e.OccurredAt < @toExclusive
 AND (@branchId IS NULL OR e.BranchId = @branchId)
GROUP BY c.Id, c.CustomerCode, c.FullName
ORDER BY c.FullName, c.CustomerCode;";
        var result = new List<CustomerBalanceReportRow>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@toExclusive", toExclusive));
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CustomerBalanceReportRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDecimal(3)));
        }
        return result;
    }

    public async Task<CustomerLedgerData> GetCustomerLedgerAsync(
        int customerId,
        int? branchId,
        DateTime fromInclusive,
        DateTime toExclusive,
        CancellationToken ct = default)
    {
        const string sql = @"
SELECT COALESCE(SUM(l.Debit - l.Credit), 0)
FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
WHERE l.CustomerId = @customerId AND l.AccountCode IN (N'1201', N'2101')
  AND l.CustomerBalanceCurrencyCode = N'IRR'
  AND e.OccurredAt < @fromInclusive
  AND (@branchId IS NULL OR e.BranchId = @branchId);

SELECT e.Id, e.SourceId, e.OccurredAt, e.BranchId, b.Name, e.SourceType, e.Description,
       l.LineNumber, l.AccountCode, l.Debit, l.Credit, e.IsVoided
FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
INNER JOIN dbo.Branches b ON b.Id = e.BranchId
WHERE l.CustomerId = @customerId AND l.AccountCode IN (N'1201', N'2101')
  AND l.CustomerBalanceCurrencyCode = N'IRR'
  AND e.OccurredAt >= @fromInclusive AND e.OccurredAt < @toExclusive
  AND (@branchId IS NULL OR e.BranchId = @branchId)
ORDER BY e.OccurredAt, e.Seq, e.Id, l.LineNumber;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@customerId", customerId));
        cmd.Parameters.Add(NullableInt("@branchId", branchId));
        cmd.Parameters.Add(new SqlParameter("@fromInclusive", fromInclusive));
        cmd.Parameters.Add(new SqlParameter("@toExclusive", toExclusive));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var openingBalance = await reader.ReadAsync(ct) ? reader.GetDecimal(0) : 0m;
        if (!await reader.NextResultAsync(ct))
        {
            return new CustomerLedgerData(openingBalance, Array.Empty<CustomerLedgerLineInfo>());
        }

        var lines = new List<CustomerLedgerLineInfo>();
        var balance = openingBalance;
        while (await reader.ReadAsync(ct))
        {
            var debit = reader.GetDecimal(9);
            var credit = reader.GetDecimal(10);
            balance += debit - credit;
            lines.Add(new CustomerLedgerLineInfo(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetDateTime(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetString(8),
                debit,
                credit,
                reader.GetBoolean(11),
                balance));
        }
        return new CustomerLedgerData(openingBalance, lines);
    }

    /// <summary>
    /// دسترسی کاربر (نقش سیستمی، فعال بودن، شعبه‌ی اصلی و عضویت‌ها با نقش هر شعبه)؛ در هر بار فراخوانی تازه خوانده می‌شود.
    /// </summary>
    public async Task<UserAccess?> GetUserAccessAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var all = await ReadAccessAsync(conn, userId, ct);
        return all.TryGetValue(userId, out var access) ? access : null;
    }

    public async Task<IReadOnlyDictionary<int, UserAccess>> GetAllUserAccessAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await ReadAccessAsync(conn, null, ct);
    }

    public async Task<IReadOnlyList<AccessRoleInfo>> GetRolesAsync(int branchId, CancellationToken ct = default)
    {
        const string sql = @"
SELECT r.Id, r.Name, p.Permission,
       (SELECT COUNT(*) FROM dbo.UserBranchRoles m WHERE m.RoleId = r.Id) AS AssignedUsers
FROM dbo.AccessRoles r
LEFT JOIN dbo.AccessRolePermissions p ON p.RoleId = r.Id
WHERE r.BranchId = @branchId;";
        var roles = new Dictionary<int, (string Name, int Assigned, HashSet<Permission> Permissions)>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@branchId", branchId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var roleId = reader.GetInt32(0);
            if (!roles.TryGetValue(roleId, out var role))
            {
                role = (reader.GetString(1), reader.GetInt32(3), new HashSet<Permission>());
                roles[roleId] = role;
            }
            if (!reader.IsDBNull(2) && PermissionCodes.TryParse(reader.GetString(2), out var permission))
            {
                role.Permissions.Add(permission);
            }
        }
        return roles
            .OrderBy(pair => pair.Value.Name)
            .Select(pair => new AccessRoleInfo(pair.Key, branchId, pair.Value.Name, pair.Value.Permissions, pair.Value.Assigned))
            .ToList();
    }

    public async Task<int> CreateRoleAsync(int branchId, string name, IReadOnlyCollection<Permission> permissions, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            return await WithTransactionAsync(async (conn, tx) =>
            {
                var roleId = await InsertRoleAsync(conn, tx, branchId, name, permissions, actorId, now, ct);
                await InsertAuditAsync(conn, tx, actorId, now, "ROLE_CREATE", "ROLE", roleId,
                    $"«{name}» در شعبه {branchId} با دسترسی‌های: {DescribePermissions(permissions)}", ct);
                return roleId;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("نقشی با این نام در این شعبه وجود دارد.");
        }
    }

    /// <summary>
    /// فقط تفاوت‌ها اعمال می‌شوند: دسترسی‌های حذف‌شده پاک و دسترسی‌های تازه ثبت می‌شوند. تغییر در سابقه ثبت می‌شود.
    /// </summary>
    public async Task SetRolePermissionsAsync(int roleId, IReadOnlyCollection<Permission> permissions, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync(async (conn, tx) =>
            {
                var role = await ReadRoleAsync(conn, tx, roleId, ct)
                    ?? throw new BusinessRuleException("نقش انتخاب‌شده پیدا نشد.");
                var current = await ReadRolePermissionsAsync(conn, tx, roleId, ct);
                var wanted = new HashSet<Permission>(permissions);
                var added = wanted.Except(current).ToList();
                var removed = current.Except(wanted).ToList();
                if (added.Count == 0 && removed.Count == 0)
                {
                    return false;
                }

                foreach (var permission in removed)
                {
                    await ExecuteAsync(conn, tx, ct,
                        "DELETE FROM dbo.AccessRolePermissions WHERE RoleId = @roleId AND Permission = @code;",
                        new SqlParameter("@roleId", roleId),
                        new SqlParameter("@code", PermissionCodes.ToCode(permission)));
                }
                foreach (var permission in added)
                {
                    await ExecuteAsync(conn, tx, ct,
                        "INSERT INTO dbo.AccessRolePermissions (RoleId, Permission) VALUES (@roleId, @code);",
                        new SqlParameter("@roleId", roleId),
                        new SqlParameter("@code", PermissionCodes.ToCode(permission)));
                }

                await InsertAuditAsync(conn, tx, actorId, now, "ROLE_PERMISSIONS", "ROLE", roleId,
                    $"«{role.Name}» افزوده: {DescribePermissions(added)}؛ حذف: {DescribePermissions(removed)}", ct);
                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            // دو ذخیره‌ی هم‌زمان یک دسترسی را دوبار درج کرده‌اند.
            throw new ConcurrencyConflictException();
        }
    }

    /// <summary>نقش را حذف می‌کند؛ اگر هنوز به کاربری داده شده باشد، خطای کاربری برمی‌گرداند.</summary>
    public async Task DeleteRoleAsync(int roleId, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync(async (conn, tx) =>
            {
                var role = await ReadRoleAsync(conn, tx, roleId, ct)
                    ?? throw new BusinessRuleException("نقش انتخاب‌شده پیدا نشد.");
                var assigned = Convert.ToInt32(await ScalarAsync(conn, tx, ct,
                    "SELECT COUNT(*) FROM dbo.UserBranchRoles WHERE RoleId = @roleId;",
                    new SqlParameter("@roleId", roleId)), CultureInfo.InvariantCulture);
                if (assigned > 0)
                {
                    throw new BusinessRuleException($"این نقش به {assigned} کاربر داده شده است. اول نقش آن‌ها را در این شعبه تغییر دهید.");
                }

                await ExecuteAsync(conn, tx, ct,
                    "DELETE FROM dbo.AccessRolePermissions WHERE RoleId = @roleId;",
                    new SqlParameter("@roleId", roleId));
                await ExecuteAsync(conn, tx, ct,
                    "DELETE FROM dbo.AccessRoles WHERE Id = @roleId;",
                    new SqlParameter("@roleId", roleId));
                await InsertAuditAsync(conn, tx, actorId, now, "ROLE_DELETE", "ROLE", roleId,
                    $"«{role.Name}» از شعبه {role.BranchId} حذف شد", ct);
                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyError)
        {
            throw new BusinessRuleException("این نقش هنوز به کاربری داده شده است.");
        }
    }

    /// <summary>
    /// عضویت کاربر در شعبه را با نقش داده‌شده می‌گذارد یا (roleId = null) حذف می‌کند.
    /// نقش باید متعلق به همان شعبه باشد و شعبه‌ی اصلی کاربر نمی‌تواند حذف شود.
    /// </summary>
    public async Task SetMembershipAsync(int userId, int branchId, int? roleId, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync(async (conn, tx) =>
            {
                var currentRoleId = ToNullableInt(await ScalarAsync(conn, tx, ct,
                    "SELECT RoleId FROM dbo.UserBranchRoles WHERE UserId = @userId AND BranchId = @branchId;",
                    new SqlParameter("@userId", userId),
                    new SqlParameter("@branchId", branchId)));
                var defaultBranchId = ToNullableInt(await ScalarAsync(conn, tx, ct,
                    "SELECT BranchId FROM dbo.Users WHERE Id = @userId;",
                    new SqlParameter("@userId", userId)));

                if (roleId is null)
                {
                    if (currentRoleId is null)
                    {
                        return false;
                    }
                    if (defaultBranchId == branchId)
                    {
                        throw new BusinessRuleException("این شعبه، شعبه‌ی اصلی کاربر است. اول شعبه‌ی اصلی دیگری انتخاب کنید.");
                    }
                    await ExecuteAsync(conn, tx, ct,
                        "DELETE FROM dbo.UserBranchRoles WHERE UserId = @userId AND BranchId = @branchId;",
                        new SqlParameter("@userId", userId),
                        new SqlParameter("@branchId", branchId));
                    await InsertAuditAsync(conn, tx, actorId, now, "USER_BRANCH_ROLE", "USER", userId,
                        $"شعبه {branchId}: عضویت حذف شد", ct);
                    return true;
                }

                if (currentRoleId == roleId)
                {
                    return false;
                }
                var role = await ReadRoleAsync(conn, tx, roleId.Value, ct)
                    ?? throw new BusinessRuleException("نقش انتخابی پیدا نشد.");
                if (role.BranchId != branchId)
                {
                    throw new BusinessRuleException("نقش انتخابی متعلق به این شعبه نیست.");
                }

                if (currentRoleId is null)
                {
                    await ExecuteAsync(conn, tx, ct, @"
INSERT INTO dbo.UserBranchRoles (UserId, BranchId, RoleId, GrantedBy, GrantedAt)
VALUES (@userId, @branchId, @roleId, @actorId, @now);",
                        new SqlParameter("@userId", userId),
                        new SqlParameter("@branchId", branchId),
                        new SqlParameter("@roleId", roleId.Value),
                        new SqlParameter("@actorId", actorId),
                        new SqlParameter("@now", now));
                }
                else
                {
                    await ExecuteAsync(conn, tx, ct, @"
UPDATE dbo.UserBranchRoles
SET RoleId = @roleId, GrantedBy = @actorId, GrantedAt = @now
WHERE UserId = @userId AND BranchId = @branchId;",
                        new SqlParameter("@userId", userId),
                        new SqlParameter("@branchId", branchId),
                        new SqlParameter("@roleId", roleId.Value),
                        new SqlParameter("@actorId", actorId),
                        new SqlParameter("@now", now));
                }

                await InsertAuditAsync(conn, tx, actorId, now, "USER_BRANCH_ROLE", "USER", userId,
                    $"شعبه {branchId}: نقش «{role.Name}»", ct);
                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyError)
        {
            throw new BusinessRuleException("شعبه یا نقش انتخابی معتبر نیست.");
        }
    }

    /// <summary>شعبه‌ی اصلی کاربر را تغییر می‌دهد؛ شعبه باید یکی از عضویت‌های همان کاربر باشد.</summary>
    public async Task SetDefaultBranchAsync(int userId, int branchId, int actorId, DateTime now, CancellationToken ct = default)
    {
        await WithTransactionAsync(async (conn, tx) =>
        {
            var memberCount = Convert.ToInt32(await ScalarAsync(conn, tx, ct,
                "SELECT COUNT(*) FROM dbo.UserBranchRoles WHERE UserId = @userId AND BranchId = @branchId;",
                new SqlParameter("@userId", userId),
                new SqlParameter("@branchId", branchId)), CultureInfo.InvariantCulture);
            if (memberCount == 0)
            {
                throw new BusinessRuleException("شعبه‌ی اصلی باید یکی از شعبه‌هایی باشد که کاربر در آن عضو است.");
            }

            var previous = ToNullableInt(await ScalarAsync(conn, tx, ct,
                "SELECT BranchId FROM dbo.Users WHERE Id = @userId;",
                new SqlParameter("@userId", userId)));
            if (previous == branchId)
            {
                return false;
            }

            await ExecuteAsync(conn, tx, ct,
                "UPDATE dbo.Users SET BranchId = @branchId WHERE Id = @userId;",
                new SqlParameter("@userId", userId),
                new SqlParameter("@branchId", branchId));
            await InsertAuditAsync(conn, tx, actorId, now, "USER_DEFAULT_BRANCH", "USER", userId,
                $"شعبه‌ی اصلی: {previous?.ToString(CultureInfo.InvariantCulture) ?? "—"} → {branchId}", ct);
            return true;
        }, ct);
    }

    /// <summary>
    /// خواندن دسترسی یک کاربر یا همه‌ی کاربران. عضویت‌ها و دسترسی‌های نقش‌ها در یک پرس‌وجو می‌آیند.
    /// </summary>
    private static async Task<Dictionary<int, UserAccess>> ReadAccessAsync(SqlConnection conn, int? userId, CancellationToken ct)
    {
        var filter = userId is null ? string.Empty : "\nWHERE u.Id = @userId";
        var sql = @"
SELECT u.Id, u.Role, u.IsActive, u.BranchId, m.BranchId, br.Name, m.RoleId, ar.Name, p.Permission
FROM dbo.Users u
LEFT JOIN dbo.UserBranchRoles m ON m.UserId = u.Id
LEFT JOIN dbo.Branches br ON br.Id = m.BranchId
LEFT JOIN dbo.AccessRoles ar ON ar.Id = m.RoleId
LEFT JOIN dbo.AccessRolePermissions p ON p.RoleId = m.RoleId" + filter + ";";

        var users = new Dictionary<int, (UserRole Role, bool IsActive, int? DefaultBranchId)>();
        var memberships = new Dictionary<int, Dictionary<int, (string BranchName, int RoleId, string RoleName, HashSet<Permission> Permissions)>>();
        await using var cmd = new SqlCommand(sql, conn);
        if (userId is { } id)
        {
            cmd.Parameters.Add(new SqlParameter("@userId", id));
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var uid = reader.GetInt32(0);
            if (!users.ContainsKey(uid))
            {
                users[uid] = (ParseRole(reader.GetString(1)), reader.GetBoolean(2), reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3));
                memberships[uid] = new Dictionary<int, (string BranchName, int RoleId, string RoleName, HashSet<Permission> Permissions)>();
            }
            if (reader.IsDBNull(4))
            {
                continue;
            }

            var branchId = reader.GetInt32(4);
            var byBranch = memberships[uid];
            if (!byBranch.TryGetValue(branchId, out var entry))
            {
                entry = (reader.GetString(5), reader.GetInt32(6), reader.GetString(7), new HashSet<Permission>());
                byBranch[branchId] = entry;
            }
            if (!reader.IsDBNull(8) && PermissionCodes.TryParse(reader.GetString(8), out var permission))
            {
                entry.Permissions.Add(permission);
            }
        }

        var result = new Dictionary<int, UserAccess>();
        foreach (var pair in users)
        {
            var branches = memberships[pair.Key].ToDictionary(
                b => b.Key,
                b => new BranchAccess(b.Key, b.Value.BranchName, b.Value.RoleId, b.Value.RoleName, b.Value.Permissions));
            result[pair.Key] = new UserAccess(pair.Key, pair.Value.Role, pair.Value.IsActive, pair.Value.DefaultBranchId, branches);
        }
        return result;
    }

    private static async Task<int> InsertRoleAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int branchId,
        string name,
        IEnumerable<Permission> permissions,
        int? actorId,
        DateTime now,
        CancellationToken ct)
    {
        var idValue = await ScalarAsync(conn, tx, ct, @"
INSERT INTO dbo.AccessRoles (BranchId, Name, CreatedBy, CreatedAt)
OUTPUT INSERTED.Id
VALUES (@branchId, @name, @actorId, @now);",
            new SqlParameter("@branchId", branchId),
            new SqlParameter("@name", name),
            NullableInt("@actorId", actorId),
            new SqlParameter("@now", now));
        var roleId = Convert.ToInt32(idValue, CultureInfo.InvariantCulture);
        foreach (var permission in permissions)
        {
            await ExecuteAsync(conn, tx, ct,
                "INSERT INTO dbo.AccessRolePermissions (RoleId, Permission) VALUES (@roleId, @code);",
                new SqlParameter("@roleId", roleId),
                new SqlParameter("@code", PermissionCodes.ToCode(permission)));
        }
        return roleId;
    }

    private static async Task<(int BranchId, string Name)?> ReadRoleAsync(SqlConnection conn, SqlTransaction tx, int roleId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT BranchId, Name FROM dbo.AccessRoles WHERE Id = @roleId;", conn, tx);
        cmd.Parameters.Add(new SqlParameter("@roleId", roleId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return (reader.GetInt32(0), reader.GetString(1));
    }

    private static async Task<HashSet<Permission>> ReadRolePermissionsAsync(SqlConnection conn, SqlTransaction tx, int roleId, CancellationToken ct)
    {
        var result = new HashSet<Permission>();
        await using var cmd = new SqlCommand("SELECT Permission FROM dbo.AccessRolePermissions WHERE RoleId = @roleId;", conn, tx);
        cmd.Parameters.Add(new SqlParameter("@roleId", roleId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (PermissionCodes.TryParse(reader.GetString(0), out var permission))
            {
                result.Add(permission);
            }
        }
        return result;
    }

    private static async Task<int?> FindRoleIdAsync(SqlConnection conn, SqlTransaction tx, int branchId, string name, CancellationToken ct)
    {
        return ToNullableInt(await ScalarAsync(conn, tx, ct,
            "SELECT Id FROM dbo.AccessRoles WHERE BranchId = @branchId AND Name = @name;",
            new SqlParameter("@branchId", branchId),
            new SqlParameter("@name", name)));
    }

    private static int? ToNullableInt(object? value) =>
        value is null or DBNull ? (int?)null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static string DescribePermissions(IEnumerable<Permission> permissions)
    {
        var names = permissions.Select(PermissionCodes.DisplayName).ToList();
        return names.Count == 0 ? "—" : string.Join("، ", names);
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

    public async Task<int> AddUserAsync(string username, string fullName, UserRole role, int? branchId, string passwordHash, DateTime now, int? actorId, string? initialRoleName, CancellationToken ct = default)
    {
        try
        {
            return await WithTransactionAsync(async (conn, tx) =>
            {
                var idValue = await ScalarAsync(conn, tx, ct, @"
INSERT INTO dbo.Users (Username, FullName, PasswordHash, Role, BranchId, IsActive, CreatedAt)
OUTPUT INSERTED.Id
VALUES (@username, @fullName, @passwordHash, @role, @branchId, 1, @now);",
                    new SqlParameter("@username", username),
                    new SqlParameter("@fullName", fullName),
                    new SqlParameter("@passwordHash", passwordHash),
                    new SqlParameter("@role", role.ToString()),
                    NullableInt("@branchId", branchId),
                    new SqlParameter("@now", now));
                var userId = Convert.ToInt32(idValue, CultureInfo.InvariantCulture);

                // کاربر شعبه: شعبه‌ی اصلی و عضویت با نقش داده‌شده در همان شعبه در یک تراکنش ثبت می‌شوند.
                if (role != UserRole.Admin && branchId is { } homeBranch && initialRoleName is not null)
                {
                    var roleId = await FindRoleIdAsync(conn, tx, homeBranch, initialRoleName, ct)
                        ?? throw new BusinessRuleException($"نقش «{initialRoleName}» در این شعبه وجود ندارد. آن را در بخش نقش‌ها بسازید یا نقش دیگری انتخاب کنید.");
                    await ExecuteAsync(conn, tx, ct, @"
INSERT INTO dbo.UserBranchRoles (UserId, BranchId, RoleId, GrantedBy, GrantedAt)
VALUES (@userId, @branchId, @roleId, @actorId, @now);",
                        new SqlParameter("@userId", userId),
                        new SqlParameter("@branchId", homeBranch),
                        new SqlParameter("@roleId", roleId),
                        new SqlParameter("@actorId", actorId ?? throw new InvalidOperationException("عضویت شعبه بدون ثبت‌کننده ساخته نمی‌شود.")),
                        new SqlParameter("@now", now));
                }
                return userId;
            }, ct);
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
        TradeInfo? trade;
        await using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add(new SqlParameter("@id", tradeId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            trade = await reader.ReadAsync(ct) ? ReadTrade(reader) : null;
        }
        if (trade is null)
        {
            return null;
        }
        return trade with { Settlements = await ReadTradeSettlementsAsync(conn, tradeId, ct) };
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

    /// <summary>ستون‌های مشتری به همان ترتیبی که ReadCustomer می‌خواند. CustomerCode محاسباتی است و هرگز نوشته نمی‌شود.</summary>
    private const string CustomerColumns =
        "Id, CustomerCode, FullName, NationalCode, Phone, Mobile, Address, City, Sheba1, Sheba2, CardNumber1, CardNumber2, Note, UpdatedAt";

    public async Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string search, int take, CancellationToken ct = default)
    {
        var sql = $@"
SELECT TOP (@take) {CustomerColumns}
FROM dbo.Customers
WHERE FullName LIKE @pattern OR CustomerCode LIKE @pattern OR NationalCode LIKE @pattern
   OR Phone LIKE @pattern OR Mobile LIKE @pattern OR Sheba1 LIKE @pattern OR Sheba2 LIKE @pattern
   OR CardNumber1 LIKE @pattern OR CardNumber2 LIKE @pattern
ORDER BY FullName, Id;";
        var result = new List<CustomerInfo>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@take", take));
        cmd.Parameters.Add(new SqlParameter("@pattern", "%" + search + "%"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadCustomer(reader));
        }
        return result;
    }

    public async Task<CustomerInfo?> GetCustomerAsync(int id, CancellationToken ct = default)
    {
        var sql = $"SELECT {CustomerColumns} FROM dbo.Customers WHERE Id = @id;";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCustomer(reader) : null;
    }

    public async Task<int> AddCustomerAsync(CustomerInput input, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            return await WithTransactionAsync(async (conn, tx) =>
            {
                const string sql = @"
INSERT INTO dbo.Customers (FullName, NationalCode, Phone, Mobile, Address, City, Sheba1, Sheba2, CardNumber1, CardNumber2, Note,
                           CreatedBy, CreatedAt, UpdatedBy, UpdatedAt)
OUTPUT INSERTED.Id
VALUES (@name, @nationalCode, @phone, @mobile, @address, @city, @sheba1, @sheba2, @card1, @card2, @note,
        @userId, @now, @userId, @now);";
                await using var cmd = new SqlCommand(sql, conn, tx);
                AddCustomerParameters(cmd, input);
                cmd.Parameters.Add(new SqlParameter("@userId", actorId));
                cmd.Parameters.Add(new SqlParameter("@now", now));
                var id = (int)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("شناسه‌ی مشتری ایجاد نشد."));
                await InsertAuditAsync(conn, tx, actorId, now, "CUSTOMER_CREATE", "CUSTOMER", id,
                    $"مشتری «{input.FullName}» ثبت شد", ct);
                return id;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("این کد ملی یا شناسه قبلاً برای مشتری دیگری ثبت شده است.");
        }
    }

    public async Task UpdateCustomerAsync(int id, CustomerInput input, int actorId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await WithTransactionAsync<bool>(async (conn, tx) =>
            {
                const string sql = @"
UPDATE dbo.Customers
SET FullName = @name, NationalCode = @nationalCode, Phone = @phone, Mobile = @mobile, Address = @address,
    City = @city, Sheba1 = @sheba1, Sheba2 = @sheba2, CardNumber1 = @card1, CardNumber2 = @card2, Note = @note,
    UpdatedBy = @userId, UpdatedAt = @now
WHERE Id = @id;";
                await using var cmd = new SqlCommand(sql, conn, tx);
                AddCustomerParameters(cmd, input);
                cmd.Parameters.Add(new SqlParameter("@userId", actorId));
                cmd.Parameters.Add(new SqlParameter("@now", now));
                cmd.Parameters.Add(new SqlParameter("@id", id));
                if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                {
                    throw new BusinessRuleException("مشتری مورد نظر پیدا نشد.");
                }
                await InsertAuditAsync(conn, tx, actorId, now, "CUSTOMER_UPDATE", "CUSTOMER", id,
                    $"اطلاعات مشتری «{input.FullName}» به‌روز شد", ct);
                return true;
            }, ct);
        }
        catch (SqlException ex) when (ex.Number is DuplicateKeyError or UniqueIndexError)
        {
            throw new BusinessRuleException("این کد ملی یا شناسه قبلاً برای مشتری دیگری ثبت شده است.");
        }
    }

    private static void AddCustomerParameters(SqlCommand cmd, CustomerInput input)
    {
        cmd.Parameters.Add(new SqlParameter("@name", input.FullName ?? string.Empty));
        cmd.Parameters.Add(new SqlParameter("@nationalCode", (object?)input.NationalCode ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@phone", (object?)input.Phone ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@mobile", (object?)input.Mobile ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@address", (object?)input.Address ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@city", (object?)input.City ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@sheba1", (object?)input.Sheba1 ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@sheba2", (object?)input.Sheba2 ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@card1", (object?)input.CardNumber1 ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@card2", (object?)input.CardNumber2 ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@note", (object?)input.Note ?? DBNull.Value));
    }

    private static CustomerInfo ReadCustomer(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        ReadNullableString(reader, 3),
        ReadNullableString(reader, 4),
        ReadNullableString(reader, 5),
        ReadNullableString(reader, 6),
        ReadNullableString(reader, 7),
        ReadNullableString(reader, 8),
        ReadNullableString(reader, 9),
        ReadNullableString(reader, 10),
        ReadNullableString(reader, 11),
        ReadNullableString(reader, 12),
        reader.GetDateTime(13));

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

    private static CashTransactionInfo ReadCashTransaction(SqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4) == "RECEIVE" ? CashTransactionDirection.Receipt : CashTransactionDirection.Payment,
        reader.GetInt32(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8).Trim(),
        reader.GetString(9),
        reader.GetByte(10),
        reader.GetDecimal(11),
        reader.GetString(12).Trim(),
        reader.GetDecimal(13),
        ParseRateMode(reader.GetString(14)),
        reader.GetDecimal(15),
        reader.GetDecimal(16),
        reader.GetDecimal(17),
        reader.GetDecimal(18),
        ReadNullableString(reader, 19),
        reader.GetDateTime(20),
        reader.GetString(21),
        reader.GetBoolean(22),
        reader.IsDBNull(23) ? (DateTime?)null : reader.GetDateTime(23),
        ReadNullableString(reader, 24),
        ReadNullableString(reader, 25));

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
            ReadNullableString(reader, 20),
            reader.GetInt32(21),
            ParseSettlementMode(reader.GetString(22)),
            ParseRateMode(reader.GetString(23)),
            reader.GetDecimal(24),
            reader.GetDecimal(25),
            null,
            ReadNullableString(reader, 26)?.Trim(),
            ParsePaymentMethod(reader.GetString(27)));
    }

    private static TradeSettlementMode ParseSettlementMode(string value) => value switch
    {
        "DIRECT" => TradeSettlementMode.Direct,
        "SPLIT" => TradeSettlementMode.Split,
        "ACCOUNT" => TradeSettlementMode.CustomerAccount,
        _ => throw new InvalidOperationException($"روش تسویه‌ی دیتابیس ناشناخته است: {value}"),
    };

    private static TradeRateMode ParseRateMode(string value) => value switch
    {
        "DIRECT" => TradeRateMode.Direct,
        "DERIVED" => TradeRateMode.Derived,
        _ => throw new InvalidOperationException($"روش نرخ دیتابیس ناشناخته است: {value}"),
    };

    private static TradePaymentMethod ParsePaymentMethod(string value) => value switch
    {
        "CASH" => TradePaymentMethod.Cash,
        "CREDIT" => TradePaymentMethod.Credit,
        "CHEQUE" => TradePaymentMethod.Cheque,
        "POS" => TradePaymentMethod.Pos,
        "TRANSFER" => TradePaymentMethod.BankTransfer,
        _ => throw new InvalidOperationException($"روش دریافت/پرداخت دیتابیس ناشناخته است: {value}"),
    };

    private static string PaymentMethodCode(TradePaymentMethod value) => value switch
    {
        TradePaymentMethod.Cash => "CASH",
        TradePaymentMethod.Credit => "CREDIT",
        TradePaymentMethod.Cheque => "CHEQUE",
        TradePaymentMethod.Pos => "POS",
        TradePaymentMethod.BankTransfer => "TRANSFER",
        _ => throw new InvalidOperationException("روش دریافت/پرداخت ناشناخته است."),
    };

    private static SqlParameter Money(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = value };

    private static SqlParameter PreciseRate(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 8, Value = value };

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
