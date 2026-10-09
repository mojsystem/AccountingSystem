namespace AccountingSystem.Core.Domain;

/// <summary>یک فایل مهاجرت که همراه برنامه است (از روی فایل‌های database/migrations ساخته می‌شود).</summary>
public sealed record MigrationInfo(int Version, string Name, string Checksum);

/// <summary>یک نسخه‌ی ثبت‌شده در جدول dbo.SchemaVersion.</summary>
public sealed record AppliedMigration(int Version, string Name, string Checksum, DateTime AppliedAt, string AppliedBy);

/// <summary>
/// ساختار پایگاه داده‌ی موجود که از روی کاتالوگ SQL خوانده می‌شود.
/// Objects شامل نام جدول‌ها (مثل «dbo.Accounts») و نام ستون‌ها (مثل «dbo.Accounts.Level») است.
/// </summary>
public sealed record DatabaseSchemaState(
    bool HasSchemaVersionTable,
    IReadOnlyList<AppliedMigration> Applied,
    bool HasUserTables,
    IReadOnlySet<string> Objects);

public enum UpgradeKind
{
    /// <summary>پایگاه داده از قبل به آخرین نسخه‌ی برنامه رسیده است.</summary>
    UpToDate,

    /// <summary>پایگاه داده خالی است و همه‌ی نسخه‌ها از اول اجرا می‌شوند.</summary>
    Create,

    /// <summary>نسخه‌های باقی‌مانده اجرا می‌شوند؛ اگر پایگاه داده‌ی قدیمی باشد، نسخه‌های موجودش هم ثبت می‌شوند.</summary>
    Upgrade,

    /// <summary>پایگاه داده قابل ارتقای خودکار نیست؛ هیچ تغییری داده نمی‌شود.</summary>
    Refuse,
}

/// <param name="AdoptVersions">نسخه‌هایی که پایگاه داده‌ی قدیمی از قبل دارد و فقط در جدول ثبت می‌شوند.</param>
/// <param name="ApplyVersions">نسخه‌هایی که باید به ترتیب اجرا شوند.</param>
public sealed record UpgradePlan(
    UpgradeKind Kind,
    IReadOnlyList<int> AdoptVersions,
    IReadOnlyList<int> ApplyVersions,
    string? Reason);

public enum BackupReason
{
    Manual,
    BeforeUpgrade,
    BeforeRestore,
}

public sealed record BackupInfo(string FilePath, string DatabaseName, DateTime FinishedAt, long SizeBytes);

public sealed record DatabaseStatusInfo(
    string DatabaseName,
    bool Exists,
    int? DatabaseVersion,
    int ApplicationVersion,
    IReadOnlyList<AppliedMigration> History,
    string BackupFolder);
