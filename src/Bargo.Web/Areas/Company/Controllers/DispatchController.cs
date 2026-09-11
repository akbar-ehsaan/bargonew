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
/// «تخصیص بار» — راننده و خودرو را به سفرهای شرکتی می‌سپارد. خودِ تخصیص همیشه از
/// <see cref="TripFlow.AssignAsync"/> می‌گذرد (آمادگی راننده، مشغولیت، ردّ TripAssignments)؛
/// اینجا فقط صف، گزینه‌ها و پیشنهادها ساخته می‌شود و یک شرط اضافه: خودرویی که در سفر
/// دیگری است تخصیص داده نمی‌شود.
///
/// «تخصیص راننده» و «تخصیص خودرو» (و «تغییر راننده/خودرو») یک صفحه‌اند؛ focus فقط
/// تعیین می‌کند کدام ستون پررنگ باشد.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Dispatch)]
public class DispatchController(BargoDbContext db, CurrentUser me, TripFlow flow, DriverReadiness readiness) : Controller
{
    private static readonly string[] QueueStatuses = [TripStatus.AwaitingAssignment, TripStatus.Assigned];

    /// <summary>سفرهای شرکتی که راننده دارند و هنوز تمام نشده‌اند — نامزد تعویض.</summary>
    private static readonly string[] ChangeableStatuses =
    [
        TripStatus.Assigned, TripStatus.Accepted, TripStatus.ToOrigin, TripStatus.AtOrigin,
        TripStatus.Loaded, TripStatus.InTransit, TripStatus.Arrived, TripStatus.Unloaded
    ];

    private IQueryable<Trip> CompanyTripsQuery() =>
        db.Trips.AsNoTracking().Where(t => t.CompanyId == me.CompanyId && t.CarrierKind == CarrierKind.Company);

