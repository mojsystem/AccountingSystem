using System.Text.RegularExpressions;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Common;

/// <summary>
/// قواعد نام و مسیر فایل پشتیبان. فایل‌ها روی سرور SQL ساخته و خوانده می‌شوند،
/// بنابراین مسیرها باید با جداکننده‌ی همان سیستم‌عامل سرور ساخته شوند.
/// </summary>
public static class BackupRules
{
    public const int MaxPathLength = 500;

    public static string BuildFileName(string databaseName, BackupReason reason, DateTime now)
    {
        var safeName = Regex.Replace(databaseName, @"[^A-Za-z0-9_\-]", "_");
        var slug = reason switch
        {
            BackupReason.BeforeUpgrade => "before-upgrade",
            BackupReason.BeforeRestore => "before-restore",
            _ => "manual",
        };
        return $"{safeName}_{slug}_{now:yyyyMMdd-HHmmss-fff}.bak";
    }

    public static string CombineServerPath(string folder, string fileName)
    {
        var trimmed = (folder ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new BusinessRuleException("پوشه‌ی پشتیبان مشخص نشده است.");
        }

        var separator = trimmed.Contains('\\') || !trimmed.Contains('/') ? "\\" : "/";
        return trimmed.EndsWith('\\') || trimmed.EndsWith('/')
            ? trimmed + fileName
            : trimmed + separator + fileName;
    }

    /// <summary>مسیر بازیابی باید یک فایل .bak معتبر باشد. (مسیر پیش از اجرا با فهرست msdb هم مقایسه می‌شود.)</summary>
    public static string CleanRestorePath(string? path)
    {
        var clean = (path ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > MaxPathLength)
        {
            throw new BusinessRuleException("مسیر فایل پشتیبان نامعتبر است.");
        }

        if (clean.Any(c => char.IsControl(c)))
        {
            throw new BusinessRuleException("مسیر فایل پشتیبان نباید کاراکتر کنترلی داشته باشد.");
        }

        if (!clean.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException("فقط فایل‌های پشتیبان با پسوند .bak پذیرفته می‌شوند.");
        }

        return clean;
    }
}
