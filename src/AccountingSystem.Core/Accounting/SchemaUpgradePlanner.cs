using System.Security.Cryptography;
using System.Text;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// تصمیم ارتقای پایگاه داده. این کلاس بدون SQL است تا با تست واحد بررسی شود.
/// نسخه‌ی پایگاه داده از جدول dbo.SchemaVersion خوانده و با فایل‌های مهاجرت همراه برنامه مقایسه می‌شود.
/// </summary>
public static class SchemaUpgradePlanner
{
    /// <summary>
    /// اثرانگشت پایگاه‌های داده‌ای که پیش از ثبت نسخه (جدول SchemaVersion) ساخته شده‌اند.
    /// ترتیب مهم است: از بالاترین نسخه به پایین بررسی می‌شود.
    /// </summary>
    private static readonly LegacyFingerprint[] LegacyFingerprints =
    {
        new(
            6,
            new[]
            {
                "dbo.AccessRoles", "dbo.UserBranchRoles", "dbo.Customers",
                "dbo.Accounts.Level", "dbo.Accounts.ParentCode", "dbo.Accounts.IsSystem",
                "dbo.CurrencyTransactions.CustomerId", "dbo.CurrencyTransactions.SettlementMode",
                "dbo.CurrencyTransactions.RateMode", "dbo.CurrencyTransactions.SettlementCurrencyCode",
                "dbo.CurrencyTransactions.CrossRate", "dbo.CurrencyTransactions.CustomerOffsetIrr",
                "dbo.CurrencyTransactions.PaymentMethod", "dbo.JournalLines.CustomerId",
                "dbo.JournalLines.CustomerBalanceCurrencyCode", "dbo.JournalLines.CustomerBalanceDelta",
                "dbo.CurrencyTransactionSettlements", "dbo.CurrencyTransactionSettlements.BankAccountId",
                "dbo.BankAccounts", "dbo.BankAccounts.OpeningBalance", "dbo.BankAccounts.OpeningCostIrr",
                "dbo.BankAccounts.Balance", "dbo.BankAccounts.CostIrr",
                "dbo.CashTransactions", "dbo.CashTransactions.Direction", "dbo.CashTransactions.CustomerId",
                "dbo.CashTransactions.CurrencyCode", "dbo.CashTransactions.Amount", "dbo.CashTransactions.RateMode",
                "dbo.CashTransactions.RateIrr", "dbo.CashTransactions.IrrAmount", "dbo.CashTransactions.CostIrr",
                "dbo.CashTransactions.ProfitIrr", "dbo.CashTransactions.OccurredAt", "dbo.CashTransactions.CreatedBy",
                "dbo.CashTransactions.IsVoided", "dbo.CashTransactions.Seq", "dbo.CashTransactions.ReplacesId",
                "dbo.CashTransactions.BalanceCurrencyCode", "dbo.CashTransactions.BalanceAmount",
            },
            Array.Empty<string>()),
        new(
            5,
            new[]
            {
                "dbo.AccessRoles", "dbo.UserBranchRoles", "dbo.Customers",
                "dbo.Accounts.Level", "dbo.Accounts.ParentCode", "dbo.Accounts.IsSystem",
                "dbo.CurrencyTransactions.CustomerId", "dbo.CurrencyTransactions.SettlementMode",
                "dbo.CurrencyTransactions.RateMode", "dbo.CurrencyTransactions.SettlementCurrencyCode",
                "dbo.CurrencyTransactions.CrossRate", "dbo.CurrencyTransactions.CustomerOffsetIrr",
                "dbo.JournalLines.CustomerId", "dbo.JournalLines.CustomerBalanceCurrencyCode",
                "dbo.JournalLines.CustomerBalanceDelta", "dbo.CurrencyTransactionSettlements",
                "dbo.CashTransactions", "dbo.CashTransactions.Direction", "dbo.CashTransactions.CustomerId",
                "dbo.CashTransactions.CurrencyCode", "dbo.CashTransactions.Amount", "dbo.CashTransactions.RateMode",
                "dbo.CashTransactions.RateIrr", "dbo.CashTransactions.IrrAmount", "dbo.CashTransactions.CostIrr",
                "dbo.CashTransactions.ProfitIrr", "dbo.CashTransactions.OccurredAt", "dbo.CashTransactions.CreatedBy",
                "dbo.CashTransactions.IsVoided", "dbo.CashTransactions.Seq", "dbo.CashTransactions.ReplacesId",
                "dbo.CashTransactions.BalanceCurrencyCode", "dbo.CashTransactions.BalanceAmount",
            },
            Array.Empty<string>()),
        new(
            4,
            new[]
            {
                "dbo.AccessRoles", "dbo.UserBranchRoles", "dbo.Customers",
                "dbo.Accounts.Level", "dbo.Accounts.ParentCode", "dbo.Accounts.IsSystem",
                "dbo.CurrencyTransactions.CustomerId", "dbo.CurrencyTransactions.SettlementMode",
                "dbo.CurrencyTransactions.RateMode", "dbo.CurrencyTransactions.SettlementCurrencyCode",
                "dbo.CurrencyTransactions.CrossRate", "dbo.CurrencyTransactions.CustomerOffsetIrr",
                "dbo.JournalLines.CustomerId", "dbo.CurrencyTransactionSettlements",
                "dbo.CashTransactions", "dbo.CashTransactions.Direction", "dbo.CashTransactions.CustomerId",
                "dbo.CashTransactions.CurrencyCode", "dbo.CashTransactions.Amount", "dbo.CashTransactions.RateMode",
                "dbo.CashTransactions.RateIrr", "dbo.CashTransactions.IrrAmount", "dbo.CashTransactions.CostIrr",
                "dbo.CashTransactions.ProfitIrr", "dbo.CashTransactions.OccurredAt", "dbo.CashTransactions.CreatedBy",
                "dbo.CashTransactions.IsVoided", "dbo.CashTransactions.Seq", "dbo.CashTransactions.ReplacesId",
            },
            Array.Empty<string>()),
        new(
            3,
            new[]
            {
                "dbo.AccessRoles", "dbo.UserBranchRoles", "dbo.Customers",
                "dbo.Accounts.Level", "dbo.Accounts.ParentCode", "dbo.Accounts.IsSystem",
                "dbo.CurrencyTransactions.CustomerId", "dbo.CurrencyTransactions.SettlementMode",
                "dbo.CurrencyTransactions.RateMode", "dbo.CurrencyTransactions.SettlementCurrencyCode",
                "dbo.CurrencyTransactions.CrossRate", "dbo.CurrencyTransactions.CustomerOffsetIrr",
                "dbo.JournalLines.CustomerId", "dbo.CurrencyTransactionSettlements",
            },
            Array.Empty<string>()),
        new(
            2,
            new[]
            {
                "dbo.AccessRoles", "dbo.UserBranchRoles", "dbo.Customers",
                "dbo.Accounts.Level", "dbo.Accounts.ParentCode", "dbo.Accounts.IsSystem",
                "dbo.CurrencyTransactions.CustomerId",
            },
            Array.Empty<string>()),
        new(
            1,
            new[] { "dbo.AccessRoles", "dbo.UserBranchRoles" },
            new[] { "dbo.Customers", "dbo.Accounts.Level" }),
    };

