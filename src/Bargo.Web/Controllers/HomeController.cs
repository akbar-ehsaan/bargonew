using System.Security.Claims;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers;

public class HomeController(BargoDbContext db) : Controller
{
    public async Task<IActionResult> Index(int? site, CancellationToken ct)
    {
        // کاربرِ واردشده مستقیم به پنل خودش می‌رود؛ ?site=1 این پرتاب را رد می‌کند
        if (User.Identity?.IsAuthenticated == true && site != 1)
            return Redirect("/" + Roles.AreaOf(User.FindFirstValue(ClaimTypes.Role) ?? ""));

        ViewData["Title"] = "بارگو";
        ViewBag.OpenLoads = await db.Loads.CountAsync(l => LoadStatus.Market.Contains(l.Status) && l.TargetCompanyId == null, ct);
        ViewBag.Recent = await db.Loads.AsNoTracking()
            .Where(l => LoadStatus.Market.Contains(l.Status) && l.TargetCompanyId == null)
            .OrderByDescending(l => l.PublishedAt).Take(6)
            .Select(l => new { l.Code, From = l.OriginCity!.Name, To = l.DestCity!.Name, l.WeightTon, Vehicle = l.VehicleType!.Name, l.LoadingFrom })
            .ToListAsync(ct);
        return View();
    }

    /// <summary>
    /// رهگیری عمومی برای گیرنده (/t/{token}) — بدون ورود. فقط وضعیت، ETA و آخرین
    /// موقعیت؛ نه نام کامل راننده، نه موبایل، نه مبلغ.
    /// </summary>
    [HttpGet("/t/{token}")]
    public async Task<IActionResult> Track(string token, CancellationToken ct)
    {
        var trip = await db.Trips.AsNoTracking()
            .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Vehicle)
            .Include(t => t.Driver)
            .FirstOrDefaultAsync(t => t.TrackToken == token, ct);
        if (trip is null) return NotFound();
        ViewData["Title"] = $"رهگیری سفر {trip.Code}";
        return View(trip);
    }

    public async Task<IActionResult> Terms(CancellationToken ct)
    {
        ViewData["Title"] = "قوانین و مقررات";
        var item = await db.ContentItems.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == ContentKind.Rule && c.IsPublished, ct);
        return View(item);
    }

    public IActionResult Error() => View("Status", 500);

    [HttpGet("/Home/Status/{code:int}")]
    public IActionResult Status(int code)
    {
        Response.StatusCode = code;
        return View("Status", code);
    }
}
