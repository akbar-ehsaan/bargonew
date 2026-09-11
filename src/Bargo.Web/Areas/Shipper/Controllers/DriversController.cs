using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>رانندگان قبلی، رانندگان مورد علاقه و امتیازدهی پس از تحویل.</summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class DriversController(BargoDbContext db, CurrentUser me, TripFlow flow) : Controller
{
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "رانندگان قبلی";

        // شمار رانندگانِ یک صاحب بار کوچک است؛ گروه‌بندی کامل و برش صفحه در حافظه
        var grouped = await db.Trips.AsNoTracking().VisibleTo(me.ToActor())
            .Where(t => t.DriverId != null)
            .GroupBy(t => t.DriverId!.Value)
            .Select(g => new
            {
                DriverId = g.Key,
                Trips = g.Count(),
                Done = g.Count(t => TripStatus.Done.Contains(t.Status)),
                LastAt = g.Max(t => t.CreatedAt),
                LastTripId = g.Max(t => t.TripId)
            })
            .OrderByDescending(x => x.LastAt)
            .ToListAsync(ct);

        var total = grouped.Count;
        const int size = 20;
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        page = Math.Clamp(page, 1, pages);
        var slice = grouped.Skip((page - 1) * size).Take(size).ToList();
        var ids = slice.Select(x => x.DriverId).ToList();
        var lastIds = slice.Select(x => x.LastTripId).ToList();

        var drivers = await db.Drivers.AsNoTracking().Where(d => ids.Contains(d.DriverId))
            .Select(d => new { d.DriverId, Name = d.FirstName + " " + d.LastName, City = d.City != null ? d.City.Name : null, Company = d.Company != null ? d.Company.Name : null, d.RatingAvg, d.RatingCount, d.TripCount })
            .ToDictionaryAsync(d => d.DriverId, ct);
        var codes = await db.Trips.AsNoTracking().Where(t => lastIds.Contains(t.TripId)).ToDictionaryAsync(t => t.TripId, t => t.Code, ct);
        var favs = await db.FavoriteDrivers.AsNoTracking().Where(f => f.ShipperId == me.Id && ids.Contains(f.DriverId)).Select(f => f.DriverId).ToListAsync(ct);

        var rows = slice.Where(x => drivers.ContainsKey(x.DriverId)).Select(x =>
        {
            var d = drivers[x.DriverId];
            return new ShipperDriverRow
            {
                DriverId = x.DriverId, Name = d.Name, City = d.City, CompanyName = d.Company,
                RatingAvg = d.RatingAvg, RatingCount = d.RatingCount, TripCount = d.TripCount,
                TripsWithMe = x.Trips, DoneWithMe = x.Done, LastTripAt = x.LastAt,
                LastTripId = x.LastTripId, LastTripCode = codes.GetValueOrDefault(x.LastTripId),
                IsFavorite = favs.Contains(x.DriverId)
            };
        }).ToList();

        return View(new PageVm<ShipperDriverRow> { Rows = rows, Page = page, PageSize = size, Total = total, Link = PageLink.For(Request) });
    }

    public async Task<IActionResult> Favorites(CancellationToken ct)
    {
        ViewData["Title"] = "رانندگان مورد علاقه";
        var myTrips = db.Trips.AsNoTracking().VisibleTo(me.ToActor());
        var rows = await db.FavoriteDrivers.AsNoTracking()
            .Where(f => f.ShipperId == me.Id)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new ShipperDriverRow
            {
                DriverId = f.DriverId,
                Name = f.Driver!.FirstName + " " + f.Driver.LastName,
                City = f.Driver.City != null ? f.Driver.City.Name : null,
                CompanyName = f.Driver.Company != null ? f.Driver.Company.Name : null,
                RatingAvg = f.Driver.RatingAvg,
                RatingCount = f.Driver.RatingCount,
                TripCount = f.Driver.TripCount,
                TripsWithMe = myTrips.Count(t => t.DriverId == f.DriverId),
                DoneWithMe = myTrips.Count(t => t.DriverId == f.DriverId && TripStatus.Done.Contains(t.Status)),
                LastTripAt = myTrips.Where(t => t.DriverId == f.DriverId).Max(t => (DateTime?)t.CreatedAt),
                LastTripId = myTrips.Where(t => t.DriverId == f.DriverId).Max(t => (int?)t.TripId),
                IsFavorite = true,
                FavoritedAt = f.CreatedAt
            })
            .ToListAsync(ct);
        return View(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Favorite(int driverId, string? returnUrl, CancellationToken ct)
    {
        if (!await RelatedAsync(driverId, ct)) return NotFound();
        if (!await db.FavoriteDrivers.AnyAsync(f => f.ShipperId == me.Id && f.DriverId == driverId, ct))
        {
            db.FavoriteDrivers.Add(new FavoriteDriver { ShipperId = me.Id, DriverId = driverId });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // دو کلیک هم‌زمان — ردیف از قبل ساخته شده (یکتایی ShipperId+DriverId)
            }
        }
        TempData["ok"] = "راننده به فهرست رانندگان مورد علاقه اضافه شد. بارهای عمومی تازه‌تان به او اعلان می‌شود.";
        return Back(returnUrl, nameof(Favorites));
    }

    [HttpPost]
    public async Task<IActionResult> Unfavorite(int driverId, string? returnUrl, CancellationToken ct)
    {
        var n = await db.FavoriteDrivers.Where(f => f.ShipperId == me.Id && f.DriverId == driverId).ExecuteDeleteAsync(ct);
        if (n == 0) return NotFound();
        TempData["ok"] = "راننده از فهرست مورد علاقه حذف شد.";
        return Back(returnUrl, nameof(Favorites));
    }

    [HttpGet]
    public async Task<IActionResult> Rate(CancellationToken ct)
    {
        ViewData["Title"] = "امتیازدهی به راننده";
        var actor = me.ToActor();

        var toRate = await db.Trips.AsNoTracking().VisibleTo(actor)
            .Where(t => TripStatus.Done.Contains(t.Status) && (t.DriverId != null || t.CompanyId != null) &&
                        !db.Ratings.Any(r => r.TripId == t.TripId && r.FromKind == Roles.Shipper && r.FromId == me.Id))
            .OrderByDescending(t => t.DeliveredAt ?? t.CreatedAt)
            .Select(ShipperTripRow.FromTrip)
            .ToListAsync(ct);

        var given = await db.Ratings.AsNoTracking()
            .Where(r => r.FromKind == Roles.Shipper && r.FromId == me.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => new ShipperRatingRow
            {
                TripId = r.TripId,
                TripCode = r.Trip!.Code,
                From = r.Trip.Load!.OriginCity!.Name,
                To = r.Trip.Load.DestCity!.Name,
                ToKind = r.ToKind,
                TargetName = r.ToKind == OwnerKind.Company
                    ? db.Companies.Where(c => c.CompanyId == r.ToId).Select(c => c.Name).FirstOrDefault() ?? ""
                    : db.Drivers.Where(d => d.DriverId == r.ToId).Select(d => d.FirstName + " " + d.LastName).FirstOrDefault() ?? "",
                Score = r.Score,
                Comment = r.Comment,
                CreatedAt = r.CreatedAt
            })
            .ToListAsync(ct);

        return View(new ShipperRateVm { ToRate = toRate, Given = given });
    }

    [HttpPost]
    public async Task<IActionResult> Rate(int tripId, int score, string? comment, string? returnUrl, CancellationToken ct)
    {
        if (!await db.Trips.AsNoTracking().VisibleTo(me.ToActor()).AnyAsync(t => t.TripId == tripId, ct)) return NotFound();
        comment = comment?.Trim();
        if (comment is { Length: > 1000 }) comment = comment[..1000];
        try
        {
            await flow.RateAsync(tripId, me.ToActor(), score, string.IsNullOrEmpty(comment) ? null : comment, ct);
            TempData["ok"] = "امتیاز شما ثبت شد. سپاس از بازخوردتان.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return Back(returnUrl, nameof(Rate));
    }

    /// <summary>راننده‌ای که روی بار من پیشنهاد داده یا سفری از من برده.</summary>
    private async Task<bool> RelatedAsync(int driverId, CancellationToken ct) =>
        await db.Offers.AsNoTracking().AnyAsync(o => o.DriverId == driverId && o.Load!.ShipperId == me.Id, ct)
        || await db.Trips.AsNoTracking().VisibleTo(me.ToActor()).AnyAsync(t => t.DriverId == driverId, ct);

    private IActionResult Back(string? returnUrl, string fallback) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(fallback);
}