    private sealed record LegacyFingerprint(int Version, string[] Required, string[] Forbidden);

    /// <summary>
    /// هش SHA-256 متن مهاجرت (با پایان خط یکسان). تغییر فایل منتشرشده با این هش شناسایی می‌شود.
    /// </summary>
    public static string ChecksumOf(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>فهرست مهاجرت‌ها باید دقیقاً ۱، ۲، ۳ ... باشد.</summary>
    public static void ValidateCatalog(IReadOnlyList<MigrationInfo> migrations)
    {
        var ordered = migrations.OrderBy(m => m.Version).ToList();
        if (ordered.Count == 0)
        {
            throw new ArgumentException("هیچ فایل مهاجرتی یافت نشد.", nameof(migrations));
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
            {
                throw new ArgumentException($"شماره‌ی نسخه‌ها باید ۱ و پیوسته باشد؛ نسخه‌ی {i + 1} پیدا نشد.", nameof(migrations));
            }

            if (string.IsNullOrWhiteSpace(ordered[i].Name) || ordered[i].Checksum.Length != 64)
            {
                throw new ArgumentException($"فایل مهاجرت نسخه‌ی {ordered[i].Version} نامعتبر است.", nameof(migrations));
            }
        }
    }

    public static UpgradePlan Plan(IReadOnlyList<MigrationInfo> available, DatabaseSchemaState state)
    {
        ValidateCatalog(available);
        var ordered = available.OrderBy(m => m.Version).ToList();
        var latest = ordered[^1].Version;

        if (state.HasSchemaVersionTable && state.Applied.Count > 0)
        {
            return PlanVersioned(ordered, state.Applied, latest);
        }

        if (!state.HasUserTables)
        {
            return new UpgradePlan(
                UpgradeKind.Create,
                Array.Empty<int>(),
                ordered.Select(m => m.Version).ToList(),
                null);
        }

        return PlanLegacy(ordered, state.Objects, latest);
    }

    private static UpgradePlan PlanVersioned(
        IReadOnlyList<MigrationInfo> ordered,
        IReadOnlyList<AppliedMigration> applied,
        int latest)
    {
        var history = applied.OrderBy(a => a.Version).ToList();
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Version != i + 1)
            {
                return Refuse($"سابقه‌ی نسخه‌های پایگاه داده ناقص است (نسخه‌ی {i + 1} ثبت نشده است). ارتقا انجام نشد.");
            }
        }

