using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Filters;

/// <summary>
/// این کنترلر/اکشن برای حسابِ «در انتظار تأیید» هم باز است: داشبورد، پروفایل،
/// بارگذاری مدارک، تیکت و اعلان. بدون این‌ها کاربرِ تازه نمی‌تواند کاری که برای
/// تأیید لازم است انجام دهد.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowUnapprovedAttribute : Attribute;

/// <summary>
/// دروازهٔ تأیید — سراسری ثبت شده، نه روی تک‌تک کنترلرها (همان تصمیم رنگیو): با شرطِ
/// دستی، هر Area یا کنترلر تازه به‌طور پیش‌فرض باز می‌ماند و کسی هم خبردار نمی‌شود.
///
/// وضعیت هر بار از پایگاه‌داده خوانده می‌شود، نه از کوکی؛ پس تعلیق حساب همان لحظه
/// اثر دارد و کاربرِ غیرفعال‌شدهٔ شرکت بی‌درنگ بیرون می‌افتد.
/// </summary>
public sealed class ApprovalGateFilter(BargoDbContext db, CurrentUser me) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        if (!me.IsAuthenticated) return;
        if (ctx.RouteData.Values["area"] is not string area || area.Length == 0) return;

        // حساب غیرفعال (کاربر شرکت / مدیر) → خروج
        var active = me.Role switch
        {
            Roles.Company => await db.CompanyUsers.AnyAsync(u => u.CompanyUserId == me.Id && u.IsActive),
            Roles.Admin => await db.Admins.AnyAsync(a => a.AdminId == me.Id && a.IsActive),
            Roles.Driver => await db.Drivers.AnyAsync(d => d.DriverId == me.Id),
            Roles.Shipper => await db.Shippers.AnyAsync(s => s.ShipperId == me.Id),
            _ => false
        };
        if (!active)
        {
            await ctx.HttpContext.SignOutAsync();
            ctx.Result = new RedirectResult("/account/login?disabled=1");
            return;
        }

        if (ctx.ActionDescriptor.EndpointMetadata.OfType<AllowUnapprovedAttribute>().Any()) return;

        var status = me.Role switch
        {
            Roles.Driver => await db.Drivers.Where(d => d.DriverId == me.Id).Select(d => d.Status).FirstOrDefaultAsync(),
            Roles.Company => await db.Companies.Where(c => c.CompanyId == me.CompanyId).Select(c => c.Status).FirstOrDefaultAsync(),
            Roles.Shipper => await db.Shippers.Where(s => s.ShipperId == me.Id).Select(s => s.Status).FirstOrDefaultAsync(),
            _ => AccountStatus.Approved
        };
        if (status == AccountStatus.Approved) return;

        if (ctx.HttpContext.Request.Headers.Accept.ToString().Contains("application/json"))
        {
            ctx.Result = new ObjectResult(new { error = "account_not_approved", status }) { StatusCode = 403 };
            return;
        }
        ctx.Result = new RedirectResult($"/{Roles.AreaOf(me.Role)}?gate={status}");
    }
}

/// <summary>دسترسی بخشی از پنل شرکت (مالی، کاربران، …) — از پایگاه‌داده و زنده خوانده می‌شود.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireCompanyPermissionAttribute(CompanyPermission permission) : Attribute, IAsyncAuthorizationFilter
{
    public CompanyPermission Permission { get; } = permission;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var me = sp.GetRequiredService<CurrentUser>();
        if (me.Role != Roles.Company) return;

        var db = sp.GetRequiredService<BargoDbContext>();
        var perms = await db.CompanyUsers.Where(u => u.CompanyUserId == me.Id && u.IsActive)
            .Select(u => (CompanyPermission?)u.Permissions).FirstOrDefaultAsync();
        if (perms is null || !perms.Value.HasFlag(Permission))
            ctx.Result = Forbidden(ctx, "شما به این بخش از پنل شرکت دسترسی ندارید. مدیر شرکت می‌تواند از «کاربران شرکت» دسترسی بدهد.");
    }

    internal static IActionResult Forbidden(AuthorizationFilterContext ctx, string message) =>
        new ViewResult
        {
            ViewName = "~/Views/Shared/Forbidden.cshtml",
            StatusCode = 403,
            ViewData = new Microsoft.AspNetCore.Mvc.ViewFeatures.ViewDataDictionary(
                new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), ctx.ModelState)
            { ["Title"] = "دسترسی ندارید", ["Message"] = message }
        };
}

/// <summary>دسترسی بخشی از پنل مدیر. مدیر ارشد همه‌جا دسترسی دارد.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAdminPermissionAttribute(AdminPermission permission) : Attribute, IAsyncAuthorizationFilter
{
    public AdminPermission Permission { get; } = permission;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var me = sp.GetRequiredService<CurrentUser>();
        if (me.Role != Roles.Admin) return;

        var db = sp.GetRequiredService<BargoDbContext>();
        var admin = await db.Admins.AsNoTracking().FirstOrDefaultAsync(a => a.AdminId == me.Id && a.IsActive);
        if (admin is null || !admin.Can(Permission))
            ctx.Result = RequireCompanyPermissionAttribute.Forbidden(ctx, "این بخش به شما تفویض نشده است. مدیر ارشد می‌تواند از «نقش‌ها و دسترسی‌ها» آن را فعال کند.");
    }
}
