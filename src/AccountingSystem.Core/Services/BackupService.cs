using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Services;

/// <summary>
/// پشتیبان‌گیری و بازیابی پایگاه داده. فقط مدیر سیستم به آن دسترسی دارد.
/// </summary>
public sealed class BackupService
{
    private readonly IDatabaseMaintenance _maintenance;

    public BackupService(IDatabaseMaintenance maintenance)
    {
        _maintenance = maintenance;
    }

    public Task<DatabaseStatusInfo> GetStatusAsync(CurrentUser actor, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return _maintenance.GetStatusAsync(ct);
    }

    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CurrentUser actor, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return _maintenance.ListBackupsAsync(ct);
    }

    public Task<BackupInfo> CreateBackupAsync(CurrentUser actor, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        return _maintenance.CreateBackupAsync(BackupReason.Manual, now, ct);
    }

    public Task RestoreAsync(CurrentUser actor, string? filePath, DateTime now, CancellationToken ct = default)
    {
        RoleGuard.RequireAdmin(actor);
        var path = BackupRules.CleanRestorePath(filePath);
        return _maintenance.RestoreBackupAsync(path, now, ct);
    }
}