        var current = history[^1].Version;
        if (current > latest)
        {
            return Refuse(
                $"نسخه‌ی پایگاه داده ({current}) از نسخه‌ی این برنامه ({latest}) جدیدتر است. " +
                "برنامه‌ی جدیدتری نصب کنید؛ با نسخه‌ی قدیمی‌تر به این پایگاه داده وصل نمی‌شوید.");
        }

        foreach (var record in history)
        {
            var file = ordered[record.Version - 1];
            if (!string.Equals(file.Checksum, record.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(
                    $"فایل مهاجرت نسخه‌ی {record.Version} ({record.Name}) بعد از اجرا تغییر کرده است. " +
                    "فایل مهاجرتی که منتشر شده نباید تغییر کند.");
            }
        }

        var pending = ordered.Where(m => m.Version > current).Select(m => m.Version).ToList();
        return pending.Count == 0
            ? new UpgradePlan(UpgradeKind.UpToDate, Array.Empty<int>(), Array.Empty<int>(), null)
            : new UpgradePlan(UpgradeKind.Upgrade, Array.Empty<int>(), pending, null);
    }

    private static UpgradePlan PlanLegacy(
        IReadOnlyList<MigrationInfo> ordered,
        IReadOnlySet<string> objects,
        int latest)
    {
        foreach (var fingerprint in LegacyFingerprints)
        {
            if (fingerprint.Version > latest)
            {
                continue;
            }

            var matches = fingerprint.Required.All(o => objects.Contains(o))
                && !fingerprint.Forbidden.Any(o => objects.Contains(o));
            if (matches)
            {
                return Adopt(ordered, fingerprint.Version);
            }
        }

        var missing = LegacyFingerprints[0].Required.Where(o => !objects.Contains(o)).ToList();
        var detail = missing.Count == 0 ? string.Empty : " اشیای ناموجود: " + string.Join("، ", missing) + ".";
        return Refuse(
            "این پایگاه داده قدیمی است و با هیچ نسخه‌ی شناخته‌شده‌ای یکی نیست؛ " +
            "ارتقای خودکار انجام نشد و هیچ تغییری داده نشد." + detail);
    }

    private static UpgradePlan Adopt(IReadOnlyList<MigrationInfo> ordered, int version)
    {
        var adopt = Enumerable.Range(1, version).ToList();
        var apply = ordered.Where(m => m.Version > version).Select(m => m.Version).ToList();
        return new UpgradePlan(UpgradeKind.Upgrade, adopt, apply, null);
    }

    private static UpgradePlan Refuse(string reason) =>
        new(UpgradeKind.Refuse, Array.Empty<int>(), Array.Empty<int>(), reason);
}
