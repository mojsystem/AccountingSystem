using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data.Schema;

/// <param name="BackupBeforeUpgrade">اگر true باشد و نسخه‌ای قرار است اجرا شود، پیش از آن پشتیبان گرفته می‌شود.</param>
/// <param name="BackupFolder">پوشه‌ی پشتیبان روی سرور SQL. خالی یعنی پوشه‌ی پیش‌فرض نمونه‌ی SQL Server.</param>
public sealed record SchemaUpgradeOptions(
    bool BackupBeforeUpgrade = true,
    string? BackupFolder = null,
    string? DatabaseFilesFolder = null);

public sealed record SchemaUpgradeResult(
    UpgradeKind Kind,
    int Version,
    IReadOnlyList<int> AdoptedVersions,
    IReadOnlyList<int> AppliedVersions,
    string? BackupPath);

/// <summary>
/// ارتقای خودکار پایگاه داده. برنامه‌ی وب و ویندوز هر بار که اجرا می‌شوند، قبل از هر کاری این متد را صدا می‌زنند:
/// اگر بانک نباشد می‌سازد، نسخه‌ی آن را با نسخه‌ی برنامه مقایسه می‌کند و نسخه‌های باقی‌مانده را به ترتیب اجرا می‌کند.
/// هر نسخه در یک تراکنش است؛ اگر شکست بخورد، همان نسخه برمی‌گردد و برنامه بالا نمی‌آید.
/// </summary>
public static class SchemaUpgrader
{
    private const string LockResource = "AccountingSystem.SchemaUpgrade";

    public static async Task<SchemaUpgradeResult> EnsureUpToDateAsync(
        string connectionString,
        SchemaUpgradeOptions? options = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        options ??= new SchemaUpgradeOptions();
        var target = SqlConnectionTarget.From(connectionString);
        var migrations = MigrationCatalog.Load();
        var available = migrations.Select(m => m.ToInfo()).ToList();

        await EnsureDatabaseExistsAsync(target, options, log, ct);

        await using var conn = await OpenAsync(target.DatabaseConnectionString, ct);
        await AcquireLockAsync(conn, ct);
        try
        {
            var state = await ReadStateAsync(conn, ct);
            var plan = SchemaUpgradePlanner.Plan(available, state);
            var current = state.Applied.Count > 0 ? state.Applied.Max(a => a.Version) : 0;

            if (plan.Kind == UpgradeKind.Refuse)
            {
                throw new SchemaUpgradeException(plan.Reason ?? "ارتقای پایگاه داده انجام نشد.");
            }

            if (plan.Kind == UpgradeKind.UpToDate)
            {
                return new SchemaUpgradeResult(plan.Kind, current, plan.AdoptVersions, plan.ApplyVersions, null);
            }

            string? backupPath = null;
            if (plan.ApplyVersions.Count > 0 && state.HasUserTables && options.BackupBeforeUpgrade)
            {
                log?.Invoke("پشتیبان قبل از ارتقای پایگاه داده گرفته می‌شود...");
                var backup = await TakeBackupAsync(target, options.BackupFolder, BackupReason.BeforeUpgrade, DateTime.Now, ct);
                backupPath = backup.FilePath;
            }

            await EnsureVersionTableAsync(conn, ct);

            if (plan.AdoptVersions.Count > 0)
            {
                await AdoptAsync(conn, available, plan.AdoptVersions, ct);
                log?.Invoke($"نسخه‌ی ۱ تا {plan.AdoptVersions[^1]} برای پایگاه داده‌ی موجود ثبت شد.");
            }

            foreach (var version in plan.ApplyVersions)
            {
                var migration = migrations[version - 1];
                log?.Invoke($"اجرای نسخه‌ی {version} ({migration.Name})...");
                await ApplyAsync(conn, migration, ct);
            }

            var finalVersion = plan.ApplyVersions.Count > 0
                ? plan.ApplyVersions[^1]
                : plan.AdoptVersions.Count > 0 ? plan.AdoptVersions[^1] : current;
            return new SchemaUpgradeResult(plan.Kind, finalVersion, plan.AdoptVersions, plan.ApplyVersions, backupPath);
        }
        finally
        {
            await ReleaseLockAsync(conn);
        }
    }

    internal static async Task<DatabaseSchemaState> ReadStateAsync(SqlConnection conn, CancellationToken ct)
    {
        var hasVersionTable = Convert.ToInt32(
            await ScalarAsync(conn, "SELECT CASE WHEN OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL THEN 0 ELSE 1 END;", ct)) == 1;

        var applied = new List<AppliedMigration>();
        if (hasVersionTable)
        {
            await using var cmd = new SqlCommand(
                "SELECT Version, Name, Checksum, AppliedAt, AppliedBy FROM dbo.SchemaVersion ORDER BY Version;", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                applied.Add(new AppliedMigration(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2).Trim(),
                    reader.GetDateTime(3),
                    reader.GetString(4)));
            }
        }

