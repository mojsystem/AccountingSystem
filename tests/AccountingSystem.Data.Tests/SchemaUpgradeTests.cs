using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Data.Schema;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AccountingSystem.Data.Tests;

/// <summary>
/// ارتقای خودکار پایگاه داده: نصب تازه، پذیرش بانک نسخه‌ی ۱، backfill مشتریان، رد تغییر فایل/نسخه‌ی جدیدتر، و پشتیبان/بازیابی.
/// بانک‌های این کلاس نام جدا دارند و با AccountingSystem_IT تداخل ندارند.
/// </summary>
public sealed class SchemaUpgradeTests
{
    private const string EnvironmentVariable = "AS_TEST_SQL";

    private readonly string? _baseConnection = Environment.GetEnvironmentVariable(EnvironmentVariable);

    private string? MasterConnection => string.IsNullOrWhiteSpace(_baseConnection)
        ? null
        : new SqlConnectionStringBuilder(_baseConnection) { InitialCatalog = "master" }.ConnectionString;

    private string? Db(string name) => string.IsNullOrWhiteSpace(_baseConnection)
        ? null
        : new SqlConnectionStringBuilder(_baseConnection) { InitialCatalog = name }.ConnectionString;

    private static SchemaUpgradeOptions NoBackup() => new(BackupBeforeUpgrade: false);

    [Fact]
    public async Task Fresh_database_matches_the_install_script_and_is_recorded_as_versioned()
    {
        var master = MasterConnection;
        if (master is null)
        {
            return;
        }

        const string fresh = "AccountingSystem_UP_Fresh";
        const string script = "AccountingSystem_UP_Script";
        await SqlTestDb.DropAsync(master, fresh);
        await SqlTestDb.DropAsync(master, script);

        var first = await SchemaUpgrader.EnsureUpToDateAsync(Db(fresh)!, NoBackup());
        Assert.Equal(UpgradeKind.Create, first.Kind);
        Assert.Equal(new[] { 1, 2, 3 }, first.AppliedVersions);

        var second = await SchemaUpgrader.EnsureUpToDateAsync(Db(fresh)!, NoBackup());
        Assert.Equal(UpgradeKind.UpToDate, second.Kind);

        await SqlTestDb.RunInstallScriptAsync(master, Path.Combine(AppContext.BaseDirectory, "AccountingSystem.sql"), script);

        var expected = await SchemaSnapshot.ReadAsync(Db(script)!);
        var actual = await SchemaSnapshot.ReadAsync(Db(fresh)!);
        Assert.Empty(expected.Except(actual));
        Assert.Empty(actual.Except(expected));

        var versions = await SqlTestDb.RowsAsync(Db(fresh)!, "SELECT Version, AppliedBy FROM dbo.SchemaVersion ORDER BY Version;");
        Assert.Equal(3, versions.Count);
        Assert.NotEqual("ADOPTED", (string)versions[2][1]!);
    }

