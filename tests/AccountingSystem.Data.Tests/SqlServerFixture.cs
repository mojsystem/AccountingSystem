using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AccountingSystem.Data.Tests;

/// <summary>
/// پایگاه داده‌ی تست (AccountingSystem_IT) را از روی اسکریپت اصلی می‌سازد.
/// اگر متغیر محیطی AS_TEST_SQL تنظیم نشده باشد، IsEnabled برابر false است و تست‌ها بدون خطا تمام می‌شوند.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "AS_TEST_SQL";
    private const string TestDatabase = "AccountingSystem_IT";

    public string? ConnectionString { get; private set; }

    public bool IsEnabled => ConnectionString is not null;

    public async Task InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        var masterConnection = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = "master" }.ConnectionString;
        ConnectionString = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = TestDatabase }.ConnectionString;

        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "AccountingSystem.sql"));
        script = script
            .Replace("[AccountingSystem]", "[" + TestDatabase + "]", StringComparison.Ordinal)
            .Replace("N'AccountingSystem'", "N'" + TestDatabase + "'", StringComparison.Ordinal);

        await using var conn = await OpenWithRetryAsync(masterConnection);
        await ExecuteAsync(conn, $"IF DB_ID(N'{TestDatabase}') IS NOT NULL BEGIN ALTER DATABASE [{TestDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{TestDatabase}]; END");

        foreach (var batch in SplitBatches(script))
        {
            await ExecuteAsync(conn, batch);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// SQL Server داخل سرویس CI ممکن است چند ثانیه بعد از شروع job آماده شود؛ تا آماده شدن دوباره تلاش می‌کند.
    /// </summary>
    private static async Task<SqlConnection> OpenWithRetryAsync(string connectionString)
    {
        const int maxAttempts = 60;
        for (var attempt = 1; ; attempt++)
        {
            var conn = new SqlConnection(connectionString);
            try
            {
                await conn.OpenAsync();
                return conn;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                await conn.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }
    }

    private static IEnumerable<string> SplitBatches(string script) =>
        Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0);

    private static async Task ExecuteAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }
}
