using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Xunit;

namespace AccountingSystem.Core.Tests;

public sealed class BackupRulesTests
{
    [Fact]
    public void Backup_file_name_states_the_reason_and_the_time()
    {
        var name = BackupRules.BuildFileName("AccountingSystem", BackupReason.BeforeUpgrade, new DateTime(2026, 10, 9, 14, 5, 6, 7));

        Assert.Equal("AccountingSystem_before-upgrade_20261009-140506-007.bak", name);
    }

    [Fact]
    public void Backup_file_name_replaces_characters_that_are_unsafe_in_paths()
    {
        var name = BackupRules.BuildFileName("My DB!", BackupReason.Manual, new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.StartsWith("My_DB__manual_", name);
        Assert.EndsWith(".bak", name);
    }

    [Theory]
    [InlineData(@"C:\Backup", @"C:\Backup\a.bak")]
    [InlineData(@"C:\Backup\", @"C:\Backup\a.bak")]
    [InlineData("/var/opt/mssql/data", "/var/opt/mssql/data/a.bak")]
    [InlineData("/var/opt/mssql/data/", "/var/opt/mssql/data/a.bak")]
    public void Server_path_uses_the_separator_of_the_server_folder(string folder, string expected)
    {
        Assert.Equal(expected, BackupRules.CombineServerPath(folder, "a.bak"));
    }

    [Fact]
    public void Empty_backup_folder_is_refused()
    {
        Assert.Throws<BusinessRuleException>(() => BackupRules.CombineServerPath("  ", "a.bak"));
    }

    [Fact]
    public void Restore_path_is_trimmed_and_must_be_a_bak_file()
    {
        Assert.Equal(@"C:\x\b.bak", BackupRules.CleanRestorePath("  C:\\x\\b.bak  "));
        Assert.Throws<BusinessRuleException>(() => BackupRules.CleanRestorePath(@"C:\x\b.txt"));
        Assert.Throws<BusinessRuleException>(() => BackupRules.CleanRestorePath(""));
        Assert.Throws<BusinessRuleException>(() => BackupRules.CleanRestorePath("C:\\x\\b\n.bak"));
    }
}
