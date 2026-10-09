namespace AccountingSystem.Web;

/// <summary>تنظیم اتصال قابل‌به‌روزرسانی در زمان راه‌اندازی اولیه‌ی وب.</summary>
public sealed class SqlConnectionRuntime
{
    private string? _connectionString;
    private int _setupRequired;
    private string? _setupError;

    public SqlConnectionRuntime(string? connectionString, bool setupRequired, string? setupError = null)
    {
        _connectionString = connectionString;
        _setupRequired = setupRequired ? 1 : 0;
        _setupError = setupError;
    }

    public string ConnectionString => Volatile.Read(ref _connectionString)
        ?? throw new InvalidOperationException("اتصال SQL Server هنوز تنظیم نشده است.");

    public bool SetupRequired => Volatile.Read(ref _setupRequired) == 1;

    public string? SetupError => Volatile.Read(ref _setupError);

    public void Configure(string connectionString)
    {
        Volatile.Write(ref _connectionString, connectionString);
        Volatile.Write(ref _setupError, null);
        Volatile.Write(ref _setupRequired, 0);
    }

    public void RequireSetup(string? reason)
    {
        Volatile.Write(ref _setupError, reason);
        Volatile.Write(ref _setupRequired, 1);
    }
}
