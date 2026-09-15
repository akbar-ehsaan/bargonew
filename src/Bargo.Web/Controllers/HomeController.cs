using System.Security.Claims;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers;

public class HomeController(BargoDbContext db, SettingsService settings) : Controller
{
    public async Task<IActionResult> Index(int? site, CancellationToken ct)
    {
        // کاربرِ واردشده مستقیم به پنل خودش می‌رود؛ ?site=1 این پرتاب را رد می‌کند
        if (User.Identity?.IsAuthenticated == true && site != 1)
            return Redirect("/" + Roles.AreaOf(User.FindFirstValue(ClaimTypes.Role) ?? ""));

        ViewData["Title"] = "بارگو — بار و کامیون، بدون واسطه";

        // فقط بارهای «بازار عمومی»: درخواستِ مستقیم برای یک شرکت (TargetCompanyId)
        // خصوصی است و نباید روی صفحهٔ فرود دیده شود.
        var market = db.Loads.AsNoTracking()
            .Where(l => LoadStatus.Market.Contains(l.Status) && l.TargetCompanyId == null);

        var vm = new LandingVm
        {
            Loads = await market
                .OrderByDescending(l => l.PublishedAt)
                .Take(6)
                // قاعدهٔ محرمانگی: هیچ نشانی از هویت صاحب بار (نام، شرکت، موبایل)
                // روی کارت عمومی نمی‌آید — فقط مشخصات خود بار.
                .Select(l => new LandingLoadVm
                {
                    Code = l.Code,
                    FromCity = l.OriginCity!.Name,
                    FromProvince = l.OriginCity!.Province!.Name,
                    ToCity = l.DestCity!.Name,
                    ToProvince = l.DestCity!.Province!.Name,
                    Cargo = l.Title != "" ? l.Title : l.CargoType,
                    Vehicle = l.VehicleType != null ? l.VehicleType.Name : null,
                    WeightTon = l.WeightTon,
                    LoadingFrom = l.LoadingFrom,
                    PriceMode = l.PriceMode,
                    Price = l.Price,
                    PublishedAt = l.PublishedAt ?? l.CreatedAt
                })
                .ToListAsync(ct),

            ActiveLoads = await market.CountAsync(ct),
            Vehicles = await db.Vehicles.CountAsync(ct),
            // «کاربران» یعنی جامعهٔ سه‌نقشهٔ سامانه؛ مدیران داخلی شمرده نمی‌شوند.
            Users = await db.Drivers.CountAsync(ct)
                    + await db.Shippers.CountAsync(ct)
                    + await db.Companies.CountAsync(ct),
            // «حمل موفق» = سفرِ تسویه‌شده؛ تحویل بدون تسویه هنوز تمام‌شده حساب نمی‌شود.
            SettledTrips = await db.Trips.CountAsync(t => t.Status == TripStatus.Settled, ct),
            SupportPhone = await settings.GetAsync(SettingsService.Keys.SupportPhone, ct)
        };
        return View(vm);
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

/// <summary>
/// ویومدل صفحهٔ فرود — کنار کنترلر مانده (نه در Models/ViewModels) چون فقط همین
/// یک صفحه مصرفش می‌کند و جابه‌جایی‌اش فایدهٔ عملی ندارد.
/// </summary>
public sealed class LandingVm
{
    public List<LandingLoadVm> Loads { get; init; } = [];
    public int ActiveLoads { get; init; }
    public int Vehicles { get; init; }
    public int Users { get; init; }
    public int SettledTrips { get; init; }
    public string SupportPhone { get; init; } = "";
}

/// <summary>یک بار روی کارت عمومی — عمداً بدون هیچ فیلد هویتی از صاحب بار.</summary>
public sealed class LandingLoadVm
{
    public string Code { get; init; } = "";
    public string FromCity { get; init; } = "";
    public string FromProvince { get; init; } = "";
    public string ToCity { get; init; } = "";
    public string ToProvince { get; init; } = "";
    public string Cargo { get; init; } = "";
    public string? Vehicle { get; init; }
    public decimal WeightTon { get; init; }
    public DateTime LoadingFrom { get; init; }
    public string PriceMode { get; init; } = "";
    public long? Price { get; init; }
    public DateTime PublishedAt { get; init; }
}
