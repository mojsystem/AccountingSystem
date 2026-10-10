using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Data.Schema;

/// <summary>فایل مهاجرت همراه برنامه: شماره، نام، متن کامل و هش.</summary>
public sealed record EmbeddedMigration(int Version, string Name, string Sql, string Checksum)
{
    public MigrationInfo ToInfo() => new(Version, Name, Checksum);
}

/// <summary>
/// فایل‌های database/migrations/NNNN_name.sql که در اسمبلی Data جاسازی شده‌اند.
/// </summary>
public static class MigrationCatalog
{
    private const string ResourcePrefix = "migrations/";

    private static readonly Regex FileNamePattern = new(
        @"^(\d{4})_([A-Za-z0-9_]+)\.sql$",
        RegexOptions.CultureInvariant);

    private static readonly Regex BatchSeparator = new(
        @"^\s*GO\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<EmbeddedMigration> Load()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var migrations = new List<EmbeddedMigration>();
        foreach (var resource in resources)
        {
            var fileName = resource[ResourcePrefix.Length..];
            var match = FileNamePattern.Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException($"نام فایل مهاجرت نامعتبر است: {fileName}");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"فایل مهاجرت پیدا نشد: {fileName}");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();

            migrations.Add(new EmbeddedMigration(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                match.Groups[2].Value,
                sql,
                SchemaUpgradePlanner.ChecksumOf(sql)));
        }

        SchemaUpgradePlanner.ValidateCatalog(migrations.Select(m => m.ToInfo()).ToList());
        return migrations.OrderBy(m => m.Version).ToList();
    }

    /// <summary>
    /// متن را با خط GO به دسته‌ها می‌شکند. دسته‌ای که فقط توضیح دارد حذف می‌شود.
    /// </summary>
    public static IEnumerable<string> SplitBatches(string sql) =>
        BatchSeparator.Split(sql)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Split('\n').Any(line =>
            {
                var t = line.Trim();
                return t.Length > 0 && !t.StartsWith("--", StringComparison.Ordinal);
            }));
}
