namespace AccountingSystem.Core.Common;

/// <summary>ارتقای پایگاه داده انجام نشد: ناسازگاری نسخه، تغییر فایل مهاجرت منتشرشده، یا خطای اجرای یک نسخه.</summary>
public sealed class SchemaUpgradeException : Exception
{
    public SchemaUpgradeException(string message) : base(message)
    {
    }

    public SchemaUpgradeException(string message, Exception inner) : base(message, inner)
    {
    }
}
