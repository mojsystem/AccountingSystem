using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>
/// قواعد ساختار سرفصل: چهار سطح (گروه ← کل ← معین ← تفصیلی)، کد، نوع و تغییرات مجاز.
/// نوع حساب از گروه به همه‌ی زیرمجموعه‌ها به ارث می‌رسد. فقط حساب‌های بی‌زیرمجموعه و فعال سند می‌گیرند.
/// </summary>
public static class AccountRules
{
    /// <summary>بیشترین عمق ساختار (تفصیلی).</summary>
    public const int MaxLevel = 4;

    public const int MaxCodeLength = 20;

    public const int MaxNameLength = 100;

    private static readonly IReadOnlyDictionary<string, string> TypeNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Asset"] = "دارایی",
        ["Liability"] = "بدهی",
        ["Equity"] = "سرمایه",
        ["Revenue"] = "درآمد",
        ["Expense"] = "هزینه",
    };

    /// <summary>همه‌ی نوع‌های حساب به ترتیب نمایش (برای فهرست‌ها).</summary>
    public static IReadOnlyCollection<string> AccountTypes => TypeNames.Keys.ToList();

    /// <summary>نام فارسی نوع حساب (برای نمایش).</summary>
    public static string TypeName(string accountType) =>
        TypeNames.TryGetValue(accountType, out var name) ? name : accountType;

    /// <summary>نام فارسی سطح حساب.</summary>
    public static string LevelName(int level) => level switch
    {
        1 => "گروه",
        2 => "کل",
        3 => "معین",
        _ => "تفصیلی",
    };

    public static string CleanCode(string? code)
    {
        var value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new BusinessRuleException("کد حساب را وارد کنید.");
        }
        if (value.Length > MaxCodeLength)
        {
            throw new BusinessRuleException($"کد حساب نمی‌تواند بیش از {MaxCodeLength} کاراکتر باشد.");
        }
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
            {
                throw new BusinessRuleException("کد حساب فقط می‌تواند شامل حروف لاتین، عدد و خط تیره (-) باشد.");
            }
        }
        return value;
    }

    public static string CleanName(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new BusinessRuleException("نام حساب را وارد کنید.");
        }
        if (value.Length > MaxNameLength)
        {
            throw new BusinessRuleException($"نام حساب نمی‌تواند بیش از {MaxNameLength} کاراکتر باشد.");
        }
        return value;
    }

    public static string CleanType(string? accountType)
    {
        var value = (accountType ?? string.Empty).Trim();
        if (!TypeNames.ContainsKey(value))
        {
            throw new BusinessRuleException("نوع حساب معتبر نیست.");
        }
        return value;
    }

    /// <summary>
    /// جایگاه حساب تازه را کنترل می‌کند و سطح آن را برمی‌گرداند.
    /// parent = null یعنی گروه تازه (سطح ۱) که کدش باید یک رقم بین ۱ تا ۹ باشد.
    /// </summary>
    public static int ValidateNew(string code, AccountInfo? parent, string accountType)
    {
        if (parent is null)
        {
            if (code.Length != 1 || code[0] < '1' || code[0] > '9')
            {
                throw new BusinessRuleException("کد گروه باید یک رقم بین ۱ تا ۹ باشد.");
            }
            return 1;
        }
        if (parent.IsSystem)
        {
            throw new BusinessRuleException($"حساب «{parent.Code}» پایه‌ی موتور حسابداری است و زیرمجموعه نمی‌گیرد.");
        }
        if (parent.Level >= MaxLevel)
        {
            throw new BusinessRuleException("حساب تفصیلی زیرمجموعه نمی‌گیرد؛ ساختار حداکثر چهار سطح است.");
        }
        if (code.Length <= parent.Code.Length || !code.StartsWith(parent.Code, StringComparison.Ordinal))
        {
            throw new BusinessRuleException($"کد حساب باید با «{parent.Code}» شروع شود و از آن بلندتر باشد.");
        }
        if (!string.Equals(accountType, parent.AccountType, StringComparison.Ordinal))
        {
            throw new BusinessRuleException($"نوع حساب زیرمجموعه باید «{TypeName(parent.AccountType)}» باشد؛ نوع از حساب پدر به ارث می‌رسد.");
        }
        return parent.Level + 1;
    }

    /// <summary>
    /// ویرایش حساب موجود را کنترل می‌کند. نام همیشه قابل ویرایش است.
    /// خروجی سطح نهایی حساب است.
    /// </summary>
    public static int ValidateChange(AccountInfo current, string newCode, string newType, string? newParentCode, AccountInfo? newParent, bool isActive)
    {
        var placementChanged = newCode != current.Code || newParentCode != current.ParentCode || newType != current.AccountType;

        if (current.IsSystem)
        {
            if (newCode != current.Code)
            {
                throw new BusinessRuleException("کد حساب‌های پایه‌ی موتور حسابداری قابل تغییر نیست.");
            }
            if (newParentCode != current.ParentCode)
            {
                throw new BusinessRuleException("حساب پدر حساب‌های پایه‌ی موتور حسابداری قابل تغییر نیست.");
            }
            if (newType != current.AccountType)
            {
                throw new BusinessRuleException("نوع حساب‌های پایه‌ی موتور حسابداری قابل تغییر نیست.");
            }
            if (!isActive)
            {
                throw new BusinessRuleException("حساب‌های پایه‌ی موتور حسابداری را نمی‌توان غیرفعال کرد.");
            }
            return current.Level;
        }

        if (!isActive && current.IsActive && current.HasChildren)
        {
            throw new BusinessRuleException("حساب دارای زیرمجموعه را نمی‌توان غیرفعال کرد.");
        }
        if (current.HasChildren && placementChanged)
        {
            throw new BusinessRuleException("حساب دارای زیرمجموعه را نمی‌توان کد، حساب پدر یا نوع آن را تغییر داد.");
        }
        if (newCode != current.Code && current.HasPostings)
        {
            throw new BusinessRuleException("کد حسابی که سند دارد قابل تغییر نیست.");
        }
        if (!placementChanged)
        {
            return current.Level;
        }
        return ValidateNew(newCode, newParent, newType);
    }

    /// <summary>حساب فقط وقتی حذف می‌شود که پایه‌ی موتور نباشد، زیرمجموعه و سند نداشته باشد.</summary>
    public static void ValidateDelete(AccountInfo account)
    {
        if (account.IsSystem)
        {
            throw new BusinessRuleException("حساب‌های پایه‌ی موتور حسابداری حذف نمی‌شوند؛ می‌توانید نام آن‌ها را تغییر دهید.");
        }
        if (account.HasChildren)
        {
            throw new BusinessRuleException("حساب دارای زیرمجموعه را نمی‌توان حذف کرد.");
        }
        if (account.HasPostings)
        {
            throw new BusinessRuleException("حسابی که سند دارد حذف نمی‌شود؛ می‌توانید آن را غیرفعال کنید.");
        }
    }
}