        var hasUserTables = Convert.ToInt32(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "WHERE NOT (s.name = N'dbo' AND t.name = N'SchemaVersion');", ct)) > 0;

        var objects = new HashSet<string>(StringComparer.Ordinal);
        const string objectSql =
            "SELECT s.name + N'.' + t.name FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "UNION ALL " +
            "SELECT s.name + N'.' + t.name + N'.' + c.name FROM sys.columns AS c " +
            "INNER JOIN sys.tables AS t ON t.object_id = c.object_id " +
            "INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id;";
        await using (var cmd = new SqlCommand(objectSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                objects.Add(reader.GetString(0));
            }
        }

        return new DatabaseSchemaState(hasVersionTable, applied, hasUserTables, objects);
    }

    /// <summary>
    /// پشتیبان کامل می‌گیرد. از COMPRESSION استفاده نمی‌شود تا با SQL Server Express هم کار کند.
    /// اگر پشتیبان‌گیری شکست بخورد، استثنا پرتاب می‌شود و ارتقا یا بازیابی انجام نمی‌شود.
    /// </summary>
    internal static async Task<BackupInfo> TakeBackupAsync(
        SqlConnectionTarget target,
        string? configuredFolder,
        BackupReason reason,
        DateTime now,
        CancellationToken ct)
    {
        await using var master = await OpenAsync(target.MasterConnectionString, ct);
        var folder = string.IsNullOrWhiteSpace(configuredFolder)
            ? await ServerPropertyAsync(master, "InstanceDefaultBackupPath", ct)
            : configuredFolder.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new SchemaUpgradeException(
                "پوشه‌ی پشتیبان مشخص نیست. مسیر را در Backup:Folder فایل appsettings.json تنظیم کنید.");
        }

        var path = BackupRules.CombineServerPath(folder, BackupRules.BuildFileName(target.DatabaseName, reason, now));
        var sql = $"BACKUP DATABASE {Quote(target.DatabaseName)} TO DISK = {Lit(path)} WITH INIT, CHECKSUM, STATS = 10;";
        try
        {
            await ExecuteAsync(master, sql, ct, timeoutSeconds: 0);
        }
        catch (SqlException ex)
        {
            throw new SchemaUpgradeException($"پشتیبان‌گیری انجام نشد: {ex.Message}", ex);
        }

        return new BackupInfo(path, target.DatabaseName, now, 0);
    }

    internal static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    internal static async Task ExecuteAsync(SqlConnection conn, string sql, CancellationToken ct, int timeoutSeconds = 120)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<object?> ScalarAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    internal static async Task<string?> ServerPropertyAsync(SqlConnection conn, string property, CancellationToken ct)
    {
        var value = await ScalarAsync(conn, $"SELECT CONVERT(NVARCHAR(4000), SERVERPROPERTY(N'{property}'));", ct);
        return value as string;
    }

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static string Lit(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task EnsureDatabaseExistsAsync(
        SqlConnectionTarget target,
        SchemaUpgradeOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        await using var master = await OpenAsync(target.MasterConnectionString, ct);
        var exists = Convert.ToInt32(await ScalarAsync(master,
            $"SELECT CASE WHEN DB_ID({Lit(target.DatabaseName)}) IS NULL THEN 0 ELSE 1 END;", ct)) == 1;
        if (exists)
        {
            return;
        }

        log?.Invoke($"پایگاه داده‌ی {target.DatabaseName} ساخته می‌شود...");
        if (string.IsNullOrWhiteSpace(options.DatabaseFilesFolder))
        {
            await ExecuteAsync(master, $"CREATE DATABASE {Quote(target.DatabaseName)};", ct);
        }
        else
        {
            var folder = Path.GetFullPath(options.DatabaseFilesFolder.Trim());
            Directory.CreateDirectory(folder);
            var dataPath = Path.Combine(folder, target.DatabaseName + ".mdf");
            var logPath = Path.Combine(folder, target.DatabaseName + "_log.ldf");
            var create = $"CREATE DATABASE {Quote(target.DatabaseName)} " +
                         $"ON PRIMARY (NAME = {Lit(target.DatabaseName + "_data")}, FILENAME = {Lit(dataPath)}, SIZE = 32MB, FILEGROWTH = 64MB) " +
                         $"LOG ON (NAME = {Lit(target.DatabaseName + "_log")}, FILENAME = {Lit(logPath)}, SIZE = 16MB, FILEGROWTH = 64MB);";
            await ExecuteAsync(master, create, ct);
        }

        await ExecuteAsync(master, $"ALTER DATABASE {Quote(target.DatabaseName)} SET COMPATIBILITY_LEVEL = 150;", ct);
    }

    private static async Task AcquireLockAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "DECLARE @r INT; " +
            $"EXEC @r = sp_getapplock @Resource = {Lit(LockResource)}, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 120000; " +
            "SELECT @r;",
            conn);
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (result < 0)
        {
            throw new SchemaUpgradeException(
                "قفل ارتقای پایگاه داده گرفته نشد؛ احتمالاً برنامه‌ی دیگری در حال ارتقا یا بازیابی است.");
        }
    }

    private static async Task ReleaseLockAsync(SqlConnection conn)
    {
        try
        {
            await ExecuteAsync(conn,
                $"EXEC sp_releaseapplock @Resource = {Lit(LockResource)}, @LockOwner = N'Session';",
                CancellationToken.None,
                timeoutSeconds: 30);
        }
        catch (Exception)
        {
            // قفل با بسته شدن اتصال هم آزاد می‌شود؛ خطای آزادسازی نباید خطای اصلی را پنهان کند.
        }
    }

    private static async Task EnsureVersionTableAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL
            CREATE TABLE dbo.SchemaVersion
            (
                Version   INT           NOT NULL CONSTRAINT PK_SchemaVersion PRIMARY KEY,
                Name      NVARCHAR(200) NOT NULL,
                Checksum  CHAR(64)      NOT NULL,
                AppliedAt DATETIME2(0)  NOT NULL CONSTRAINT DF_SchemaVersion_AppliedAt DEFAULT (SYSDATETIME()),
                AppliedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_SchemaVersion_AppliedBy DEFAULT (SUSER_SNAME())
            );
            """;
        await ExecuteAsync(conn, sql, ct);
    }

    /// <summary>برای پایگاه داده‌ی قدیمی: نسخه‌هایی که از قبل روی آن اعمال شده‌اند فقط ثبت می‌شوند.</summary>
    private static async Task AdoptAsync(
        SqlConnection conn,
        IReadOnlyList<MigrationInfo> available,
        IReadOnlyList<int> versions,
        CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var version in versions)
            {
                var info = available[version - 1];
                await using var cmd = new SqlCommand(
                    "INSERT INTO dbo.SchemaVersion (Version, Name, Checksum, AppliedBy) VALUES (@v, @n, @c, N'ADOPTED');",
                    conn,
                    tx);
                cmd.Parameters.AddWithValue("@v", info.Version);
                cmd.Parameters.AddWithValue("@n", info.Name);
                cmd.Parameters.AddWithValue("@c", info.Checksum);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(tx);
            throw new SchemaUpgradeException($"ثبت نسخه‌های موجود ناموفق بود و برگشت داده شد: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// یک نسخه را در یک تراکنش اجرا می‌کند. دسته‌ها با GO جدا می‌شوند و ثبت نسخه آخرین کار تراکنش است.
    /// </summary>
    private static async Task ApplyAsync(SqlConnection conn, EmbeddedMigration migration, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var batch in MigrationCatalog.SplitBatches(migration.Sql))
            {
                await using var cmd = new SqlCommand(batch, conn, tx) { CommandTimeout = 0 };
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using var record = new SqlCommand(
                "INSERT INTO dbo.SchemaVersion (Version, Name, Checksum) VALUES (@v, @n, @c);",
                conn,
                tx);
            record.Parameters.AddWithValue("@v", migration.Version);
            record.Parameters.AddWithValue("@n", migration.Name);
            record.Parameters.AddWithValue("@c", migration.Checksum);
            await record.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(tx);
            throw new SchemaUpgradeException(
                $"اجرای نسخه‌ی {migration.Version} ({migration.Name}) ناموفق بود و تغییرات همین نسخه برگشت داده شد: {ex.Message}",
                ex);
        }
    }

    private static async Task SafeRollbackAsync(SqlTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // اگر تراکنش قبلاً خودکار برگشته باشد، خطای rollback مهم نیست.
        }
    }
}

/// <summary>رشته‌ی اتصال به سرور و نام پایگاه داده‌ی برنامه.</summary>
internal sealed record SqlConnectionTarget(string DatabaseName, string MasterConnectionString, string DatabaseConnectionString)
{
    public static SqlConnectionTarget From(string connectionString)
    {
        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("در رشته‌ی اتصال نام پایگاه داده (Database) مشخص نشده است.");
        }

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        return new SqlConnectionTarget(databaseName, master, connectionString);
    }
}
