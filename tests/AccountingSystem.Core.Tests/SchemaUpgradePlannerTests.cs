using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public sealed class SchemaUpgradePlannerTests
{
    private static readonly string Sum1 = new('1', 64);
    private static readonly string Sum2 = new('2', 64);

    private static readonly IReadOnlyList<MigrationInfo> Catalog = new[]
    {
        new MigrationInfo(1, "permissions_v2", Sum1),
        new MigrationInfo(2, "chart_and_customers", Sum2),
    };

    private static DatabaseSchemaState Empty() =>
        new(false, Array.Empty<AppliedMigration>(), false, new HashSet<string>());

    private static DatabaseSchemaState Versioned(params AppliedMigration[] applied) =>
        new(true, applied, true, new HashSet<string>());

    private static DatabaseSchemaState Legacy(params string[] objects) =>
        new(false, Array.Empty<AppliedMigration>(), true, new HashSet<string>(objects));

    private static AppliedMigration Applied(int version, string checksum) =>
        new(version, "v" + version, checksum, DateTime.UtcNow, "test");

    private static readonly string[] V1Objects = { "dbo.AccessRoles", "dbo.UserBranchRoles", "dbo.Branches" };

    private static readonly string[] V2Objects =
    {
        "dbo.AccessRoles", "dbo.UserBranchRoles", "dbo.Customers",
        "dbo.Accounts.Level", "dbo.Accounts.ParentCode", "dbo.Accounts.IsSystem",
        "dbo.CurrencyTransactions.CustomerId",
    };

    [Fact]
    public void Empty_database_gets_every_version_in_order()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Empty());

        Assert.Equal(UpgradeKind.Create, plan.Kind);
        Assert.Empty(plan.AdoptVersions);
        Assert.Equal(new[] { 1, 2 }, plan.ApplyVersions);
    }

    [Fact]
    public void Versioned_database_at_latest_version_is_up_to_date()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Versioned(Applied(1, Sum1), Applied(2, Sum2)));

        Assert.Equal(UpgradeKind.UpToDate, plan.Kind);
        Assert.Empty(plan.ApplyVersions);
    }

    [Fact]
    public void Versioned_database_applies_only_the_remaining_versions()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Versioned(Applied(1, Sum1)));

        Assert.Equal(UpgradeKind.Upgrade, plan.Kind);
        Assert.Empty(plan.AdoptVersions);
        Assert.Equal(new[] { 2 }, plan.ApplyVersions);
    }

    [Fact]
    public void Changed_migration_file_is_refused()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Versioned(Applied(1, Sum1), Applied(2, new string('9', 64))));

        Assert.Equal(UpgradeKind.Refuse, plan.Kind);
        Assert.Contains("تغییر کرده", plan.Reason);
    }

    [Fact]
    public void Database_newer_than_the_program_is_refused()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Versioned(Applied(1, Sum1), Applied(2, Sum2), Applied(3, Sum2)));

        Assert.Equal(UpgradeKind.Refuse, plan.Kind);
        Assert.Contains("جدیدتر", plan.Reason);
    }

    [Fact]
    public void Gap_in_the_version_history_is_refused()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Versioned(Applied(1, Sum1), Applied(3, Sum2)));

        Assert.Equal(UpgradeKind.Refuse, plan.Kind);
        Assert.Contains("ناقص", plan.Reason);
    }

    [Fact]
    public void Permission_v2_database_without_a_version_table_is_adopted_as_version_1()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Legacy(V1Objects));

        Assert.Equal(UpgradeKind.Upgrade, plan.Kind);
        Assert.Equal(new[] { 1 }, plan.AdoptVersions);
        Assert.Equal(new[] { 2 }, plan.ApplyVersions);
    }

    [Fact]
    public void Current_shape_without_a_version_table_is_adopted_as_versions_1_and_2()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Legacy(V2Objects));

        Assert.Equal(UpgradeKind.Upgrade, plan.Kind);
        Assert.Equal(new[] { 1, 2 }, plan.AdoptVersions);
        Assert.Empty(plan.ApplyVersions);
    }

    [Fact]
    public void Half_upgraded_database_is_refused_and_the_missing_objects_are_named()
    {
        var partial = V2Objects.Where(o => o != "dbo.Accounts.Level").ToArray();

        var plan = SchemaUpgradePlanner.Plan(Catalog, Legacy(partial));

        Assert.Equal(UpgradeKind.Refuse, plan.Kind);
        Assert.Contains("dbo.Accounts.Level", plan.Reason);
    }

    [Fact]
    public void Pre_v2_database_is_refused()
    {
        var plan = SchemaUpgradePlanner.Plan(Catalog, Legacy("dbo.Branches", "dbo.UserPermissions"));

        Assert.Equal(UpgradeKind.Refuse, plan.Kind);
        Assert.Contains("dbo.AccessRoles", plan.Reason);
    }

    [Fact]
    public void Catalog_must_be_numbered_from_one_without_gaps()
    {
        var broken = new[]
        {
            new MigrationInfo(1, "a", Sum1),
            new MigrationInfo(3, "b", Sum2),
        };

        Assert.Throws<ArgumentException>(() => SchemaUpgradePlanner.Plan(broken, Empty()));
    }

    [Fact]
    public void Checksum_ignores_line_ending_differences()
    {
        var lf = SchemaUpgradePlanner.ChecksumOf("SELECT 1;\nGO\nSELECT 2;\n");
        var crlf = SchemaUpgradePlanner.ChecksumOf("SELECT 1;\r\nGO\r\nSELECT 2;\r\n");

        Assert.Equal(lf, crlf);
        Assert.Equal(64, lf.Length);
    }
}
