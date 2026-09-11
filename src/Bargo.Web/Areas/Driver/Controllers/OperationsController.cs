using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// «عملیات سفر» — همان گام‌های صفحهٔ سفر، ولی با دکمه‌های درشت برای راننده‌ای که پشت
/// فرمان است. هر گام فقط وقتی فعال است که <see cref="TripFlow.NextSteps"/> اجازه بدهد؛
/// ثبت‌ها به Trips/Move و Trips/Deliver می‌روند و با back=ops به همین صفحه برمی‌گردند.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class OperationsController(BargoDbContext db, CurrentUser me, TripFlow flow, SettingsService settings) : Controller
{
    private static readonly string[] Steps = ["to_origin", "at_origin", "loaded", "in_transit", "arrived", "delivered"];

    public async Task<IActionResult> Index(string? step, CancellationToken ct)
    {
        step = step is not null && Steps.Contains(step) ? step : null;
        ViewData["Title"] = step switch
        {
            "to_origin" => "حرکت به سمت مبدا",
            "at_origin" => "اعلام حضور در مبدا",
            "loaded" => "تأیید بارگیری",
            "in_transit" => "شروع سفر",
            "arrived" => "اعلام رسیدن",
            "delivered" => "تأیید تخلیه و تحویل بار",
            _ => "عملیات سفر"
        };

        var vm = new OperationsVm { Step = step };
        vm.LiveCount = await db.Trips.CountAsync(t => t.DriverId == me.Id && TripStatus.Live.Contains(t.Status), ct);
        if (await db.CurrentTripIdAsync(me.Id, ct) is int id && await flow.FindForAsync(id, me.ToActor(), ct) is { } trip)
            vm.Detail = await db.TripDetailAsync(trip, me.Id, settings, Request, ct);
        return View(vm);
    }

    /// <summary>
    /// اشتراک موقعیت. ارسال خودکار در panel.js است و با localStorage['bg:track'] خاموش
    /// می‌شود؛ این صفحه همان کلید را تغییر می‌دهد و آخرین نقاطِ رسیده به سرور را نشان
    /// می‌دهد تا راننده ببیند ارسال واقعاً کار می‌کند.
    /// </summary>
    public async Task<IActionResult> Location(CancellationToken ct)
    {
        ViewData["Title"] = "اشتراک موقعیت";
        var meId = me.Id;
        var d = await db.Drivers.AsNoTracking().Where(x => x.DriverId == meId)
            .Select(x => new { x.IsAvailable, x.LastLat, x.LastLng, x.LastSeenAt }).FirstAsync(ct);
        var live = await db.Trips.AsNoTracking()
            .Where(t => t.DriverId == meId && TripStatus.Live.Contains(t.Status))
            .OrderByDescending(t => t.TripId).Select(t => new { t.TripId, t.Code }).FirstOrDefaultAsync(ct);
        var today = Fa.TodayStartUtc;

        var vm = new LocationVm
        {
            IsAvailable = d.IsAvailable,
            LastLat = d.LastLat,
            LastLng = d.LastLng,
            LastSeenAt = d.LastSeenAt,
            IntervalMin = Math.Max(1, await settings.GetIntAsync(SettingsService.Keys.TrackIntervalMin, ct)),
            LiveTripId = live?.TripId,
            LiveTripCode = live?.Code,
            PointsToday = await db.TrackingPoints.CountAsync(p => p.DriverId == meId && p.RecordedAt >= today, ct),
            Recent = await db.TrackingPoints.AsNoTracking().Where(p => p.DriverId == meId)
                .OrderByDescending(p => p.RecordedAt).Take(15)
                .Select(p => new TrackRowVm(p.RecordedAt, p.Lat, p.Lng, p.SpeedKmh, p.AccuracyM,
                    db.Trips.Where(t => t.TripId == p.TripId).Select(t => t.Code).FirstOrDefault()))
                .ToListAsync(ct)
        };
        return View(vm);
    }
}
