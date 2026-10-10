using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data.Schema;

/// <summary>
/// وضعیت نسخه، فهرست پشتیبان‌ها (از msdb)، پشتیبان‌گیری و بازیابی پایگاه داده‌ی برنامه روی سرور SQL.
/// </summary>
public sealed class SqlDatabaseMaintenance : IDatabaseMaintenance
{
    private readonly string _connectionString;
    private readonly string? _backupFolder;
    private readonly SqlConnectionTarget _target;

    public SqlDatabaseMaintenance(string connectionString, string? backupFolder = null)
    {
        _connectionString = connectionString;
        _backupFolder = string.IsNullOrWhiteSpace(backupFolder) ? null : backupFolder.Trim();
        _target = SqlConnectionTarget.From(connectionString);
    }

    public async Task<DatabaseStatusInfo> GetStatusAsync(CancellationToken ct = default)
    {
        var folder = await ResolveFolderAsync(ct);
        var appVersion = MigrationCatalog.Load().Max(m => m.Version);

        if (!await DatabaseExistsAsync(ct))
        {
            return new DatabaseStatusInfo(_target.DatabaseName, false, null, appVersion, Array.Empty<AppliedMigration>(), folder);
        }

        await using var conn = await SchemaUpgrader.OpenAsync(_target.DatabaseConnectionString, ct);
        var state = await SchemaUpgrader.ReadStateAsync(conn, ct);
        int? version = state.Applied.Count > 0 ? state.Applied.Max(a => a.Version) : (int?)null;
        return new DatabaseStatusInfo(_target.DatabaseName, true, version, appVersion, state.Applied, folder);
    }

    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct = default)
    {
        const string sql =
            "SELECT TOP (50) bmf.physical_device_name, bs.database_name, bs.backup_finish_date, CAST(bs.backup_size AS BIGINT) " +
            "FROM msdb.dbo.backupset AS bs " +
            "INNER JOIN msdb.dbo.backupmediafamily AS bmf ON bmf.media_set_id = bs.media_set_id " +
            "WHERE bs.database_name = @db AND bs.type = 'D' AND bmf.device_type = 2 AND bs.backup_finish_date IS NOT NULL " +
            "ORDER BY bs.backup_finish_date DESC;";

        await using var master = await SchemaUpgrader.OpenAsync(_target.MasterConnectionString, ct);
        await using var cmd = new SqlCommand(sql, master);
        cmd.Parameters.AddWithValue("@db", _target.DatabaseName);

        var list = new List<BackupInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var finished = reader.GetDateTime(2);
            var size = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);
            list.Add(new BackupInfo(reader.GetString(0), reader.GetString(1), finished, size));
        }

        return list;
    }

    public Task<BackupInfo> CreateBackupAsync(BackupReason reason, DateTime now, CancellationToken ct = default) =>
        SchemaUpgrader.TakeBackupAsync(_target, _backupFolder, reason, now, ct);

    public async Task RestoreBackupAsync(string filePath, DateTime now, CancellationToken ct = default)
    {
        var backups = await ListBackupsAsync(ct);
        if (!backups.Any(b => string.Equals(b.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessRuleException("این فایل در فهرست پشتیبان‌های سرور SQL نیست.");
        }

        var database = _target.DatabaseName;
        var exists = await DatabaseExistsAsync(ct);

        // پیش از هر تغییری، وضعیت فعلی هم پشتیبان می‌شود تا بازیابی اشتباه برگشت‌پذیر باشد.
        if (exists)
        {
            await SchemaUpgrader.TakeBackupAsync(_target, _backupFolder, BackupReason.BeforeRestore, now, ct);
        }

        await using var master = await SchemaUpgrader.OpenAsync(_target.MasterConnectionString, ct);
        var moves = await BuildMoveClauseAsync(master, filePath, ct);

        if (exists)
        {
            await SchemaUpgrader.ExecuteAsync(master,
                $"ALTER DATABASE {SchemaUpgrader.Quote(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", ct);
        }

        try
        {
            await SchemaUpgrader.ExecuteAsync(master,
                $"RESTORE DATABASE {SchemaUpgrader.Quote(database)} FROM DISK = {SchemaUpgrader.Lit(filePath)} " +
                $"WITH REPLACE, RECOVERY, STATS = 10, {moves};",
                ct,
                timeoutSeconds: 0);
        }
        catch (SqlException ex)
        {
            if (exists)
            {
                await TryMultiUserAsync(master, database);
            }

            throw new SchemaUpgradeException($"بازیابی انجام نشد: {ex.Message}", ex);
        }

        await SchemaUpgrader.ExecuteAsync(master,
            $"ALTER DATABASE {SchemaUpgrader.Quote(database)} SET MULTI_USER;", ct);

        // اتصال‌های قدیمی به بانک قبلی از بین رفته‌اند؛ استخر اتصال هم پاک می‌شود.
        SqlConnection.ClearPool(new SqlConnection(_target.DatabaseConnectionString));

        // پشتیبان ممکن است از نسخه‌ی قدیمی‌تر برنامه باشد؛ پس از بازیابی، ارتقا انجام می‌شود.
        await SchemaUpgrader.EnsureUpToDateAsync(
            _connectionString,
            new SchemaUpgradeOptions(BackupBeforeUpgrade: true, BackupFolder: _backupFolder),
            null,
            ct);
    }

    private async Task<bool> DatabaseExistsAsync(CancellationToken ct)
    {
        await using var master = await SchemaUpgrader.OpenAsync(_target.MasterConnectionString, ct);
        return Convert.ToInt32(await SchemaUpgrader.ScalarAsync(master,
            $"SELECT CASE WHEN DB_ID({SchemaUpgrader.Lit(_target.DatabaseName)}) IS NULL THEN 0 ELSE 1 END;", ct)) == 1;
    }

    private async Task<string> ResolveFolderAsync(CancellationToken ct)
    {
        if (_backupFolder is not null)
        {
            return _backupFolder;
        }

        await using var master = await SchemaUpgrader.OpenAsync(_target.MasterConnectionString, ct);
        return await ServerPathAsync(master, "InstanceDefaultBackupPath", ct) ?? string.Empty;
    }

    /// <summary>
    /// فایل‌های بانک پشتیبان را با FILELISTONLY می‌خواند و برای هر کدام یک MOVE به پوشه‌ی پیش‌فرض سرور می‌سازد.
    /// </summary>
    private async Task<string> BuildMoveClauseAsync(SqlConnection master, string filePath, CancellationToken ct)
    {
        var files = new List<(string Logical, string Type)>();
        await using (var cmd = new SqlCommand($"RESTORE FILELISTONLY FROM DISK = {SchemaUpgrader.Lit(filePath)};", master))
        {
            cmd.CommandTimeout = 120;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                files.Add((reader.GetString(reader.GetOrdinal("LogicalName")), reader.GetString(reader.GetOrdinal("Type"))));
            }
        }

        var dataDirectory = await ServerPathAsync(master, "InstanceDefaultDataPath", ct);
        var logDirectory = await ServerPathAsync(master, "InstanceDefaultLogPath", ct) ?? dataDirectory;
        if (string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new SchemaUpgradeException("مسیر پیش‌فرض فایل‌های پایگاه داده روی سرور مشخص نیست؛ بازیابی انجام نشد.");
        }

        var database = _target.DatabaseName;
        var moves = new List<string>();
        var dataIndex = 0;
        var logIndex = 0;
        foreach (var (logical, type) in files)
        {
            string fileName;
            string directory;
            if (type == "L")
            {
                logIndex++;
                fileName = logIndex == 1 ? $"{database}_log.ldf" : $"{database}_log{logIndex}.ldf";
                directory = logDirectory;
            }
            else
            {
                dataIndex++;
                fileName = dataIndex == 1 ? $"{database}.mdf" : $"{database}_{dataIndex}.ndf";
                directory = dataDirectory;
            }

            moves.Add($"MOVE {SchemaUpgrader.Lit(logical)} TO {SchemaUpgrader.Lit(BackupRules.CombineServerPath(directory, fileName))}");
        }

        if (moves.Count == 0)
        {
            throw new SchemaUpgradeException("فایل پشتیبان فهرست فایلی ندارد؛ بازیابی انجام نشد.");
        }

        return string.Join(", ", moves);
    }

    private static async Task TryMultiUserAsync(SqlConnection master, string database)
    {
        try
        {
            await SchemaUpgrader.ExecuteAsync(master,
                $"ALTER DATABASE {SchemaUpgrader.Quote(database)} SET MULTI_USER;", CancellationToken.None);
        }
        catch (Exception)
        {
            // خطای برگرداندن حالت چندکاربره را به خطای اصلی بازیابی ترجیح نمی‌دهیم.
        }
    }

    private static async Task<string?> ServerPathAsync(SqlConnection master, string property, CancellationToken ct)
    {
        var value = await SchemaUpgrader.ScalarAsync(master,
            $"SELECT CONVERT(NVARCHAR(4000), SERVERPROPERTY(N'{property}'));", ct);
        return value as string;
    }
}
