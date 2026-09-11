using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// رهگیری بار: نقشه با مبدا، مقصد، آخرین موقعیت خودرو و مسیر طی‌شده؛ مراحل سفر؛ زمان
/// تقریبی رسیدن؛ و پیوند رهگیری عمومی برای گیرنده. پارامتر view فقط تأکید صفحه را
/// عوض می‌کند (نقشهٔ بزرگ / مراحل / ETA)، نه داده را.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class TrackingController(BargoDbContext db, CurrentUser me, TripFlow flow) : Controller
{
    private const int MaxLinePoints = 1500;

    public async Task<IActionResult> Index(int? tripId, string? view, CancellationToken ct)
    {
        var mode = view is "route" or "status" or "eta" ? view : "";
        ViewData["Title"] = mode switch
        {
            "route" => "مسیر روی نقشه",
            "status" => "وضعیت لحظه‌ای بار",
            "eta" => "زمان تقریبی رسیدن",
            _ => "موقعیت خودرو"
        };
        var actor = me.ToActor();

        var picker = await db.Trips.AsNoTracking().VisibleTo(actor)
            .Where(t => TripStatus.Live.Contains(t.Status) || TripStatus.Upcoming.Contains(t.Status))
            .OrderBy(t => TripStatus.Live.Contains(t.Status) ? 0 : 1)
            .ThenByDescending(t => t.LastPointAt ?? t.CreatedAt)
            .Select(ShipperTripRow.FromTrip)
            .ToListAsync(ct);

        var id = tripId ?? picker.FirstOrDefault()?.TripId;
        if (id is null)
            return View(new ShipperTrackingVm { Picker = picker, Mode = mode });

        var trip = await flow.FindForAsync(id.Value, actor, ct);
        if (trip is null) return NotFound();

        // سفرِ خواسته‌شده ممکن است تمام‌شده باشد و در فهرست «جاری» نباشد
        if (picker.All(p => p.TripId != trip.TripId))
            picker.Insert(0, await db.Trips.AsNoTracking().Where(t => t.TripId == trip.TripId).Select(ShipperTripRow.FromTrip).FirstAsync(ct));

        var points = await db.TrackingPoints.AsNoTracking().Where(p => p.TripId == trip.TripId)
            .OrderBy(p => p.RecordedAt).Select(p => new { p.Lat, p.Lng, p.SpeedKmh }).ToListAsync(ct);
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / (double)MaxLinePoints));
        var line = points.Where((_, i) => i % step == 0).Select(p => new[] { p.Lat, p.Lng }).ToList();
        if (points.Count > 0 && (points.Count - 1) % step != 0) line.Add([points[^1].Lat, points[^1].Lng]);

        var l = trip.Load!;
        var markers = new List<object>();
        if (l.OriginLat is double ola && l.OriginLng is double olg)
            markers.Add(new { lat = ola, lng = olg, kind = "origin", label = $"مبدا: {l.OriginCity?.Name}\n{l.OriginAddress}" });
        if (l.DestLat is double dla && l.DestLng is double dlg)
            markers.Add(new { lat = dla, lng = dlg, kind = "dest", label = $"مقصد: {l.DestCity?.Name}\n{l.DestAddress}" });

        double? lastLat = trip.LastLat, lastLng = trip.LastLng;
        if (lastLat is null && points.Count > 0) { lastLat = points[^1].Lat; lastLng = points[^1].Lng; }
        if (lastLat is double tla && lastLng is double tlg && trip.Status != TripStatus.Cancelled)
        {
            var stale = trip.LastPointAt is DateTime lp && DateTime.UtcNow - lp > TimeSpan.FromMinutes(30);
            var who = trip.Revealed ? trip.Vehicle?.PlateNo ?? trip.Driver?.FullName ?? "خودرو" : "خودروی حامل بار";
            var label = $"{who}\nآخرین موقعیت: {(trip.LastPointAt is DateTime at ? Fa.Ago(at) : "—")}";
            markers.Add(new { lat = tla, lng = tlg, kind = TripStatus.Live.Contains(trip.Status) ? (stale ? "stale" : "truck") : "idle", label });
        }

        var events = await db.TripEvents.AsNoTracking().Where(e => e.TripId == trip.TripId)
            .OrderByDescending(e => e.CreatedAt).ToListAsync(ct);

        ViewData["Title"] += $" — سفر {trip.Code}";
        return View(new ShipperTrackingVm
        {
            Trip = trip,
            Picker = picker,
            Events = events,
            Mode = mode,
            MapJson = System.Text.Json.JsonSerializer.Serialize(new { markers, line, fit = true }),
            ShareUrl = $"{Request.Scheme}://{Request.Host}/t/{trip.TrackToken}",
            RemainingKm = Geo.RoadKm(lastLat, lastLng, l.DestLat, l.DestLng),
            PointCount = points.Count,
            SpeedKmh = points.Count > 0 ? points[^1].SpeedKmh : null
        });
    }

    public async Task<IActionResult> History(int? tripId, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سوابق موقعیت";
        var actor = me.ToActor();

        var picker = await db.Trips.AsNoTracking().VisibleTo(actor)
            .Where(t => db.TrackingPoints.Any(p => p.TripId == t.TripId))
            .OrderByDescending(t => t.LastPointAt ?? t.CreatedAt)
            .Select(ShipperTripRow.FromTrip)
            .ToListAsync(ct);

        var id = tripId ?? picker.FirstOrDefault()?.TripId;
        if (id is null) return View(new ShipperTrackHistoryVm { Picker = picker });

        var trip = await db.Trips.AsNoTracking().VisibleTo(actor).Where(t => t.TripId == id)
            .Select(ShipperTripRow.FromTrip).FirstOrDefaultAsync(ct);
        if (trip is null) return NotFound();
        if (picker.All(p => p.TripId != trip.TripId)) picker.Insert(0, trip);
        ViewData["Title"] = $"سوابق موقعیت سفر {trip.Code}";

        var pts = db.TrackingPoints.AsNoTracking().Where(p => p.TripId == trip.TripId);
        var firstAt = await pts.MinAsync(p => (DateTime?)p.RecordedAt, ct);
        var lastAt = await pts.MaxAsync(p => (DateTime?)p.RecordedAt, ct);
        var avgSpeed = await pts.Where(p => p.SpeedKmh != null).AverageAsync(p => p.SpeedKmh, ct);
        var maxSpeed = await pts.MaxAsync(p => p.SpeedKmh, ct);

        return View(new ShipperTrackHistoryVm
        {
            Trip = trip,
            Picker = picker,
            Points = await PageVm<TrackingPoint>.FromAsync(pts.OrderByDescending(p => p.RecordedAt).ThenByDescending(p => p.TrackingPointId),
                page, PageLink.For(Request), 50, ct),
            FirstAt = firstAt,
            LastAt = lastAt,
            AvgSpeed = avgSpeed,
            MaxSpeed = maxSpeed
        });
    }
}
