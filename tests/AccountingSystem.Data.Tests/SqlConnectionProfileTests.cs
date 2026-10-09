using AccountingSystem.Data.Schema;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AccountingSystem.Data.Tests;

public sealed class SqlConnectionProfileTests
{
    [Fact]
    public void Connection_profile_builds_sql_authentication_with_encryption_and_database_selection()
    {
        var profile = new SqlConnectionProfile(
            @"localhost\MOJ",
            "exchange_user",
            "p;ass=word",
            "mojdb3");

        var builder = new SqlConnectionStringBuilder(profile.BuildConnectionString());

        Assert.Equal(@"localhost\MOJ", builder.DataSource);
        Assert.Equal("mojdb3", builder.InitialCatalog);
        Assert.Equal("exchange_user", builder.UserID);
        Assert.Equal("p;ass=word", builder.Password);
        Assert.False(builder.IntegratedSecurity);
        Assert.True(builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
        Assert.False(builder.PersistSecurityInfo);
    }

    [Fact]
    public void Connection_profile_can_be_read_from_a_connection_string_without_losing_credentials()
    {
        var source = new SqlConnectionStringBuilder
        {
            DataSource = @"server\instance",
            InitialCatalog = "mojdb12",
            UserID = "sql_user",
            Password = "complex;secret",
            IntegratedSecurity = false,
            Encrypt = true,
            TrustServerCertificate = true,
        }.ConnectionString;

        var profile = SqlConnectionProfile.FromConnectionString(source);

        Assert.Equal(@"server\instance", profile.Server);
        Assert.Equal("mojdb12", profile.DatabaseName);
        Assert.Equal("sql_user", profile.UserName);
        Assert.Equal("complex;secret", profile.Password);
    }
}
