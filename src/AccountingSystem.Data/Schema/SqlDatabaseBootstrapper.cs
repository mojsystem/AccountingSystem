using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data.Schema;

public sealed record SqlDatabaseBootstrapResult(
    string DatabaseName,
    string ConnectionString,
    bool Created,
    SchemaUpgradeResult Upgrade);

/// <summary>
/// اتصال اولیه به SQL Server، انتخاب بانک برنامه و ساخت بانک تازه با فایل در مسیر محلی برنامه.
/// بانک‌های موجود با نام AccountingSystem یا mojdb1, mojdb2, ... دوباره استفاده می‌شوند.
/// </summary>
public static class SqlDatabaseBootstrapper
{
    private const string BootstrapLock = "AccountingSystem.DatabaseBootstrap";
    private static readonly Regex MojDbPattern = new(@"^mojdb([1-9][0-9]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>اتصال به master بدون وابستگی به وجود بانک برنامه را آزمایش می‌کند.</summary>
    public static async Task TestServerConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        var masterConnection = ToMaster(connectionString);
        await using var conn = await SchemaUpgrader.OpenAsync(masterConnection, ct);
        await SchemaUpgrader.ScalarAsync(conn, "SELECT 1;", ct);
    }

    /// <summary>
    /// اگر بانک مقصد از رشته‌ی اتصال موجود باشد آن را نگه می‌دارد. در غیر این صورت اولین mojdbN موجود
    /// را برمی‌گزیند؛ اگر هیچ‌کدام نبود اولین نام آزاد را در appBasePath/database می‌سازد.
    /// </summary>
    public static async Task<SqlDatabaseBootstrapResult> EnsureApplicationDatabaseAsync(
        string serverConnectionString,
        string? preferredDatabase,
        string appBasePath,
        SchemaUpgradeOptions? upgradeOptions = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        upgradeOptions ??= new SchemaUpgradeOptions();
        var root = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" };
        await using var master = await SchemaUpgrader.OpenAsync(root.ConnectionString, ct);
        await AcquireBootstrapLockAsync(master, ct);
        try
        {
            var databases = await ReadUserDatabasesAsync(master, ct);
            string databaseName;
            bool created;

            var preferred = (preferredDatabase ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(preferred)
                && !preferred.Equals("master", StringComparison.OrdinalIgnoreCase)
                && databases.TryGetValue(preferred, out var preferredState)
                && preferredState == 0)
            {
                databaseName = preferred;
                created = false;
            }
            else
            {
                var existingMojDbs = databases
                    .Where(x => x.Value == 0)
                    .Select(x => (Name: x.Key, Match: MojDbPattern.Match(x.Key)))
                    .Where(x => x.Match.Success)
                    .Select(x => (x.Name, Number: int.Parse(x.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
                    .OrderBy(x => x.Number)
                    .ToList();

                if (existingMojDbs.Count > 0)
                {
                    databaseName = existingMojDbs[0].Name;
                    created = false;
                }
                else
                {
                    var dataFolder = Path.Combine(appBasePath, "database");
                    Directory.CreateDirectory(dataFolder);
                    databaseName = FindNextFreeMojDbName(databases, dataFolder);
                    created = true;
                }
            }

            var targetConnection = new SqlConnectionStringBuilder(serverConnectionString)
            {
                InitialCatalog = databaseName,
                ConnectTimeout = Math.Max(new SqlConnectionStringBuilder(serverConnectionString).ConnectTimeout, 8),
            }.ConnectionString;

            if (created)
            {
                log?.Invoke($"بانک {databaseName} در پوشه‌ی {Path.Combine(appBasePath, "database")} ساخته می‌شود...");
            }
            else
            {
                log?.Invoke($"بانک موجود {databaseName} انتخاب شد.");
            }

            // قفل master تا پایان ساخت و ارتقای اولیه نگه داشته می‌شود تا دو اجرای هم‌زمان بانک mojdb یکسان نسازند.
            var result = await SchemaUpgrader.EnsureUpToDateAsync(
                targetConnection,
                upgradeOptions with { DatabaseFilesFolder = created ? Path.Combine(appBasePath, "database") : upgradeOptions.DatabaseFilesFolder },
                log,
                ct);

            return new SqlDatabaseBootstrapResult(databaseName, targetConnection, created, result);
        }
        finally
        {
            await ReleaseBootstrapLockAsync(master);
        }
    }

    public static string ToMaster(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;

    private static async Task<Dictionary<string, byte>> ReadUserDatabasesAsync(SqlConnection master, CancellationToken ct)
    {
        var states = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        const string sql = "SELECT name, state FROM sys.databases WHERE database_id > 4;";
        await using var cmd = new SqlCommand(sql, master);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            states[reader.GetString(0)] = reader.GetByte(1);
        }

        return states;
    }

    private static string FindNextFreeMojDbName(IReadOnlyDictionary<string, byte> databases, string dataFolder)
    {
        for (var number = 1; number < int.MaxValue; number++)
        {
            var name = "mojdb" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (databases.ContainsKey(name))
            {
                continue;
            }

            // بانک حذف‌شده می‌تواند فایل یتیم بگذارد؛ هرگز آن فایل را با INIT/REPLACE بازنویسی نمی‌کنیم.
            if (File.Exists(Path.Combine(dataFolder, name + ".mdf"))
                || File.Exists(Path.Combine(dataFolder, name + "_log.ldf")))
            {
                continue;
            }

            return name;
        }

        throw new InvalidOperationException("نام خالی برای پایگاه داده‌ی mojdb پیدا نشد.");
    }

    private static async Task AcquireBootstrapLockAsync(SqlConnection master, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            $"DECLARE @r INT; EXEC @r = sp_getapplock @Resource = {SchemaUpgrader.Lit(BootstrapLock)}, " +
            "@LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 120000; SELECT @r;",
            master);
        var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (result < 0)
        {
            throw new InvalidOperationException("قفل انتخاب پایگاه داده گرفته نشد؛ برنامه‌ی دیگری در حال راه‌اندازی است.");
        }
    }

    private static async Task ReleaseBootstrapLockAsync(SqlConnection master)
    {
        try
        {
            await SchemaUpgrader.ExecuteAsync(master,
                $"EXEC sp_releaseapplock @Resource = {SchemaUpgrader.Lit(BootstrapLock)}, @LockOwner = N'Session';",
                CancellationToken.None,
                30);
        }
        catch (Exception)
        {
            // با بسته شدن اتصال قفل آزاد می‌شود.
        }
    }
}
