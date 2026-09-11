namespace Bargo.Web.Areas.CompanyPanel.Controllers;

using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// «کنترل و رهگیری ناوگان» — نقشهٔ زنده (سفرهای در راه / رانندگان / خودروها)، مسیر
/// طی‌شدهٔ یک سفر، تابلوی وضعیت بارها و سوابق مسیر یک خودرو یا راننده در یک روز.
/// همه‌چیز فقط‌خواندنی است؛ داده از نقاطی می‌آید که اپ راننده ثبت کرده (TripFlow.RecordPoint).
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Dispatch)]
public class MonitoringController(BargoDbContext db, CurrentUser me) : Controller
{
    private const int HistoryPageSize = 50;

    /// <summary>ستون‌های تابلوی وضعیت بارها — از صف تخصیص تا تحویل.</summary>
    private static readonly string[] BoardStatuses =
    [
        TripStatus.AwaitingAssignment, TripStatus.Assigned, TripStatus.Accepted, TripStatus.ToOrigin, TripStatus.AtOrigin,
        TripStatus.Loaded, TripStatus.InTransit, TripStatus.Arrived, TripStatus.Unloaded, TripStatus.Delivered
    ];

    private static string? NormLayer(string? layer) => layer is "drivers" or "vehicles" ? layer : null;

    // =====================================================================
    //  نقشهٔ آنلاین
    // =====================================================================

    public async Task<IActionResult> Index(string? layer, CancellationToken ct)
    {
        layer = NormLayer(layer);
        ViewData["Title"] = layer switch { "drivers" => "موقعیت رانندگان", "vehicles" => "موقعیت خودروها", _ => "نقشهٔ آنلاین" };
        var cid = me.CompanyId;

        var vm = new OpsMonitorVm
        {
            Layer = layer,
            Markers = await MarkersAsync(layer, ct),
            LiveTrips = await db.Trips.AsNoTracking()
                .Where(t => t.CompanyId == cid && TripStatus.Live.Contains(t.Status))
                .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
                .Include(t => t.Load).ThenInclude(l => l!.DestCity)
                .Include(t => t.Driver).Include(t => t.Vehicle)
                .OrderByDescending(t => t.LastPointAt)
                .ToListAsync(ct)
        };
        vm.WithPosition = vm.LiveTrips.Count(t => t.LastLat != null);
        return View(vm);
    }

    /// <summary>
    /// همان نشانگرها به JSON — نقشه هر ۳۰ ثانیه این را می‌خواند. با tripId، نشانگرها و خط
    /// مسیر همان یک سفر برمی‌گردد (صفحهٔ «مسیر سفر»)؛ وگرنه لایهٔ خواسته‌شده بدون خط.
    /// </summary>
    public async Task<IActionResult> Data(string? layer, int? tripId, CancellationToken ct)
    {
        List<OpsMarker> markers;
        List<double[]> line = [];
        if (tripId is int id)
        {
            var trip = await db.Trips.AsNoTracking()
                .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
                .Include(t => t.Load).ThenInclude(l => l!.DestCity)
                .Include(t => t.Driver).Include(t => t.Vehicle)
                .FirstOrDefaultAsync(t => t.TripId == id && t.CompanyId == me.CompanyId, ct);
            if (trip is null) return NotFound();
            var points = await CompanyTrips.TripPointsAsync(db, id, ct);
            markers = CompanyTrips.TripMarkers(trip, points.Count > 0 ? points[^1] : null);
            line = CompanyTrips.Line(points);
        }
        else markers = await MarkersAsync(NormLayer(layer), ct);

        return Json(new
        {
            markers = markers.Select(m => new { lat = m.Lat, lng = m.Lng, kind = m.Kind, label = m.Label, href = m.Href }),
            line
        });
    }