    private static IQueryable<Trip> WithRefs(IQueryable<Trip> q) =>
        q.Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Load).ThenInclude(l => l!.Shipper)
            .Include(t => t.Load).ThenInclude(l => l!.VehicleType)
            .Include(t => t.Driver).Include(t => t.Vehicle).ThenInclude(v => v!.VehicleType);

    // =====================================================================
    //  صف تخصیص
    // =====================================================================

    public async Task<IActionResult> Index(string? focus, int page = 1, CancellationToken ct = default)
    {
        var focusVehicle = focus == "vehicle";
        ViewData["Title"] = focusVehicle ? "تخصیص خودرو" : "تخصیص راننده";
        var cid = me.CompanyId;

        var q = CompanyTripsQuery().Where(t => QueueStatuses.Contains(t.Status))
            .OrderBy(t => t.Status == TripStatus.AwaitingAssignment ? 0 : 1)
            .ThenBy(t => t.ScheduledDepartureAt);

        var vm = new OpsDispatchVm
        {
            FocusVehicle = focusVehicle,
            Page = await PageVm<Trip>.FromAsync(WithRefs(q), page, PageLink.For(Request), pageSize: 20, ct: ct),
            Drivers = await CompanyOps.DriverOptionsAsync(db, readiness, cid, null, null, ct),
            Vehicles = await CompanyOps.VehicleOptionsAsync(db, cid, ct)
        };

        var ids = vm.Page.Rows.Select(t => t.TripId).ToList();
        if (ids.Count > 0)
        {
            var assignments = await db.TripAssignments.AsNoTracking().Include(a => a.Driver)
                .Where(a => ids.Contains(a.TripId))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.TripAssignmentId)
                .ToListAsync(ct);
            // ردیفِ «رد مأموریت»: راننده همان رانندهٔ قبلی است (ApplyAsync → AwaitingAssignment)
            vm.Rejections = assignments.Where(a => a.PreviousDriverId == a.DriverId)
                .GroupBy(a => a.TripId).ToDictionary(g => g.Key, g => g.First());
            vm.AssignedAt = assignments.Where(a => a.PreviousDriverId != a.DriverId)
                .GroupBy(a => a.TripId).ToDictionary(g => g.Key, g => g.First().CreatedAt);
        }
        return View(vm);
    }

    // =====================================================================
    //  تخصیص یک سفر
    // =====================================================================

    [HttpGet]
    public async Task<IActionResult> Assign(int id, string? focus, string? back, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var trip = await WithRefs(CompanyTripsQuery()).Include(t => t.Company).AsSplitQuery()
            .FirstOrDefaultAsync(t => t.TripId == id, ct);
        if (trip is null) return NotFound();

        if (trip.Status is TripStatus.Delivered or TripStatus.Settled or TripStatus.Cancelled)
        {
            TempData["err"] = $"سفر {trip.Code} پایان یافته و تخصیص آن قابل تغییر نیست.";
            return RedirectToAction("Detail", "Trips", new { id });
        }

        ViewData["Title"] = (trip.DriverId is null ? "تخصیص سفر " : "تعویض راننده/خودروی سفر ") + trip.Code;
        var l = trip.Load!;
        var drivers = await CompanyOps.DriverOptionsAsync(db, readiness, cid, l.OriginLat ?? l.OriginCity?.Lat, l.OriginLng ?? l.OriginCity?.Lng, ct);
        var history = await db.TripAssignments.AsNoTracking().Include(a => a.Driver).Include(a => a.Vehicle)
            .Where(a => a.TripId == id)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.TripAssignmentId)
            .ToListAsync(ct);

        var vm = new OpsAssignVm
        {
            Form = new OpsAssignFormVm
            {
                Trip = trip, Drivers = drivers, Vehicles = await CompanyOps.VehicleOptionsAsync(db, cid, ct),
                Back = back is "index" or "change" or "trip" or "schedule" ? back : "assign",
                FocusVehicle = focus == "vehicle", SelectedDriverId = trip.DriverId
            },
            // رانندگان آزاد و آماده، نزدیک‌ترین به مبدا
            Suggestions = drivers.Where(d => d.Selectable(id) && d.DriverId != trip.DriverId && d.IsAvailable && d.Busy is null)
                .OrderBy(d => d.DistanceKm ?? double.MaxValue).ThenByDescending(d => d.RatingAvg)
                .Take(5).ToList(),
            History = history,
            Rejection = history.FirstOrDefault(a => a.PreviousDriverId == a.DriverId),
            Customer = l.CompanyId == cid ? (await CompanyOps.CustomersOfLoadsAsync(db, cid, [l.LoadId], ct)).GetValueOrDefault(l.LoadId) : null
        };
        return View(vm);
    }

    /// <summary>
    /// تخصیص یا تعویض. تاریخ حرکت اختیاری است و روی خودِ سفر می‌نشیند (وضعیت را عوض نمی‌کند).
    /// back می‌گوید پس از ثبت به کدام صفحه برگردیم؛ focus ستون پررنگ همان صفحه را نگه می‌دارد.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Assign(int tripId, int? driverId, int? vehicleId, string? reason, DateTime? departure, int? hour,
        string? back, string? focus, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var trip = await db.Trips.FirstOrDefaultAsync(t => t.TripId == tripId && t.CompanyId == cid && t.CarrierKind == CarrierKind.Company, ct);
        if (trip is null) return NotFound();

        focus = focus == "vehicle" ? "vehicle" : null;
        reason = CompanyTrips.Clean(reason, 500);
        try
        {
            if (driverId is not int did) throw new UserError("راننده را انتخاب کنید.");
            if (trip.DriverId is not null && (trip.DriverId != did || trip.VehicleId != vehicleId) && reason is null)
                throw new UserError("برای تعویض راننده یا خودرو، علت تغییر را بنویسید.");

            if (vehicleId is int vid)
            {
                var busy = (await CompanyOps.BusyVehiclesAsync(db, cid, ct)).GetValueOrDefault(vid);
                if (busy is { Hard: true } b && b.TripId != tripId)
                    throw new UserError($"این خودرو در سفر {b.Code} است و تا پایان آن قابل تخصیص نیست.");
            }

            await flow.AssignAsync(tripId, did, vehicleId, me.ToActor(), reason, ct);

            if (departure is DateTime dep)
            {
                var h = hour ?? (trip.ScheduledDepartureAt is DateTime cur ? Fa.ToTehran(cur).Hour : 8);
                trip.ScheduledDepartureAt = Fa.WithHour(dep, h);
                await db.SaveChangesAsync(ct);
            }

            var driverName = await db.Drivers.Where(d => d.DriverId == did).Select(d => d.FirstName + " " + d.LastName).FirstAsync(ct);
            TempData["ok"] = trip.Status == TripStatus.Assigned
                ? $"سفر {trip.Code} به {driverName.Trim()} سپرده شد؛ منتظر قبول مأموریت از اپ راننده."
                : $"تخصیص سفر {trip.Code} به‌روز شد و به راننده اطلاع داده شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }

        return back switch
        {
            "change" => RedirectToAction(nameof(Change), new { focus }),
            "trip" => RedirectToAction("Detail", "Trips", new { id = tripId }),
            "schedule" => RedirectToAction(nameof(Schedule)),
            "assign" => RedirectToAction(nameof(Assign), new { id = tripId, focus }),
            _ => RedirectToAction(nameof(Index), new { focus })
        };
    }

    // =====================================================================
    //  تغییر راننده / خودرو
    // =====================================================================

    public async Task<IActionResult> Change(string? focus, int page = 1, CancellationToken ct = default)
    {
        var focusVehicle = focus == "vehicle";
        ViewData["Title"] = focusVehicle ? "تغییر خودرو" : "تغییر راننده";
        var cid = me.CompanyId;

        var q = CompanyTripsQuery().Where(t => t.DriverId != null && ChangeableStatuses.Contains(t.Status))
            .OrderBy(t => TripStatus.Live.Contains(t.Status) ? 0 : 1).ThenBy(t => t.ScheduledDepartureAt);

        var vm = new OpsChangeVm
        {
            FocusVehicle = focusVehicle,
            Page = await PageVm<Trip>.FromAsync(WithRefs(q), page, PageLink.For(Request), pageSize: 20, ct: ct),
            Drivers = await CompanyOps.DriverOptionsAsync(db, readiness, cid, null, null, ct),
            Vehicles = await CompanyOps.VehicleOptionsAsync(db, cid, ct)
        };

        var ids = vm.Page.Rows.Select(t => t.TripId).ToList();
        if (ids.Count > 0)
        {
            var rows = await db.TripAssignments.AsNoTracking().Include(a => a.Driver).Include(a => a.Vehicle)
                .Where(a => ids.Contains(a.TripId))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.TripAssignmentId)
                .ToListAsync(ct);
            vm.History = rows.GroupBy(a => a.TripId).ToDictionary(g => g.Key, g => g.ToList());
        }
        return View(vm);
    }

    // =====================================================================
    //  برنامه‌ریزی حمل — ۱۴ روز آینده
    // =====================================================================

    public async Task<IActionResult> Schedule(CancellationToken ct)
    {
        ViewData["Title"] = "برنامه‌ریزی حمل";
        const int days = 14;
        var start = Fa.TodayStartUtc;
        var end = start.AddDays(days);

        var trips = await WithRefs(CompanyTripsQuery()
                .Where(t => TripStatus.Upcoming.Contains(t.Status) || TripStatus.Live.Contains(t.Status))
                .Where(t => t.ScheduledDepartureAt == null || t.ScheduledDepartureAt < end))
            .OrderBy(t => t.ScheduledDepartureAt).ThenBy(t => t.TripId)
            .ToListAsync(ct);
        // سفرِ در راه با تاریخ گذشته دیگر «برنامه» نیست؛ در نقشهٔ آنلاین دیده می‌شود
        trips = trips.Where(t => !(TripStatus.Live.Contains(t.Status) && t.ScheduledDepartureAt < start)).ToList();

        var vm = new OpsScheduleVm { Days14 = days, Total = trips.Count, Unassigned = trips.Count(t => t.DriverId is null) };

        // عقب‌افتاده: زمان حرکتش گذشته ولی هنوز راه نیفتاده
        var overdue = trips.Where(t => t.ScheduledDepartureAt < start && TripStatus.Upcoming.Contains(t.Status)).ToList();
        if (overdue.Count > 0)
            vm.Days.Add(new OpsScheduleDay("عقب‌افتاده", "زمان حرکت گذشته و سفر هنوز شروع نشده", "no", overdue));

        for (var i = 0; i < days; i++)
        {
            var dayStart = start.AddDays(i);
            var dayEnd = dayStart.AddDays(1);
            var list = trips.Where(t => t.ScheduledDepartureAt >= dayStart && t.ScheduledDepartureAt < dayEnd).ToList();
            var sub = i == 0 ? "امروز" : i == 1 ? "فردا" : CompanyTrips.WeekDay(dayStart);
            var tone = list.Count == 0 ? "mut" : list.Any(t => t.DriverId is null) ? "wait" : "ok";
            vm.Days.Add(new OpsScheduleDay(Fa.LongDate(dayStart.AddHours(12)), sub, tone, list));
        }

        var undated = trips.Where(t => t.ScheduledDepartureAt is null).ToList();
        if (undated.Count > 0)
            vm.Days.Add(new OpsScheduleDay("بدون زمان حرکت", "از صفحهٔ تخصیص، تاریخ حرکت را تعیین کنید", "wait", undated));

        return View(vm);
    }
}
