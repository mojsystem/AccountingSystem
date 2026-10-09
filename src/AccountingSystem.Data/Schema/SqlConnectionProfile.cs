using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data.Schema;

/// <summary>تنظیمات اتصال SQL Server که با SQL Authentication از راه‌انداز دریافت می‌شود.</summary>
public sealed record SqlConnectionProfile(
    string Server,
    string UserName,
    string Password,
    string DatabaseName = "master")
{
    public string BuildConnectionString(string? databaseName = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(databaseName) ? DatabaseName : databaseName,
            UserID = UserName,
            Password = Password,
            IntegratedSecurity = false,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 8,
            PersistSecurityInfo = false,
            ApplicationName = "AccountingSystem",
        };
        return builder.ConnectionString;
    }

    public static SqlConnectionProfile FromConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return new SqlConnectionProfile(
            builder.DataSource,
            builder.UserID,
            builder.Password,
            string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "master" : builder.InitialCatalog);
    }
}