    private async Task<List<OpsMarker>> MarkersAsync(string? layer, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var now = DateTime.UtcNow;

        if (layer == "drivers")
        {
            var busy = await CompanyOps.BusyDriversAsync(db, cid, ct);
            var drivers = await db.Drivers.AsNoTracking()
                .Where(d => d.CompanyId == cid && d.Status == AccountStatus.Approved && d.LastLat != null && d.LastLng != null)
                .Select(d => new { d.DriverId, d.FirstName, d.LastName, d.Mobile, d.LastLat, d.LastLng, d.LastSeenAt, d.IsAvailable })
                .ToListAsync(ct);
            return drivers.Select(d =>
            {
                var b = busy.GetValueOrDefault(d.DriverId);
                var inLive = b is not null && TripStatus.Live.Contains(b.Status);
                var state = b is { Hard: true } ? $"در سفر {b.Code}" : b is not null ? $"منتظر قبول سفر {b.Code}" : d.IsAvailable ? "آماده بار" : "خارج از خدمت";
                return new OpsMarker(d.LastLat!.Value, d.LastLng!.Value, OpsLabels.LiveKind(inLive, d.LastSeenAt),
                    $"{d.FirstName} {d.LastName}\n{Fa.Digits(d.Mobile)}\n{state}\nآخرین موقعیت: {(d.LastSeenAt is DateTime s ? Fa.Ago(s) : "—")}",
                    b is not null ? $"/Company/Trips/Detail/{b.TripId}" : $"/Company/Monitoring/History?driverId={d.DriverId}");
            }).ToList();
        }

        if (layer == "vehicles")
        {
            var busy = await CompanyOps.BusyVehiclesAsync(db, cid, ct);
            var vehicles = await db.Vehicles.AsNoTracking()
                .Where(v => v.CompanyId == cid && v.LastLat != null && v.LastLng != null)
                .Select(v => new { v.VehicleId, v.PlateNo, Type = v.VehicleType!.Name, v.Status, v.LastLat, v.LastLng, v.LastSeenAt })
                .ToListAsync(ct);
            return vehicles.Select(v =>
            {
                var b = busy.GetValueOrDefault(v.VehicleId);
                var inLive = b is not null && TripStatus.Live.Contains(b.Status);
                var state = b is { Hard: true } ? $"در سفر {b.Code} — {b.Driver}" : b is not null ? $"رزرو سفر {b.Code}" : VehicleStatus.Label(v.Status);
                return new OpsMarker(v.LastLat!.Value, v.LastLng!.Value, OpsLabels.LiveKind(inLive, v.LastSeenAt),
                    $"{v.PlateNo} · {v.Type}\n{state}\nآخرین موقعیت: {(v.LastSeenAt is DateTime s ? Fa.Ago(s) : "—")}",
                    b is not null ? $"/Company/Trips/Detail/{b.TripId}" : $"/Company/Monitoring/History?vehicleId={v.VehicleId}");
            }).ToList();
        }

        // پیش‌فرض: سفرهای در راه
        var trips = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId == cid && TripStatus.Live.Contains(t.Status) && t.LastLat != null && t.LastLng != null)
            .Select(t => new
            {
                t.TripId, t.Code, t.Status, t.LastLat, t.LastLng, t.LastPointAt, t.EtaAt,
                Driver = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null,
                Plate = t.Vehicle != null ? t.Vehicle.PlateNo : null,
                From = t.Load!.OriginCity!.Name, To = t.Load!.DestCity!.Name
            })
            .ToListAsync(ct);
        return trips.Select(t =>
        {
            var stale = t.LastPointAt is DateTime lp && now - lp > CompanyTrips.StaleAfter;
            var who = string.Join(" · ", new[] { t.Plate, t.Driver }.Where(s => !string.IsNullOrEmpty(s)));
            var eta = t.Status == TripStatus.InTransit && t.EtaAt is DateTime e ? $"\nرسیدن تقریبی: {Fa.Stamp(e)}" : "";
            return new OpsMarker(t.LastLat!.Value, t.LastLng!.Value, stale ? "stale" : "truck",
                $"سفر {t.Code} — {TripStatus.Label(t.Status)}\n{t.From} ← {t.To}\n{who}\nآخرین موقعیت: {(t.LastPointAt is DateTime at ? Fa.Ago(at) : "—")}{eta}",
                $"/Company/Trips/Detail/{t.TripId}");
        }).ToList();
    }

    // =====================================================================
    //  مسیر سفر
    // =====================================================================

    public async Task<IActionResult> Route(int? tripId, CancellationToken ct)
    {
        ViewData["Title"] = "مسیر سفر";
        var cid = me.CompanyId;

        // سفرهایی که نقطه‌ای ثبت کرده‌اند: در راه‌ها اول، بعد تازه‌ترین‌ها
        var rows = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId == cid && db.TrackingPoints.Any(p => p.TripId == t.TripId))
            .OrderBy(t => TripStatus.Live.Contains(t.Status) ? 0 : 1).ThenByDescending(t => t.LastPointAt ?? t.CreatedAt)
            .Take(200)
            .Select(t => new { t.TripId, t.Code, t.Status, From = t.Load!.OriginCity!.Name, To = t.Load!.DestCity!.Name })
            .ToListAsync(ct);
        var options = rows.Select(r => new OpsOption(r.TripId, $"{r.Code} — {r.From} ← {r.To} — {TripStatus.Label(r.Status)}")).ToList();

        var vm = new OpsRouteVm { TripOptions = options };
        var id = tripId ?? options.FirstOrDefault()?.Id;
        if (id is null) return View(vm);

        var trip = await db.Trips.AsNoTracking()
            .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Driver).Include(t => t.Vehicle)
            .FirstOrDefaultAsync(t => t.TripId == id && t.CompanyId == cid, ct);
        if (trip is null) return NotFound();
        if (options.All(o => o.Id != trip.TripId))
            options.Insert(0, new OpsOption(trip.TripId, $"{trip.Code} — {trip.Load!.OriginCity?.Name} ← {trip.Load.DestCity?.Name} — {TripStatus.Label(trip.Status)}"));

        ViewData["Title"] = $"مسیر سفر {trip.Code}";
        var pts = db.TrackingPoints.AsNoTracking().Where(p => p.TripId == trip.TripId);
        var points = await CompanyTrips.TripPointsAsync(db, trip.TripId, ct);

        vm.Trip = trip;
        vm.Line = CompanyTrips.Line(points);
        vm.Markers = CompanyTrips.TripMarkers(trip, points.Count > 0 ? points[^1] : null);
        vm.PointCount = points.Count;
        vm.MaxSpeed = await pts.MaxAsync(p => p.SpeedKmh, ct);
        vm.FirstPoint = await pts.MinAsync(p => (DateTime?)p.RecordedAt, ct);
        vm.LastPoint = await pts.MaxAsync(p => (DateTime?)p.RecordedAt, ct);
        return View(vm);
    }

    // =====================================================================
    //  وضعیت بارها (تابلو)
    // =====================================================================

    public async Task<IActionResult> Loads(CancellationToken ct)
    {
        ViewData["Title"] = "وضعیت بارها";
        var cid = me.CompanyId;
        var trips = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId == cid && BoardStatuses.Contains(t.Status))
            .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Driver).Include(t => t.Vehicle)
            .OrderBy(t => t.ScheduledDepartureAt).ThenBy(t => t.TripId)
            .ToListAsync(ct);

        var columns = BoardStatuses
            .Select(s => new OpsBoardColumn(s, trips.Where(t => t.Status == s).ToList()))
            .ToList();
        return View(columns);
    }

    // =====================================================================
    //  سوابق مسیر
    // =====================================================================

    public async Task<IActionResult> History(int? vehicleId, int? driverId, DateTime? date, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سوابق مسیر";
        var cid = me.CompanyId;

        var vm = new OpsHistoryVm
        {
            Drivers = await CompanyOps.DriverListAsync(db, cid, ct),
            Vehicles = await CompanyOps.VehicleListAsync(db, cid, ct),
            Date = date ?? DateTime.UtcNow
        };

        // فقط راننده/خودروی خودِ شرکت — شناسهٔ غریبه نادیده گرفته می‌شود
        vm.VehicleId = vehicleId is int v && vm.Vehicles.Any(o => o.Id == v) ? v : null;
        vm.DriverId = driverId is int d && vm.Drivers.Any(o => o.Id == d) ? d : null;
        if (vm.VehicleId is null && vm.DriverId is null) return View(vm);

        var dayStart = Fa.ToUtc(Fa.ToTehran(vm.Date).Date);
        var dayEnd = dayStart.AddDays(1);
        var pts = db.TrackingPoints.AsNoTracking().Where(p => p.RecordedAt >= dayStart && p.RecordedAt < dayEnd);
        pts = vm.VehicleId is int vid
            ? pts.Where(p => p.VehicleId == vid && (vm.DriverId == null || p.DriverId == vm.DriverId))
            : pts.Where(p => p.DriverId == vm.DriverId);

        var ordered = await pts.OrderBy(p => p.RecordedAt).ThenBy(p => p.TrackingPointId)
            .Select(p => new { p.Lat, p.Lng, p.StepKm, p.SpeedKmh, p.RecordedAt }).ToListAsync(ct);

        vm.Count = ordered.Count;
        vm.Km = Math.Round(ordered.Sum(p => p.StepKm), 1);
        vm.MaxSpeed = ordered.Where(p => p.SpeedKmh != null).Select(p => p.SpeedKmh).DefaultIfEmpty().Max();
        vm.First = ordered.FirstOrDefault()?.RecordedAt;
        vm.Last = ordered.LastOrDefault()?.RecordedAt;
        vm.Line = CompanyTrips.Line(ordered.Select(p => (p.Lat, p.Lng)).ToList());
        if (ordered.Count > 0)
        {
            var f = ordered[0];
            var l = ordered[^1];
            vm.Markers.Add(new OpsMarker(f.Lat, f.Lng, "origin", $"شروع روز: {Fa.Time(f.RecordedAt)}", null));
            if (ordered.Count > 1)
                vm.Markers.Add(new OpsMarker(l.Lat, l.Lng, "truck", $"آخرین نقطه: {Fa.Time(l.RecordedAt)}", null));
        }

        vm.Page = await PageVm<TrackingPoint>.FromAsync(pts.OrderByDescending(p => p.RecordedAt).ThenByDescending(p => p.TrackingPointId),
            page, PageLink.For(Request), HistoryPageSize, ct);

        var tripIds = vm.Page.Rows.Where(p => p.TripId != null).Select(p => p.TripId!.Value).Distinct().ToList();
        if (tripIds.Count > 0)
            vm.TripCodes = await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.TripId))
                .ToDictionaryAsync(t => t.TripId, t => t.Code, ct);
        return View(vm);
    }
}
