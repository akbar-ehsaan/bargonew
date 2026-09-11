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
/// «سفرها» — پروندهٔ هر سفرِ شرکت: مراحل، مالی، راننده/خودرو، مسیر، بارنامه و اسناد.
/// شرکت ممکن است حمل‌کنندهٔ سفر باشد (IsCarrier) یا فقط صاحبِ بار (سفری که راننده یا
/// شرکتی دیگر برای مشتریِ او می‌برد). گام‌های سفر و بارنامه فقط برای حمل‌کننده باز است؛
/// لغو و بارگذاری سند برای هر دو. هیچ گذاری اینجا نوشته نمی‌شود — همه از TripFlow.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Dispatch)]
public class TripsController(
    BargoDbContext db,
    CurrentUser me,
    TripFlow flow,
    SettingsService settings,
    DocumentStorage storage,
    NotificationService notify) : Controller
{
    private static readonly string[] Planned = [TripStatus.AwaitingAssignment, TripStatus.Assigned, TripStatus.Accepted];

    public async Task<IActionResult> Index(string? tab, int page = 1, CancellationToken ct = default)
    {
        tab = tab is "current" or "done" or "cancelled" ? tab : "planned";
        ViewData["Title"] = tab switch
        {
            "current" => "سفرهای جاری",
            "done" => "سفرهای تکمیل‌شده",
            "cancelled" => "سفرهای لغوشده",
            _ => "سفرهای برنامه‌ریزی‌شده"
        };

        var mine = db.Trips.AsNoTracking().VisibleTo(me.ToActor());
        var byStatus = await mine.GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        int Sum(Func<string, bool> p) => byStatus.Where(x => p(x.Key)).Sum(x => x.N);

        var vm = new OpsTripsVm { Tab = tab, CompanyId = me.CompanyId };
        vm.Counts["planned"] = Sum(s => Planned.Contains(s));
        vm.Counts["current"] = Sum(s => TripStatus.Live.Contains(s));
        vm.Counts["done"] = Sum(s => TripStatus.Done.Contains(s));
        vm.Counts["cancelled"] = Sum(s => s == TripStatus.Cancelled);

        IQueryable<Trip> q = tab switch
        {
            "current" => mine.Where(t => TripStatus.Live.Contains(t.Status)).OrderByDescending(t => t.LastPointAt ?? t.StartedAt ?? t.CreatedAt),
            "done" => mine.Where(t => TripStatus.Done.Contains(t.Status)).OrderByDescending(t => t.DeliveredAt),
            "cancelled" => mine.Where(t => t.Status == TripStatus.Cancelled).OrderByDescending(t => t.CancelledAt),
            _ => mine.Where(t => Planned.Contains(t.Status)).OrderBy(t => t.ScheduledDepartureAt ?? t.CreatedAt)
        };
        vm.Page = await PageVm<Trip>.FromAsync(
            q.Include(t => t.Load).ThenInclude(l => l!.OriginCity)
                .Include(t => t.Load).ThenInclude(l => l!.DestCity)
                .Include(t => t.Load).ThenInclude(l => l!.Shipper)
                .Include(t => t.Driver).Include(t => t.Vehicle).Include(t => t.Company).Include(t => t.Waybill),
            page, PageLink.For(Request), ct: ct);
        return View(vm);
    }

    // =====================================================================
    //  پروندهٔ سفر
    // =====================================================================

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        ViewData["Title"] = $"سفر {trip.Code}";

        var isCarrier = trip.CompanyId == cid;
        var isLoadOwner = trip.Load!.CompanyId == cid;
        var points = await CompanyTrips.TripPointsAsync(db, id, ct);

        var vm = new OpsTripDetailVm
        {
            Trip = trip,
            IsCarrier = isCarrier,
            IsLoadOwner = isLoadOwner,
            Steps = isCarrier ? TripFlow.NextSteps(trip.Status, Roles.Company).ToList() : [],
            CanCancel = TripFlow.CanCancel(trip.Status, Roles.Company),
            DeliveryOtp = await settings.GetBoolAsync(SettingsService.Keys.DeliveryOtp, ct),
            RequireWaybill = await settings.GetBoolAsync(SettingsService.Keys.RequireWaybill, ct),
            Events = await db.TripEvents.AsNoTracking().Where(e => e.TripId == id).OrderByDescending(e => e.CreatedAt).ToListAsync(ct),
            Assignments = await db.TripAssignments.AsNoTracking().Include(a => a.Driver).Include(a => a.Vehicle)
                .Where(a => a.TripId == id).OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.TripAssignmentId).ToListAsync(ct),
            Documents = await db.TripDocuments.AsNoTracking().Where(d => d.TripId == id).OrderByDescending(d => d.CreatedAt).ToListAsync(ct),
            Line = CompanyTrips.Line(points),
            Markers = CompanyTrips.TripMarkers(trip, points.Count > 0 ? points[^1] : null),
            Customer = isLoadOwner ? (await CompanyOps.CustomersOfLoadsAsync(db, cid, [trip.LoadId], ct)).GetValueOrDefault(trip.LoadId) : null,
            Expenses = isCarrier ? await db.CompanyExpenses.Where(e => e.CompanyId == cid && e.TripId == id).SumAsync(e => (long?)e.Amount, ct) ?? 0 : 0,
            CanWaybill = isCarrier && trip.Waybill?.Status != "verified"
                         && trip.Status is not (TripStatus.AwaitingAssignment or TripStatus.Assigned or TripStatus.Settled or TripStatus.Cancelled)
        };
        return View(vm);
    }

    // =====================================================================
    //  گام‌های سفر
    // =====================================================================

    [HttpPost]
    public async Task<IActionResult> Move(int id, string? to, string? note, CancellationToken ct)
    {
        var trip = await CarrierTripAsync(id, ct);
        if (trip is null) return NotFound();
        // لغو، فرم و علتِ اجباری خودش را دارد
        if (string.IsNullOrEmpty(to) || to == TripStatus.Cancelled) return BadRequest();

        try
        {
            await flow.MoveAsync(trip, to, me.ToActor(), CompanyTrips.Clean(note, 500), ct: ct);
            TempData["ok"] = to switch
            {
                TripStatus.AwaitingAssignment => $"تخصیص سفر {trip.Code} برداشته شد؛ سفر به صف تخصیص برگشت.",
                TripStatus.Unloaded when trip.DeliveryOtpHash is not null =>
                    "تخلیه ثبت شد و کد تحویل برای گیرنده پیامک شد. راننده کد را از گیرنده می‌گیرد و ثبت می‌کند.",
                TripStatus.Delivered when trip.Status == TripStatus.Settled => $"تحویل ثبت و سفر {trip.Code} تسویه شد.",
                _ => $"«{TripFlow.ActionLabel(to)}» برای سفر {trip.Code} ثبت شد."
            };
            if (to == TripStatus.AwaitingAssignment) return RedirectToAction("Assign", "Dispatch", new { id });
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            await flow.CancelAsync(trip, me.ToActor(), CompanyTrips.Clean(reason, 500) ?? "", ct);
            TempData["ok"] = $"سفر {trip.Code} لغو شد." + (trip.IsProblem ? " چون بار روی خودرو بود، سفر برای بررسی به مدیر سامانه ارجاع شد." : "");
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    // =====================================================================
    //  بارنامه و اسناد
    // =====================================================================

    [HttpPost]
    public async Task<IActionResult> Waybill(int id, string? number, IFormFile? file, string? back, CancellationToken ct)
    {
        var trip = await CarrierTripAsync(id, ct);
        if (trip is null) return NotFound();
        try
        {
            var no = await CompanyTrips.RegisterWaybillAsync(flow, storage, trip, number, file, me.ToActor(), ct);
            TempData["ok"] = $"بارنامهٔ شمارهٔ {no} برای سفر {trip.Code} ثبت شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return back == "issue" ? RedirectToAction("Issue", "Waybills") : RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Document(int id, string? kind, string? title, IFormFile? file, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            var t = await CompanyTrips.AddDocumentAsync(db, storage, notify, trip, kind, title, file, me.ToActor(), ct);
            TempData["ok"] = $"«{t}» به اسناد سفر {trip.Code} افزوده شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>سفری که شرکت حمل‌کنندهٔ آن است — فقط این‌ها گام و بارنامه می‌گیرند.</summary>
    private async Task<Trip?> CarrierTripAsync(int id, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        return trip is not null && trip.CompanyId == me.CompanyId ? trip : null;
    }
}
