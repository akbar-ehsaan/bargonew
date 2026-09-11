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
/// «بارنامه و اسناد» — ثبت شمارهٔ بارنامهٔ سفرهایی که به مرحلهٔ بارگیری رسیده‌اند،
/// فهرست بارنامه‌های شرکت و اسناد سفرها (رسید بارگیری/تحویل، بیمه‌نامه، تصویر).
/// ثبت بارنامه از TripFlow.RegisterWaybill می‌گذرد تا رویداد سفر ساخته شود.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Dispatch)]
public class WaybillsController(
    BargoDbContext db,
    CurrentUser me,
    TripFlow flow,
    SettingsService settings,
    DocumentStorage storage,
    NotificationService notify) : Controller
{
    /// <summary>مراحلی که بارنامه در آن‌ها معنا دارد و هنوز دیر نشده.</summary>
    private static readonly string[] NeedsWaybill = [TripStatus.AtOrigin, TripStatus.Loaded, TripStatus.InTransit];

    // =====================================================================
    //  صدور / ثبت بارنامه
    // =====================================================================

    [HttpGet]
    public async Task<IActionResult> Issue(CancellationToken ct)
    {
        ViewData["Title"] = "صدور/ثبت بارنامه";
        var cid = me.CompanyId;
        var vm = new OpsWaybillIssueVm
        {
            RequireWaybill = await settings.GetBoolAsync(SettingsService.Keys.RequireWaybill, ct),
            Trips = await db.Trips.AsNoTracking()
                .Where(t => t.CompanyId == cid && NeedsWaybill.Contains(t.Status) && (t.Waybill == null || t.Waybill.Status == "void"))
                .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
                .Include(t => t.Load).ThenInclude(l => l!.DestCity)
                .Include(t => t.Driver).Include(t => t.Vehicle).Include(t => t.Waybill)
                .OrderBy(t => t.Status == TripStatus.Loaded ? 0 : t.Status == TripStatus.AtOrigin ? 1 : 2).ThenBy(t => t.LoadedAt)
                .ToListAsync(ct),
            Registered = await db.Waybills.CountAsync(w => w.Trip!.CompanyId == cid && w.Status != "void", ct)
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Issue(int tripId, string? number, IFormFile? file, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(tripId, me.ToActor(), ct);
        if (trip is null || trip.CompanyId != me.CompanyId) return NotFound();
        try
        {
            var no = await CompanyTrips.RegisterWaybillAsync(flow, storage, trip, number, file, me.ToActor(), ct);
            TempData["ok"] = $"بارنامهٔ شمارهٔ {no} برای سفر {trip.Code} ثبت شد." +
                             (trip.Status == TripStatus.Loaded ? " راننده اکنون می‌تواند «شروع سفر» را بزند." : "");
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Issue));
    }

    // =====================================================================
    //  بارنامه‌های شرکت
    // =====================================================================

    public async Task<IActionResult> Index(string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارنامه‌های شرکت";
        var cid = me.CompanyId;
        var query = db.Waybills.AsNoTracking().Where(w => w.Trip!.CompanyId == cid);

        q = CompanyTrips.Clean(q, 40);
        if (q is not null)
        {
            var latin = Fa.Latin(q);
            query = query.Where(w => w.Number.Contains(latin) || w.Trip!.Code.Contains(latin) || w.Trip!.Load!.Title.Contains(q));
        }

        var vm = new OpsWaybillsVm
        {
            Q = q,
            Page = await PageVm<Waybill>.FromAsync(
                query.Include(w => w.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.OriginCity)
                    .Include(w => w.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.DestCity)
                    .Include(w => w.Trip).ThenInclude(t => t!.Driver)
                    .Include(w => w.Trip).ThenInclude(t => t!.Vehicle)
                    .OrderByDescending(w => w.IssuedAt),
                page, PageLink.For(Request), ct: ct)
        };
        return View(vm);
    }

    // =====================================================================
    //  اسناد بار
    // =====================================================================

    public async Task<IActionResult> Documents(string? kind, int page = 1, CancellationToken ct = default)
    {
        kind = kind is not null && CompanyTrips.AllDocKinds.Contains(kind) ? kind : null;
        ViewData["Title"] = kind switch
        {
            TripDocumentKind.CargoInsurance => "بیمه بار",
            TripDocumentKind.DeliveryReceipt => "رسید تحویل",
            TripDocumentKind.LoadingReceipt => "رسید بارگیری",
            TripDocumentKind.Photo => "تصاویر بار",
            _ => "اسناد بار"
        };
        var cid = me.CompanyId;

        // اسناد سفرهایی که شرکت حمل‌کننده‌شان است یا بارشان را ثبت کرده
        var all = db.TripDocuments.AsNoTracking().Where(d => d.Trip!.CompanyId == cid || d.Trip!.Load!.CompanyId == cid);
        var vm = new OpsTripDocsVm
        {
            Kind = kind,
            Counts = await all.GroupBy(d => d.Kind).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct),
            Trips = await db.Trips.AsNoTracking()
                .Where(t => (t.CompanyId == cid || t.Load!.CompanyId == cid) && t.Status != TripStatus.Cancelled)
                .OrderBy(t => TripStatus.Live.Contains(t.Status) ? 0 : TripStatus.Upcoming.Contains(t.Status) ? 1 : 2)
                .ThenByDescending(t => t.CreatedAt)
                .Take(300)
                .Select(t => new OpsOption(t.TripId, t.Code + " — " + t.Load!.OriginCity!.Name + " ← " + t.Load!.DestCity!.Name + " — " + t.Load!.Title))
                .ToListAsync(ct)
        };

        var q = kind is null ? all : all.Where(d => d.Kind == kind);
        vm.Page = await PageVm<TripDocument>.FromAsync(
            q.Include(d => d.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.OriginCity)
                .Include(d => d.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.DestCity)
                .OrderByDescending(d => d.CreatedAt),
            page, PageLink.For(Request), ct: ct);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Upload(int tripId, string? kind, string? title, IFormFile? file, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(tripId, me.ToActor(), ct);
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
        return RedirectToAction(nameof(Documents), new { kind = kind is not null && CompanyTrips.AllDocKinds.Contains(kind) ? kind : null });
    }
}
