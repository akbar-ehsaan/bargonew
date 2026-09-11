using System.Security.Claims;
using Bargo.Web.Models.Entities;

namespace Bargo.Web.Services;

/// <summary>دسترسی سریع به هویت کاربر جاری از روی کوکی ورود.</summary>
public class CurrentUser(IHttpContextAccessor accessor)
{
    public const string ClaimName = "name";
    /// <summary>شناسهٔ شرکت — فقط برای کاربران پنل شرکت.</summary>
    public const string ClaimCompany = "cid";

    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;
    public string Role => User?.FindFirstValue(ClaimTypes.Role) ?? "";
    public string Name => User?.FindFirstValue(ClaimName) ?? "";

    /// <summary>
    /// شناسهٔ ردیفِ حساب: DriverId، ShipperId، AdminId — و برای پنل شرکت،
    /// CompanyUserId (نه CompanyId؛ آن در <see cref="CompanyId"/> است).
    /// </summary>
    public int Id => int.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var v) ? v : 0;

    public int CompanyId => int.TryParse(User?.FindFirstValue(ClaimCompany), out var v) ? v : 0;

    /// <summary>
    /// صاحبِ کیف پول، اعلان و تیکت. برای شرکت خودِ شرکت است نه کاربرِ واردشده،
    /// تا همهٔ کاربران یک شرکت یک کیف پول و یک صندوق اعلان ببینند.
    /// </summary>
    public (string Kind, int Id) Owner => Role == Roles.Company ? (OwnerKind.Company, CompanyId) : (Role, Id);

    public Actor ToActor() => new(Role, Id, Name, Role == Roles.Company ? CompanyId : null);
}
