using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// «کنترل زنده» — نقشهٔ کشور، سفرها و خودروهای فعال، بارهای در حال حمل و هشدارهای سیستمی.
///
/// همه‌چیز فقط‌خواندنی است؛ اقدام از پروندهٔ سفر یا صفحهٔ مربوطه انجام می‌شود. نشانگرها
/// از <see cref="AdminOps.LiveMarkersAsync"/> می‌آیند تا داشبورد و این صفحه یک تصویر نشان دهند.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Operations)]
public class LiveController(BargoDbContext db) : Controller
{
    // ------------------------------------------------------------------
    //  نقشهٔ کشور
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "نقشهٔ کشور";
        var now = DateTime.UtcNow;
        var stale = now.AddHours(-AdminOps.StaleHours);
        var trips = db.Trips.AsNoTracking();
        var live = trips.Where(t => TripStatus.Live.Contains(t.Status));

        var vm = new LiveBoardVm
        {
            Markers = await AdminOps.LiveMarkersAsync(db, withVehicles: true, ct),
            ByStatus = await live.GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct),
            LiveTrips = await live.CountAsync(ct),
            Upcoming = await trips.CountAsync(t => TripStatus.Upcoming.Contains(t.Status), ct),
            NoGps = await live.CountAsync(t => t.LastPointAt == null || t.LastPointAt < stale, ct),
            PastEta = await live.CountAsync(t => t.EtaAt != null && t.EtaAt < now, ct),
            LoadsInTransit = await db.Loads.CountAsync(l => l.Trips.Any(t => TripStatus.Live.Contains(t.Status)), ct),
            OnlineDrivers = await db.Drivers.CountAsync(d => d.LastSeenAt >= now.AddMinutes(-15), ct),
            Recent = await AdminOps.TripRows(live.OrderByDescending(t => t.LastPointAt)).Take(8).ToListAsync(ct)
        };
        vm.IdleVehicles = vm.Markers.Count(m => m.Kind == "idle");
        vm.StaleVehicles = vm.Markers.Count(m => m.Kind == "stale");
        vm.Alerts = await AlertsCountAsync(now, ct);
        return View(vm);
    }

    /// <summary>JSON برای بازخوانی دوره‌ای نقشه (map.js هر ۳۰ ثانیه). ?trips=1 فقط سفرها، بدون خودروهای آزاد.</summary>
    public async Task<IActionResult> Data(bool trips = false, CancellationToken ct = default)
    {
        var markers = await AdminOps.LiveMarkersAsync(db, withVehicles: !trips, ct);
        return Json(new { markers, line = Array.Empty<double[]>() });
    }

    // ------------------------------------------------------------------
    //  فهرست‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Trips(string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سفرهای فعال";
        var live = db.Trips.AsNoTracking().Where(t => TripStatus.Live.Contains(t.Status));
        ViewBag.Counts = await live.GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        status = TripStatus.Live.Contains(status ?? "") ? status : null;
        if (status is not null) live = live.Where(t => t.Status == status);

        // بی‌خبرترین اول: سفری که دیرتر GPS فرستاده بالاتر می‌نشیند
        var vm = await PageVm<TripRow>.FromAsync(AdminOps.TripRows(live.OrderBy(t => t.LastPointAt == null ? 0 : 1).ThenBy(t => t.LastPointAt)), page, PageLink.For(Request), ct: ct);
        ViewBag.Status = status;
        return View(vm);
    }

    public async Task<IActionResult> Vehicles(string? q, string? show, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "خودروهای فعال";
        var now = DateTime.UtcNow;
        var since = now.AddHours(-24);
        var term = AdminOps.Term(q);
        var fa = Fa.Digits(term);

        var src = db.Vehicles.AsNoTracking().Where(v => v.LastSeenAt >= since);
        if (term.Length > 0)
            src = src.Where(v => v.PlateNo.Contains(term) || v.PlateNo.Contains(fa) ||
                                 (v.Driver != null && ((v.Driver.FirstName + " " + v.Driver.LastName).Contains(term) || v.Driver.Mobile.Contains(term))) ||
                                 (v.Company != null && v.Company.Name.Contains(term)));

        var counts = new Dictionary<string, int>
        {
            ["all"] = await src.CountAsync(ct),
            ["trip"] = await src.CountAsync(v => db.Trips.Any(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status)), ct),
            ["idle"] = await src.CountAsync(v => v.LastSeenAt >= now.AddHours(-1) && !db.Trips.Any(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status)), ct),
        };
        counts["stale"] = counts["all"] - counts["trip"] - counts["idle"];

        show = show is "trip" or "idle" or "stale" ? show : null;
        src = show switch
        {
            "trip" => src.Where(v => db.Trips.Any(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status))),
            "idle" => src.Where(v => v.LastSeenAt >= now.AddHours(-1) && !db.Trips.Any(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status))),
            "stale" => src.Where(v => v.LastSeenAt < now.AddHours(-1) && !db.Trips.Any(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status))),
            _ => src
        };

        var vm = await PageVm<VehicleRow>.FromAsync(AdminOps.VehicleRows(db, src.OrderByDescending(v => v.LastSeenAt)), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Show = show;
        ViewBag.Counts = counts;
        ViewBag.Markers = await AdminOps.LiveMarkersAsync(db, withVehicles: true, ct);
        return View(vm);
    }

    public async Task<IActionResult> Loads(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارهای در حال حمل";
        var src = db.Loads.AsNoTracking().Where(l => l.Trips.Any(t => TripStatus.Live.Contains(t.Status)));
        var vm = await PageVm<LoadRow>.FromAsync(AdminOps.LoadRows(src.OrderByDescending(l => l.LoadingFrom)), page, PageLink.For(Request), ct: ct);

        // GPS و ETA سفرِ زندهٔ هر بار — یک پرس‌وجو برای ردیف‌های همین صفحه
        var tripIds = vm.Rows.Where(r => r.TripId.HasValue).Select(r => r.TripId!.Value).ToList();
        ViewBag.Trips = tripIds.Count == 0
            ? new Dictionary<int, TripRow>()
            : await AdminOps.TripRows(db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.TripId))).ToDictionaryAsync(t => t.TripId, ct);
        ViewBag.Markers = await AdminOps.LiveMarkersAsync(db, withVehicles: false, ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  هشدارها
    // ------------------------------------------------------------------

    public const int StalePayoutHours = 48;

    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        ViewData["Title"] = "هشدارهای سیستمی";
        var now = DateTime.UtcNow;
        var stale = now.AddHours(-AdminOps.StaleHours);
        var today = Fa.TodayStartUtc;
        var live = db.Trips.AsNoTracking().Where(t => TripStatus.Live.Contains(t.Status));

        var vm = new LiveAlertsVm
        {
            NoGps = await AdminOps.TripRows(live.Where(t => t.LastPointAt == null || t.LastPointAt < stale).OrderBy(t => t.LastPointAt)).ToListAsync(ct),
            PastEta = await AdminOps.TripRows(live.Where(t => t.EtaAt != null && t.EtaAt < now).OrderBy(t => t.EtaAt)).ToListAsync(ct),
            FailedPayments = await db.Payments.AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Failed && p.CreatedAt >= today)
                .OrderByDescending(p => p.PaymentId).ToListAsync(ct),
            StalePayouts = await db.PayoutRequests.AsNoTracking()
                .Where(p => p.Status == PayoutStatus.Pending && p.CreatedAt < now.AddHours(-StalePayoutHours))
                .OrderBy(p => p.CreatedAt).ToListAsync(ct)
        };
        vm.PayerNames = await AdminOps.OwnerRefsAsync(db, vm.FailedPayments.Select(p => (p.PayerKind, p.PayerId)), ct);

        // رانندگان و خودروهای سفرهای زنده با مدرکِ تأییدشدهٔ منقضی (که مدرک تازه‌تری جایش را نگرفته)
        var liveRows = await AdminOps.TripRows(live.OrderBy(t => t.TripId)).ToListAsync(ct);
        var driverIds = liveRows.Where(r => r.DriverId.HasValue).Select(r => r.DriverId!.Value).Distinct().ToList();
        var vehicleIds = await live.Where(t => t.VehicleId != null).Select(t => t.VehicleId!.Value).Distinct().ToListAsync(ct);
        if (driverIds.Count > 0 || vehicleIds.Count > 0)
        {
            var expired = await AdminOps.NotSuperseded(db, db.Documents.AsNoTracking()
                    .Where(d => d.Status == AccountStatus.Approved && d.ExpiresAt < now &&
                                ((d.OwnerKind == OwnerKind.Driver && driverIds.Contains(d.OwnerId)) ||
                                 (d.OwnerKind == OwnerKind.Vehicle && vehicleIds.Contains(d.OwnerId)))))
                .Select(d => new { d.OwnerKind, d.OwnerId, d.Kind, d.ExpiresAt }).ToListAsync(ct);
            var tripVehicle = await live.Where(t => t.VehicleId != null).Select(t => new { t.TripId, VehicleId = t.VehicleId!.Value }).ToListAsync(ct);
            foreach (var e in expired)
            {
                var rows = e.OwnerKind == OwnerKind.Driver
                    ? liveRows.Where(r => r.DriverId == e.OwnerId)
                    : liveRows.Where(r => tripVehicle.Any(tv => tv.TripId == r.TripId && tv.VehicleId == e.OwnerId));
                foreach (var r in rows)
                    vm.ExpiredDocs.Add((r, (e.OwnerKind == OwnerKind.Vehicle ? "خودرو: " : "راننده: ") + DocumentKind.Label(e.Kind), e.ExpiresAt!.Value));
            }
            vm.ExpiredDocs = vm.ExpiredDocs.OrderBy(x => x.ExpiredAt).ToList();
        }

        // مدرکِ خودِ راننده که تاریخ انقضایش روی حساب ثبت شده (گواهینامه، کارت هوشمند) ولی ردیف Document ندارد
        if (driverIds.Count > 0)
        {
            var drv = await db.Drivers.AsNoTracking().Where(d => driverIds.Contains(d.DriverId) &&
                    ((d.LicenseExpiresAt != null && d.LicenseExpiresAt < now) || (d.SmartCardExpiresAt != null && d.SmartCardExpiresAt < now)))
                .Select(d => new { d.DriverId, d.LicenseExpiresAt, d.SmartCardExpiresAt }).ToListAsync(ct);
            foreach (var d in drv)
            foreach (var r in liveRows.Where(r => r.DriverId == d.DriverId))
            {
                if (d.LicenseExpiresAt is DateTime le && le < now && !vm.ExpiredDocs.Any(x => x.Trip.TripId == r.TripId && x.What.EndsWith(DocumentKind.Label(DocumentKind.License))))
                    vm.ExpiredDocs.Add((r, "راننده: " + DocumentKind.Label(DocumentKind.License), le));
                if (d.SmartCardExpiresAt is DateTime sc && sc < now && !vm.ExpiredDocs.Any(x => x.Trip.TripId == r.TripId && x.What.EndsWith(DocumentKind.Label(DocumentKind.SmartCard))))
                    vm.ExpiredDocs.Add((r, "راننده: " + DocumentKind.Label(DocumentKind.SmartCard), sc));
            }
        }

        return View(vm);
    }

    /// <summary>شمار کل هشدارها برای کاشی نقشهٔ کشور — همان پنج قاعدهٔ Alerts، فقط شمارش.</summary>
    private async Task<int> AlertsCountAsync(DateTime now, CancellationToken ct)
    {
        var stale = now.AddHours(-AdminOps.StaleHours);
        var today = Fa.TodayStartUtc;
        var live = db.Trips.AsNoTracking().Where(t => TripStatus.Live.Contains(t.Status));
        var n = await live.CountAsync(t => t.LastPointAt == null || t.LastPointAt < stale, ct)
                + await live.CountAsync(t => t.EtaAt != null && t.EtaAt < now, ct)
                + await db.Payments.CountAsync(p => p.Status == PaymentStatus.Failed && p.CreatedAt >= today, ct)
                + await db.PayoutRequests.CountAsync(p => p.Status == PayoutStatus.Pending && p.CreatedAt < now.AddHours(-StalePayoutHours), ct);
        var driverIds = live.Where(t => t.DriverId != null).Select(t => t.DriverId!.Value);
        var vehicleIds = live.Where(t => t.VehicleId != null).Select(t => t.VehicleId!.Value);
        n += await AdminOps.NotSuperseded(db, db.Documents.AsNoTracking()
            .Where(d => d.Status == AccountStatus.Approved && d.ExpiresAt < now &&
                        ((d.OwnerKind == OwnerKind.Driver && driverIds.Contains(d.OwnerId)) ||
                         (d.OwnerKind == OwnerKind.Vehicle && vehicleIds.Contains(d.OwnerId))))).CountAsync(ct);
        return n;
    }
}
