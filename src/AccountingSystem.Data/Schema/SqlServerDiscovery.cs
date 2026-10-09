using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace AccountingSystem.Data.Schema;

/// <summary>
/// SQL Serverهای نصب‌شده‌ی محلی (از رجیستری ویندوز) و SQL Serverهایی که SQL Server Browser در شبکه معرفی می‌کند.
/// روی Windows کشف خودکار انجام می‌شود؛ وارد کردن دستی آدرس در هر سیستم‌عاملی نیز پشتیبانی می‌شود.
/// </summary>
public static class SqlServerDiscovery
{
    public static Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() => Discover(), ct);

    public static IReadOnlyList<string> Discover()
    {
        var results = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddInstalledLocalInstances(results);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var sources = SqlDataSourceEnumerator.Instance.GetDataSources();
                foreach (DataRow row in sources.Rows)
                {
                    var server = Convert.ToString(row["ServerName"], System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                    var instance = Convert.ToString(row["InstanceName"], System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                    if (string.IsNullOrWhiteSpace(server))
                    {
                        continue;
                    }

                    var endpoint = string.IsNullOrWhiteSpace(instance) ? server : server + "\\" + instance;
                    results.Add(endpoint);
                }
            }
            catch (Exception ex) when (ex is SqlException or NotSupportedException or InvalidOperationException)
            {
                // Browser ممکن است خاموش یا UDP/1434 مسدود باشد؛ آدرس دستی همچنان در دسترس است.
            }
        }

        return results.ToList();
    }

    private static void AddInstalledLocalInstances(ISet<string> results)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string instanceNamesKey = @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(instanceNamesKey, writable: false);
                if (key is null)
                {
                    continue;
                }

                foreach (var instance in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(instance))
                    {
                        continue;
                    }

                    var server = instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                        ? Environment.MachineName
                        : Environment.MachineName + "\\" + instance;
                    results.Add(server);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or PlatformNotSupportedException)
            {
                // اگر رجیستری محدود باشد، کشف SQL Browser و ورودی دستی همچنان کار می‌کنند.
            }
        }
    }
}
