using AccountingSystem.Data.Schema;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AccountingSystem.Data.Tests;

/// <summary>
/// پایگاه داده‌ی تست (AccountingSystem_IT) را با همان مهاجرتی می‌سازد که برنامه استفاده می‌کند.
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

        await SqlTestDb.DropAsync(masterConnection, TestDatabase);
        await SchemaUpgrader.EnsureUpToDateAsync(ConnectionString, new SchemaUpgradeOptions(BackupBeforeUpgrade: false));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
