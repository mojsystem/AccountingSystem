using System.Globalization;
using System.Security.Claims;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace AccountingSystem.Web;

public static class WebExtensions
{
    public static CurrentUser ToCurrentUser(this ClaimsPrincipal principal)
    {
        var idText = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("شناسه کاربر در نشست یافت نشد.");
        var username = principal.Identity?.Name ?? string.Empty;
        var fullName = principal.FindFirst("FullName")?.Value ?? username;
        var roleText = principal.FindFirst(ClaimTypes.Role)?.Value ?? nameof(UserRole.Cashier);
        return new CurrentUser(
            int.Parse(idText, CultureInfo.InvariantCulture),
            username,
            fullName,
            Enum.Parse<UserRole>(roleText));
    }

    public static Task SignInUserAsync(this HttpContext httpContext, CurrentUser user, bool persistent)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("FullName", user.FullName),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = persistent });
    }
}

/// <summary>قالب‌بندی مقادیر برای نمایش در Razor.</summary>
public static class ViewFormat
{
    public static string Money(decimal? value, int decimals = 0) =>
        value.HasValue ? MoneyMath.FormatAmount(value.Value, decimals) : "-";

    public static string Quantity(decimal value) => MoneyMath.FormatRate(value);

    public static string Rate(decimal value) => MoneyMath.FormatRate(value);

    public static string Date(DateTime value) => PersianDate.FormatDateTime(value);

    public static string TradeText(TradeType type) => type == TradeType.Buy ? "خرید از مشتری" : "فروش به مشتری";

    public static string RoleText(UserRole role) => role == UserRole.Admin ? "مدیر" : "کاربر صندوق";
}