    [Fact]
    public async Task Permission_v2_database_is_adopted_then_upgraded_and_keeps_its_trades()
    {
        var master = MasterConnection;
        if (master is null)
        {
            return;
        }

        const string legacy = "AccountingSystem_UP_Legacy";
        await SqlTestDb.DropAsync(master, legacy);
        await SqlTestDb.ExecAsync(master, $"CREATE DATABASE [{legacy}];");

        // بانک را دقیقاً مانند نسخه‌ی ۱ (پیش از جدول SchemaVersion) می‌سازیم.
        var v1 = MigrationCatalog.Load().Single(m => m.Version == 1);
        await using (var conn = await SqlTestDb.OpenWithRetryAsync(Db(legacy)!))
        {
            foreach (var batch in MigrationCatalog.SplitBatches(v1.Sql))
            {
                await SqlTestDb.ExecuteAsync(conn, batch);
            }
        }

        await SqlTestDb.ExecAsync(Db(legacy)!,
            "INSERT INTO dbo.Users (Username, FullName, PasswordHash, Role, BranchId) VALUES (N'cashier', N'صندوقدار', N'hash', N'Cashier', (SELECT Id FROM dbo.Branches WHERE Code = N'MAIN'));");

        // شش معامله‌ی قدیمی: دو معامله با یک کد ملی (آخرین نام برنده می‌شود)، دو معامله‌ی بدون کد ملی با یک نام،
        // و دو معامله‌ی بی‌نام/نام یک‌حرفی که به «مشتری نامشخص» می‌روند.
        var trades = new (string? Name, string? NationalCode, DateTime At)[]
        {
            ("علی رضایی", "0012345678", new DateTime(2026, 10, 1, 10, 0, 0)),
            ("علی رضایی\u200Cنژاد", "0012345678", new DateTime(2026, 10, 2, 10, 0, 0)),
            ("سارا احمدی", null, new DateTime(2026, 10, 1, 11, 0, 0)),
            ("سارا احمدی", null, new DateTime(2026, 10, 3, 11, 0, 0)),
            (null, null, new DateTime(2026, 10, 4, 12, 0, 0)),
            ("x", "", new DateTime(2026, 10, 5, 12, 0, 0)),
        };
        foreach (var trade in trades)
        {
            await SqlTestDb.ExecAsync(Db(legacy)!,
                "INSERT INTO dbo.CurrencyTransactions (BranchId, TradeType, CurrencyCode, Amount, Rate, IrrAmount, CostIrr, ProfitIrr, FeeIrr, CustomerName, NationalCode, OccurredAt, CreatedBy) " +
                "VALUES ((SELECT Id FROM dbo.Branches WHERE Code = N'MAIN'), N'BUY', N'USD', 100, 500000, 50000000, 49000000, 1000000, 0, @name, @nc, @at, (SELECT Id FROM dbo.Users WHERE Username = N'cashier'));",
                ("@name", trade.Name),
                ("@nc", trade.NationalCode),
                ("@at", trade.At));
        }

        var backupFolderless = new SchemaUpgradeOptions(BackupBeforeUpgrade: true);
        var result = await SchemaUpgrader.EnsureUpToDateAsync(Db(legacy)!, backupFolderless);

        Assert.Equal(UpgradeKind.Upgrade, result.Kind);
        Assert.Equal(new[] { 1 }, result.AdoptedVersions);
        Assert.Equal(new[] { 2, 3 }, result.AppliedVersions);
        Assert.NotNull(result.BackupPath);

        var backups = await new SqlDatabaseMaintenance(Db(legacy)!).ListBackupsAsync();
        Assert.Contains(backups, b => b.FilePath == result.BackupPath);

        // نسخه‌ی ۱ قدیمی پذیرفته شده و نسخه‌های ۲ و ۳ توسط برنامه اجرا شده‌اند.
        var versions = await SqlTestDb.RowsAsync(Db(legacy)!, "SELECT Version, AppliedBy FROM dbo.SchemaVersion ORDER BY Version;");
        Assert.Equal(3, versions.Count);
        Assert.Equal("ADOPTED", (string)versions[0][1]!);

        // backfill: هر معامله به مشتری وصل است و گروه‌بندی درست است.
        var ids = (await SqlTestDb.RowsAsync(Db(legacy)!, "SELECT Id, CustomerId FROM dbo.CurrencyTransactions ORDER BY Id;"))
            .Select(r => Convert.ToInt32(r[1]))
            .ToList();
        Assert.Equal(6, ids.Count);
        Assert.Equal(6, Convert.ToInt32(await SqlTestDb.ScalarAsync(Db(legacy)!, "SELECT COUNT(*) FROM dbo.CurrencyTransactionSettlements;")));
        Assert.Equal("IRR", await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT RTRIM(SettlementCurrencyCode) FROM dbo.CurrencyTransactions WHERE Id = 1;"));
        Assert.Equal("DIRECT", await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT SettlementMode FROM dbo.CurrencyTransactions WHERE Id = 1;"));
        Assert.Equal("DERIVED", await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT RateMode FROM dbo.CurrencyTransactions WHERE Id = 1;"));
        Assert.Equal(500_000m, Convert.ToDecimal(await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT CrossRate FROM dbo.CurrencyTransactions WHERE Id = 1;"), System.Globalization.CultureInfo.InvariantCulture));
        var legacySettlement = Assert.Single(await SqlTestDb.RowsAsync(Db(legacy)!, @"
SELECT Direction, RTRIM(CurrencyCode), Amount, RateIrr, IrrAmount, CostIrr
FROM dbo.CurrencyTransactionSettlements WHERE TradeId = 1;"));
        Assert.Equal("PAY", legacySettlement[0]);
        Assert.Equal("IRR", legacySettlement[1]);
        Assert.Equal(50_000_000m, Convert.ToDecimal(legacySettlement[2], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1m, Convert.ToDecimal(legacySettlement[3], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(50_000_000m, Convert.ToDecimal(legacySettlement[4], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(50_000_000m, Convert.ToDecimal(legacySettlement[5], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(ids[2], ids[3]);
        Assert.Equal(ids[4], ids[5]);
        Assert.NotEqual(ids[0], ids[2]);
        Assert.NotEqual(ids[2], ids[4]);

        var latestName = await SqlTestDb.ScalarAsync(Db(legacy)!, "SELECT FullName FROM dbo.Customers WHERE Id = @id;", ("@id", ids[0]));
        Assert.Equal("علی رضایی\u200Cنژاد", latestName);

        var placeholder = await SqlTestDb.ScalarAsync(Db(legacy)!, "SELECT FullName FROM dbo.Customers WHERE Id = @id;", ("@id", ids[4]));
        Assert.Equal("مشتری نامشخص", placeholder);

        var customerCount = Convert.ToInt32(await SqlTestDb.ScalarAsync(Db(legacy)!, "SELECT COUNT(*) FROM dbo.Customers;"));
        Assert.Equal(3, customerCount);

        // سرفصل: گروه‌ها و حساب‌های موجود درست به سطح‌ها متصل شده‌اند؛ نام حساب موجود تغییر نمی‌کند.
        Assert.Equal("4|1101|1", await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT CONCAT(Level, N'|', ParentCode, N'|', IsSystem) FROM dbo.Accounts WHERE Code = N'1101-USD';"));
        Assert.Equal("3|10|1", await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT CONCAT(Level, N'|', ParentCode, N'|', IsSystem) FROM dbo.Accounts WHERE Code = N'1001';"));
        Assert.Equal("3|60|0", await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT CONCAT(Level, N'|', ParentCode, N'|', IsSystem) FROM dbo.Accounts WHERE Code = N'6001';"));
        Assert.Equal("1|-|0", await SqlTestDb.ScalarAsync(Db(legacy)!,
            "SELECT CONCAT(Level, N'|', ISNULL(ParentCode, N'-'), N'|', IsSystem) FROM dbo.Accounts WHERE Code = N'6';"));
        Assert.Equal("صندوق ریال", await SqlTestDb.ScalarAsync(Db(legacy)!, "SELECT Name FROM dbo.Accounts WHERE Code = N'1001';"));

        var again = await SchemaUpgrader.EnsureUpToDateAsync(Db(legacy)!, NoBackup());
        Assert.Equal(UpgradeKind.UpToDate, again.Kind);
    }

    [Fact]
    public async Task Changed_migration_or_newer_database_is_refused_before_anything_runs()
    {
        var master = MasterConnection;
        if (master is null)
        {
            return;
        }

        const string guard = "AccountingSystem_UP_Guard";
        await SqlTestDb.DropAsync(master, guard);
        await SchemaUpgrader.EnsureUpToDateAsync(Db(guard)!, NoBackup());
        var realChecksum = await SqlTestDb.ScalarAsync(Db(guard)!, "SELECT Checksum FROM dbo.SchemaVersion WHERE Version = 2;");

        await SqlTestDb.ExecAsync(Db(guard)!, "UPDATE dbo.SchemaVersion SET Checksum = REPLICATE(N'0', 64) WHERE Version = 2;");
        var changed = await Assert.ThrowsAsync<SchemaUpgradeException>(
            () => SchemaUpgrader.EnsureUpToDateAsync(Db(guard)!, NoBackup()));
        Assert.Contains("تغییر کرده", changed.Message);

        await SqlTestDb.ExecAsync(Db(guard)!, "UPDATE dbo.SchemaVersion SET Checksum = @c WHERE Version = 2;", ("@c", realChecksum));
        await SqlTestDb.ExecAsync(Db(guard)!,
            "INSERT INTO dbo.SchemaVersion (Version, Name, Checksum, AppliedBy) VALUES (3, N'future', REPLICATE(N'A', 64), N'test');");
        var newer = await Assert.ThrowsAsync<SchemaUpgradeException>(
            () => SchemaUpgrader.EnsureUpToDateAsync(Db(guard)!, NoBackup()));
        Assert.Contains("جدیدتر", newer.Message);
    }

    [Fact]
    public async Task Backup_then_restore_brings_back_the_backed_up_data_and_keeps_a_pre_restore_copy()
    {
        var master = MasterConnection;
        if (master is null)
        {
            return;
        }

        const string restore = "AccountingSystem_UP_Restore";
        await SqlTestDb.DropAsync(master, restore);
        await SchemaUpgrader.EnsureUpToDateAsync(Db(restore)!, NoBackup());
        var maintenance = new SqlDatabaseMaintenance(Db(restore)!);

        await SqlTestDb.ExecAsync(Db(restore)!, "INSERT INTO dbo.Branches (Code, Name) VALUES (N'BKPA', N'قبل از پشتیبان');");
        var backup = await maintenance.CreateBackupAsync(BackupReason.Manual, DateTime.Now);

        await SqlTestDb.ExecAsync(Db(restore)!, "INSERT INTO dbo.Branches (Code, Name) VALUES (N'BKPB', N'بعد از پشتیبان');");

        var listed = await maintenance.ListBackupsAsync();
        Assert.Contains(listed, b => b.FilePath == backup.FilePath);

        await maintenance.RestoreBackupAsync(backup.FilePath, DateTime.Now);

        Assert.Equal(1, Convert.ToInt32(await SqlTestDb.ScalarAsync(Db(restore)!, "SELECT COUNT(*) FROM dbo.Branches WHERE Code = N'BKPA';")));
        Assert.Equal(0, Convert.ToInt32(await SqlTestDb.ScalarAsync(Db(restore)!, "SELECT COUNT(*) FROM dbo.Branches WHERE Code = N'BKPB';")));

        var afterRestore = await maintenance.ListBackupsAsync();
        Assert.Contains(afterRestore, b => b.FilePath.Contains("before-restore", StringComparison.OrdinalIgnoreCase));

        var status = await maintenance.GetStatusAsync();
        Assert.True(status.Exists);
        Assert.Equal(2, status.DatabaseVersion);
    }
}
