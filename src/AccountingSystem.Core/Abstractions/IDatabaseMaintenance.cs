using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Abstractions;

/// <summary>
/// وضعیت نسخه‌ی پایگاه داده، فهرست پشتیبان‌ها، پشتیبان‌گیری و بازیابی.
/// پیاده‌سازی SQL در لایه‌ی Data است؛ بررسی دسترسی مدیر در <c>BackupService</c> انجام می‌شود.
/// </summary>
public interface IDatabaseMaintenance
{
    Task<DatabaseStatusInfo> GetStatusAsync(CancellationToken ct = default);

    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct = default);

    Task<BackupInfo> CreateBackupAsync(BackupReason reason, DateTime now, CancellationToken ct = default);

    /// <summary>بازیابی یک پشتیبان. قبل از آن پشتیبان «قبل از بازیابی» گرفته می‌شود و بعد از آن نسخه‌ی پایگاه داده ارتقا می‌یابد.</summary>
    Task RestoreBackupAsync(string filePath, DateTime now, CancellationToken ct = default);
}
