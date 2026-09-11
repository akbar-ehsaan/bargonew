using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// پیشنهادهای رانندگان و شرکت‌ها. صاحب بار قیمت، خودرو، سابقه، امتیاز، شمار سفر و
/// مدارک تأییدشده را کنار هم می‌بیند و یکی را انتخاب می‌کند؛ پذیرش از
/// <see cref="TripFlow.AcceptOfferAsync"/> می‌گذرد که سفر را می‌سازد.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class OffersController(BargoDbContext db, CurrentUser me, TripFlow flow, DriverReadiness readiness) : Controller
{
    private static readonly string[] Modes = ["price", "driver", "vehicle", "rating"];

    // ------------------------------------------------------------------ فهرست

    public async Task<IActionResult> Index(int? pending, int page = 1, CancellationToken ct = default)
    {
        var onlyPending = pending == 1;
        ViewData["Title"] = onlyPending ? "قبول / رد پیشنهادها" : "پیشنهادهای رانندگان";

        var loads = db.Loads.AsNoTracking().OwnedBy(me.ToActor());
        loads = onlyPending
            ? loads.Where(l => LoadStatus.Market.Contains(l.Status) && l.Offers.Any(o => o.Status == OfferStatus.Pending))
            : loads.Where(l => l.Offers.Any(o => o.Status != OfferStatus.Withdrawn));

        var paged = await PageVm<OfferLoadGroup>.FromAsync(
            loads.OrderByDescending(l => l.Offers.Max(o => o.CreatedAt))
                .Select(l => new OfferLoadGroup
                {
                    LoadId = l.LoadId, Code = l.Code, Title = l.Title, From = l.OriginCity!.Name, To = l.DestCity!.Name,
                    Status = l.Status, WeightTon = l.WeightTon, LoadingFrom = l.LoadingFrom, PriceMode = l.PriceMode, Price = l.Price
                }),
            page, PageLink.For(Request), 10, ct);

        var ids = paged.Rows.Select(r => r.LoadId).ToList();
        var offers = await db.Offers.AsNoTracking()
            .Where(o => ids.Contains(o.LoadId) && (onlyPending ? o.Status == OfferStatus.Pending : o.Status != OfferStatus.Withdrawn))
            .Include(o => o.Driver).ThenInclude(d => d!.Company)
            .Include(o => o.Company)
            .Include(o => o.Vehicle).ThenInclude(v => v!.VehicleType)
            .OrderBy(o => o.Status == OfferStatus.Pending ? 0 : 1).ThenBy(o => o.Amount)
            .AsSplitQuery()
            .ToListAsync(ct);

        foreach (var g in paged.Rows)
        {
            g.Offers = offers.Where(o => o.LoadId == g.LoadId).Select(BasicRow).ToList();
            var lowest = g.Offers.Where(o => o.Status == OfferStatus.Pending).Select(o => (long?)o.Amount).Min();
            foreach (var o in g.Offers) o.IsLowest = o.Status == OfferStatus.Pending && o.Amount == lowest && g.Offers.Count(x => x.Status == OfferStatus.Pending) > 1;
        }

        ViewBag.OnlyPending = onlyPending;
        ViewBag.PendingTotal = await db.Offers.AsNoTracking()
            .CountAsync(o => o.Load!.ShipperId == me.Id && o.Status == OfferStatus.Pending && LoadStatus.Market.Contains(o.Load.Status), ct);
        return View(paged);
    }

    // ------------------------------------------------------------------ مقایسه

    public async Task<IActionResult> Compare(int? id, string? view, CancellationToken ct)
    {
        var mode = Modes.Contains(view) ? view! : "price";
        var actor = me.ToActor();

        var switchable = await db.Loads.AsNoTracking().OwnedBy(actor)
            .Where(l => LoadStatus.Market.Contains(l.Status))
            .OrderByDescending(l => l.Offers.Any(o => o.Status == OfferStatus.Pending))
            .ThenByDescending(l => l.LoadId)
            .Select(ShipperLoadRow.FromLoad)
            .ToListAsync(ct);

        if (id is null)
        {
            var target = switchable.Where(l => l.Status == LoadStatus.Offering && l.PendingOffers > 0)
                .OrderByDescending(l => l.LoadId).FirstOrDefault();
            if (target is not null)
                return RedirectToAction(nameof(Compare), new { id = target.LoadId, view = mode == "price" ? null : mode });

            ViewData["Title"] = "مقایسهٔ پیشنهادها";
            ViewBag.Mode = mode;
            return View("Picker", switchable);
        }

        var load = await db.Loads.AsNoTracking().OwnedBy(actor)
            .Include(l => l.OriginCity).Include(l => l.DestCity).Include(l => l.VehicleType)
            .FirstOrDefaultAsync(l => l.LoadId == id, ct);
        if (load is null) return NotFound();
        ViewData["Title"] = $"مقایسهٔ پیشنهادهای بار {load.Code}";

        var canDecide = LoadStatus.Market.Contains(load.Status);
        var offers = await db.Offers.AsNoTracking()
            .Where(o => o.LoadId == load.LoadId && (canDecide ? o.Status == OfferStatus.Pending : o.Status != OfferStatus.Withdrawn))
            .Include(o => o.Driver).ThenInclude(d => d!.Company)
            .Include(o => o.Driver).ThenInclude(d => d!.City)
            .Include(o => o.Company).ThenInclude(c => c!.City)
            .Include(o => o.Vehicle).ThenInclude(v => v!.VehicleType)
            .AsSplitQuery()
            .ToListAsync(ct);

        var rows = await EnrichAsync(offers, ct);

        var pendingRows = rows.Where(r => r.Status == OfferStatus.Pending).ToList();
        if (pendingRows.Count > 1)
        {
            var min = pendingRows.Min(r => r.Amount);
            foreach (var r in pendingRows) r.IsLowest = r.Amount == min;
        }

        rows = mode switch
        {
            "rating" => rows.OrderByDescending(r => r.RatingCount > 0).ThenByDescending(r => r.RatingAvg).ThenByDescending(r => r.RatingCount).ThenBy(r => r.Amount).ToList(),
            "driver" => rows.OrderByDescending(r => r.TripCount).ThenByDescending(r => r.RatingAvg).ThenBy(r => r.Amount).ToList(),
            "vehicle" => rows.OrderByDescending(r => r.CapacityTon.HasValue && r.CapacityTon >= load.WeightTon).ThenByDescending(r => r.VerifiedDocs).ThenBy(r => r.Amount).ToList(),
            _ => rows.OrderBy(r => r.Status == OfferStatus.Accepted ? 0 : 1).ThenBy(r => r.Amount).ToList()
        };

        var tripId = canDecide ? null : await db.Trips.AsNoTracking()
            .Where(t => t.LoadId == load.LoadId && t.Status != TripStatus.Cancelled)
            .OrderByDescending(t => t.TripId).Select(t => (int?)t.TripId).FirstOrDefaultAsync(ct);

        return View(new OfferCompareVm
        {
            Load = load, Rows = rows, Mode = mode, CanDecide = canDecide, TripId = tripId,
            Switchable = switchable.Where(l => l.PendingOffers > 0 || l.LoadId == load.LoadId).ToList()
        });
    }

    // ------------------------------------------------------------------ پذیرش و رد

    [HttpPost]
    public async Task<IActionResult> Accept(int id, CancellationToken ct)
    {
        var loadId = await db.Offers.AsNoTracking().Where(o => o.OfferId == id && o.Load!.ShipperId == me.Id)
            .Select(o => (int?)o.LoadId).FirstOrDefaultAsync(ct);
        if (loadId is null) return NotFound();

        try
        {
            var trip = await flow.AcceptOfferAsync(id, me.ToActor(), ct);
            TempData["ok"] = $"پیشنهاد پذیرفته شد و سفارش {trip.Code} ساخته شد. برای قطعی شدن حمل، کرایهٔ {Fa.Toman(trip.Fare)} را از کیف پول پرداخت کنید.";
            return RedirectToAction("Detail", "Orders", new { id = trip.TripId });
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
            return RedirectToAction(nameof(Compare), new { id = loadId });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int id, string? back, CancellationToken ct)
    {
        var loadId = await db.Offers.AsNoTracking().Where(o => o.OfferId == id && o.Load!.ShipperId == me.Id)
            .Select(o => (int?)o.LoadId).FirstOrDefaultAsync(ct);
        if (loadId is null) return NotFound();

        try
        {
            await flow.RejectOfferAsync(id, me.ToActor(), ct);
            TempData["ok"] = "پیشنهاد رد شد و به پیشنهاددهنده اعلان شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return back switch
        {
            "pending" => RedirectToAction(nameof(Index), new { pending = 1 }),
            "all" => RedirectToAction(nameof(Index)),
            _ => RedirectToAction(nameof(Compare), new { id = loadId })
        };
    }

    // ------------------------------------------------------------------ مشخصات راننده

    public async Task<IActionResult> Driver(int id, CancellationToken ct)
    {
        // فقط راننده‌ای که روی بار من پیشنهاد داده یا سفری از من برده
        var related = await db.Offers.AsNoTracking().AnyAsync(o => o.DriverId == id && o.Load!.ShipperId == me.Id, ct)
                      || await db.Trips.AsNoTracking().VisibleTo(me.ToActor()).AnyAsync(t => t.DriverId == id, ct);
        if (!related) return NotFound();

        var d = await db.Drivers.AsNoTracking().Include(x => x.City).Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.DriverId == id, ct);
        if (d is null) return NotFound();
        ViewData["Title"] = $"مشخصات راننده — {d.FullName}";

        var vehicles = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType)
            .Where(v => v.DriverId == id).OrderByDescending(v => v.Status == VehicleStatus.Active).ThenByDescending(v => v.VehicleId)
            .ToListAsync(ct);

        // خودروهای شرکتی که این راننده با آن‌ها بار من را برده
        var tripVehicleIds = await db.Trips.AsNoTracking().VisibleTo(me.ToActor())
            .Where(t => t.DriverId == id && t.VehicleId != null).Select(t => t.VehicleId!.Value).Distinct().ToListAsync(ct);
        var extra = tripVehicleIds.Except(vehicles.Select(v => v.VehicleId)).ToList();
        if (extra.Count > 0)
            vehicles.AddRange(await db.Vehicles.AsNoTracking().Include(v => v.VehicleType).Where(v => extra.Contains(v.VehicleId)).ToListAsync(ct));

        var vehicleIds = vehicles.Select(v => v.VehicleId).ToList();
        var docs = await db.Documents.AsNoTracking()
            .Where(x => x.Status == AccountStatus.Approved &&
                        ((x.OwnerKind == OwnerKind.Driver && x.OwnerId == id) ||
                         (x.OwnerKind == OwnerKind.Vehicle && vehicleIds.Contains(x.OwnerId))))
            .OrderByDescending(x => x.UploadedAt)
            .ToListAsync(ct);
        var badges = docs
            .GroupBy(x => (x.OwnerKind, x.OwnerId, x.Kind)).Select(g => g.First())
            .Select(x => new DocBadge
            {
                Kind = x.Kind,
                ExpiresAt = x.ExpiresAt,
                Subject = x.OwnerKind == OwnerKind.Vehicle ? "خودروی " + (vehicles.FirstOrDefault(v => v.VehicleId == x.OwnerId)?.PlateNo ?? "") : "راننده"
            })
            .OrderBy(b => b.Subject != "راننده").ThenBy(b => b.Subject).ToList();

        var reviews = await db.Ratings.AsNoTracking()
            .Where(r => r.ToKind == OwnerKind.Driver && r.ToId == id)
            .OrderByDescending(r => r.CreatedAt).Take(10).ToListAsync(ct);

        var trips = await db.Trips.AsNoTracking().VisibleTo(me.ToActor()).Where(t => t.DriverId == id)
            .OrderByDescending(t => t.TripId).Take(20).Select(ShipperTripRow.FromTrip).ToListAsync(ct);

        var myOffers = await db.Offers.AsNoTracking()
            .Where(o => o.DriverId == id && o.Load!.ShipperId == me.Id && o.Status == OfferStatus.Pending && LoadStatus.Market.Contains(o.Load.Status))
            .Include(o => o.Driver).Include(o => o.Vehicle).ThenInclude(v => v!.VehicleType)
            .ToListAsync(ct);

        return View(new ShipperDriverProfileVm
        {
            DriverId = d.DriverId,
            Name = d.FullName,
            City = d.City?.Name,
            CompanyName = d.Company?.Name,
            Status = d.Status,
            RatingAvg = d.RatingAvg,
            RatingCount = d.RatingCount,
            TripCount = d.TripCount,
            MemberSince = d.CreatedAt,
            IsFavorite = await db.FavoriteDrivers.AnyAsync(f => f.ShipperId == me.Id && f.DriverId == id, ct),
            Vehicles = vehicles,
            Docs = badges,
            Reviews = reviews,
            TripsWithMe = trips,
            OffersOnMyLoads = myOffers.Select(BasicRow).ToList()
        });
    }

    // ------------------------------------------------------------------ ساخت ردیف‌ها

    private static OfferCompareRow BasicRow(Offer o) => new()
    {
        OfferId = o.OfferId,
        LoadId = o.LoadId,
        CarrierKind = o.CarrierKind,
        DriverId = o.DriverId,
        CompanyId = o.CompanyId,
        CarrierName = o.CarrierName,
        DriverCompany = o.Driver?.Company?.Name,
        Amount = o.Amount,
        EtaHours = o.EtaHours,
        Note = o.Note,
        Status = o.Status,
        CreatedAt = o.CreatedAt,
        RatingAvg = o.Company?.RatingAvg ?? o.Driver?.RatingAvg ?? 0,
        RatingCount = o.Company?.RatingCount ?? o.Driver?.RatingCount ?? 0,
        TripCount = o.Driver?.TripCount ?? 0,
        VehicleFromOffer = o.Vehicle is not null,
        VehicleType = o.Vehicle?.VehicleType?.Name,
        BodyKind = o.Vehicle?.VehicleType?.BodyKind,
        PlateNo = o.Vehicle?.PlateNo,
        CapacityTon = o.Vehicle?.CapacityTon,
        Brand = o.Vehicle?.Brand,
        VehicleModel = o.Vehicle?.Model,
        Year = o.Vehicle?.Year,
        InsuranceExpiresAt = o.Vehicle?.InsuranceExpiresAt,
        InspectionExpiresAt = o.Vehicle?.InspectionExpiresAt,
        VehicleVerify = o.Vehicle?.VerifyStatus
    };

    /// <summary>سابقه، خودرو، مدارک و آمادگی هر پیشنهاددهنده — برای جدول مقایسه.</summary>
    private async Task<List<OfferCompareRow>> EnrichAsync(List<Offer> offers, CancellationToken ct)
    {
        var rows = offers.Select(BasicRow).ToList();
        var driverIds = offers.Where(o => o.DriverId != null).Select(o => o.DriverId!.Value).Distinct().ToList();
        var companyIds = offers.Where(o => o.CompanyId != null).Select(o => o.CompanyId!.Value).Distinct().ToList();

        // خودروی فعال رانندهٔ مستقل، وقتی پیشنهاد خودرو را مشخص نکرده
        var activeVehicles = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType)
            .Where(v => v.DriverId != null && driverIds.Contains(v.DriverId.Value) && v.Status == VehicleStatus.Active)
            .OrderByDescending(v => v.VehicleId)
            .ToListAsync(ct);

        var vehicleIds = offers.Where(o => o.VehicleId != null).Select(o => o.VehicleId!.Value)
            .Concat(activeVehicles.Select(v => v.VehicleId)).Distinct().ToList();

        var docs = await db.Documents.AsNoTracking()
            .Where(x => x.Status == AccountStatus.Approved &&
                        ((x.OwnerKind == OwnerKind.Driver && driverIds.Contains(x.OwnerId)) ||
                         (x.OwnerKind == OwnerKind.Vehicle && vehicleIds.Contains(x.OwnerId)) ||
                         (x.OwnerKind == OwnerKind.Company && companyIds.Contains(x.OwnerId))))
            .Select(x => new { x.OwnerKind, x.OwnerId, x.Kind, x.ExpiresAt })
            .ToListAsync(ct);
        int ValidDocs(string kind, int ownerId) => docs
            .Where(x => x.OwnerKind == kind && x.OwnerId == ownerId && (x.ExpiresAt == null || x.ExpiresAt >= DateTime.UtcNow))
            .Select(x => x.Kind).Distinct().Count();

        var myTrips = db.Trips.AsNoTracking().VisibleTo(me.ToActor());
        var withMeDriver = await myTrips.Where(t => t.DriverId != null && driverIds.Contains(t.DriverId.Value) && TripStatus.Done.Contains(t.Status))
            .GroupBy(t => t.DriverId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var withMeCompany = await myTrips.Where(t => t.CompanyId != null && companyIds.Contains(t.CompanyId.Value) && TripStatus.Done.Contains(t.Status))
            .GroupBy(t => t.CompanyId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);

        var companyTrips = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId != null && companyIds.Contains(t.CompanyId.Value) && TripStatus.Done.Contains(t.Status))
            .GroupBy(t => t.CompanyId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);

        var fleets = await db.Vehicles.AsNoTracking()
            .Where(v => v.CompanyId != null && companyIds.Contains(v.CompanyId.Value) && v.Status == VehicleStatus.Active && v.VerifyStatus == AccountStatus.Approved)
            .GroupBy(v => v.CompanyId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);

        var cancelDriver = await db.TripEvents.AsNoTracking()
            .Where(e => e.ToStatus == TripStatus.Cancelled && e.ActorKind == Roles.Driver && driverIds.Contains(e.ActorId))
            .GroupBy(e => e.ActorId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var cancelCompany = await db.TripEvents.AsNoTracking()
            .Where(e => e.ToStatus == TripStatus.Cancelled && e.ActorKind == Roles.Company && e.Trip!.CompanyId != null && companyIds.Contains(e.Trip.CompanyId.Value))
            .GroupBy(e => e.Trip!.CompanyId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);

        var favorites = await db.FavoriteDrivers.AsNoTracking().Where(f => f.ShipperId == me.Id && driverIds.Contains(f.DriverId))
            .Select(f => f.DriverId).ToListAsync(ct);

        var readyByDriver = new Dictionary<(int, int?), List<DriverReadiness.Issue>>();

        foreach (var (row, offer) in rows.Zip(offers))
        {
            if (offer.Driver is { } drv)
            {
                row.City = drv.City?.Name;
                row.MemberSince = drv.CreatedAt;
                row.TripsWithMe = withMeDriver.GetValueOrDefault(drv.DriverId);
                row.CancelledByCarrier = cancelDriver.GetValueOrDefault(drv.DriverId);
                row.IsFavorite = favorites.Contains(drv.DriverId);

                if (offer.Vehicle is null && activeVehicles.FirstOrDefault(v => v.DriverId == drv.DriverId) is { } av)
                {
                    row.VehicleType = av.VehicleType?.Name;
                    row.BodyKind = av.VehicleType?.BodyKind;
                    row.PlateNo = av.PlateNo;
                    row.CapacityTon = av.CapacityTon;
                    row.Brand = av.Brand;
                    row.VehicleModel = av.Model;
                    row.Year = av.Year;
                    row.InsuranceExpiresAt = av.InsuranceExpiresAt;
                    row.InspectionExpiresAt = av.InspectionExpiresAt;
                    row.VehicleVerify = av.VerifyStatus;
                }

                var vehicleId = offer.VehicleId ?? activeVehicles.FirstOrDefault(v => v.DriverId == drv.DriverId)?.VehicleId;
                row.VerifiedDocs = ValidDocs(OwnerKind.Driver, drv.DriverId) + (vehicleId is int vid ? ValidDocs(OwnerKind.Vehicle, vid) : 0);

                var key = (drv.DriverId, offer.VehicleId);
                if (!readyByDriver.TryGetValue(key, out var issues))
                {
                    issues = await readiness.CheckAsync(drv.DriverId, offer.VehicleId, ct);
                    readyByDriver[key] = issues;
                }
                row.Ready = DriverReadiness.IsReady(issues);
                row.Issues = issues.Where(i => i.Blocking).Select(i => i.Text).ToList();
            }
            else if (offer.Company is { } co)
            {
                row.City = co.City?.Name;
                row.MemberSince = co.CreatedAt;
                row.TripCount = companyTrips.GetValueOrDefault(co.CompanyId);
                row.TripsWithMe = withMeCompany.GetValueOrDefault(co.CompanyId);
                row.CancelledByCarrier = cancelCompany.GetValueOrDefault(co.CompanyId);
                row.FleetSize = fleets.GetValueOrDefault(co.CompanyId);
                row.VerifiedDocs = ValidDocs(OwnerKind.Company, co.CompanyId) + (offer.VehicleId is int cvid ? ValidDocs(OwnerKind.Vehicle, cvid) : 0);
                row.Ready = co.Status == AccountStatus.Approved;
                if (!row.Ready) row.Issues = [$"وضعیت حساب شرکت: {AccountStatus.Label(co.Status)}"];
            }
        }
        return rows;
    }
}
