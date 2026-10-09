using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data.Tests;

/// <summary>کمک‌های مشترک تست‌های پایگاه داده: اتصال با تلاش مجدد، حذف بانک، اجرای اسکریپت و خواندن نتیجه.</summary>
internal static class SqlTestDb
{
    public static async Task<SqlConnection> OpenWithRetryAsync(string connectionString)
    {
        // SQL Server داخل سرویس CI ممکن است چند ثانیه بعد از شروع job آماده شود؛ تا آماده شدن دوباره تلاش می‌کند.
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

    public static IEnumerable<string> SplitBatches(string script) =>
        Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0);

    public static async Task ExecuteAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ExecAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await OpenWithRetryAsync(connectionString);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await OpenWithRetryAsync(connectionString);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var value2 = await cmd.ExecuteScalarAsync();
        return value2 is DBNull ? null : value2;
    }

    public static async Task<List<object?[]>> RowsAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await OpenWithRetryAsync(connectionString);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static async Task DropAsync(string masterConnection, string database)
    {
        await using var conn = await OpenWithRetryAsync(masterConnection);
        await ExecuteAsync(conn,
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
    }

    /// <summary>
    /// اسکریپت نصب کامل را روی بانکی با نام دلخواه اجرا می‌کند (نام بانک داخل اسکریپت جایگزین می‌شود).
    /// این اسکریپت فقط برای مقایسه با نتیجه‌ی مهاجرت‌ها (drift test) و نصب دستی استفاده می‌شود.
    /// </summary>
    public static async Task RunInstallScriptAsync(string masterConnection, string scriptPath, string database)
    {
        var script = await File.ReadAllTextAsync(scriptPath);
        script = script
            .Replace("[AccountingSystem]", "[" + database + "]", StringComparison.Ordinal)
            .Replace("N'AccountingSystem'", "N'" + database + "'", StringComparison.Ordinal);

        await using var conn = await OpenWithRetryAsync(masterConnection);
        foreach (var batch in SplitBatches(script))
        {
            await ExecuteAsync(conn, batch);
        }
    }
}
