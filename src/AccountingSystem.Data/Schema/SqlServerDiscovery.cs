using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace AccountingSystem.Data.Schema;

/// <summary>
/// SQL Serverهای نصب‌شده‌ی محلی (از رجیستری ویندوز) و SQL Serverهایی که SQL Server Browser در شبکه معرفی می‌کند.
/// در هر سیستم‌عاملی امکان وارد کردن دستی آدرس نیز وجود دارد، چون Browser ممکن است خاموش یا مسدود باشد.
/// </summary>
public static class SqlServerDiscovery
{
    public static Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() => Discover(), ct);

    public static IReadOnlyList<string> Discover()
    {
        var results = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddInstalledLocalInstances(results);
        AddBrowserInstances(results);
        return results.ToList();
    }

    /// <summary>
    /// SQL Server Browser: درخواست CLNT_BCAST_EX (بایت 0x02) به UDP/1434 می‌فرستد.
    /// پاسخ SRV_RESP (بایت 0x05) شامل جفت‌های کلید/مقدار UTF-16 است.
    /// </summary>
    private static void AddBrowserInstances(ISet<string> results)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
            };
            udp.Client.ReceiveTimeout = 250;
            var probe = new byte[] { 0x02 };
            udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, 1434));

            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(2))
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var response = udp.Receive(ref remote);
                    var server = ParseBrowserResponse(response, remote.Address);
                    if (!string.IsNullOrWhiteSpace(server))
                    {
                        results.Add(server);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
                {
                    // بازه‌ی کوچک timeout فرصت دریافت پاسخ‌های چند سرور را می‌دهد.
                }
            }
        }
        catch (SocketException)
        {
            // شبکه ممکن است broadcast را مسدود کرده باشد (مثلاً کانتینر/شبکه‌ی شرکتی)؛ ورودی دستی باقی است.
        }
        catch (PlatformNotSupportedException)
        {
            // برخی محیط‌ها broadcast را پشتیبانی نمی‌کنند؛ ورودی دستی باقی است.
        }
    }

    private static string? ParseBrowserResponse(byte[] packet, IPAddress address)
    {
        if (packet.Length < 4 || packet[0] != 0x05)
        {
            return null;
        }

        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1, 2));
        if (payloadLength == 0 || payloadLength > packet.Length - 3)
        {
            return null;
        }

        var payload = System.Text.Encoding.Unicode.GetString(packet, 3, payloadLength);
        var fields = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? instanceName = null;
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            if (fields[i].Equals("InstanceName", StringComparison.OrdinalIgnoreCase))
            {
                instanceName = fields[i + 1].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return null;
        }

        var host = address.ToString();
        return instanceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
            ? host
            : host + "\\" + instanceName;
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
                // اگر رجیستری محدود باشد، جست‌وجوی Browser و ورود دستی همچنان کار می‌کنند.
            }
        }
    }
}
