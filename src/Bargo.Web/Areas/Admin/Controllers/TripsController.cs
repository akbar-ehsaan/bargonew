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
/// «مدیریت سفرها» — فهرست سفرها، صف سفرهای مشکل‌دار، رهگیری با کد و «پروندهٔ سفر».
///
/// پرونده عمداً همه‌چیزِ یک سفر را یک‌جا می‌آورد (مراحل، طرفین، پول، تحویل، بارنامه،
/// GPS، گفتگو، شکایت): رسیدگی به اختلاف یعنی «چه کسی، کی، کجا، چه کرد» و این پاسخ
/// در پنج صفحهٔ جدا پیدا نمی‌شود. هر تغییر وضعیت از <see cref="TripFlow"/> می‌گذرد تا
/// TripEvent، اعلان‌ها و تسویه/استرداد هرگز از قلم نیفتند؛ اینجا فقط ردّ حسابرسی افزوده می‌شود.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Operations)]
public class TripsController(BargoDbContext db, TripFlow flow, AuditService audit, SettingsService settings, CurrentUser me) : Controller
{
    public static readonly (string Key, string Label, string Icon)[] Tabs =
    [
        ("current", "جاری", "bi-truck"),
        ("done", "تکمیل‌شده", "bi-flag"),
        ("cancelled", "لغوشده", "bi-slash-circle"),
        ("all", "همه", "bi-list-ul"),
    ];

    private static IQueryable<Trip> Filter(IQueryable<Trip> q, string tab) => tab switch
    {
        "done" => q.Where(t => TripStatus.Done.Contains(t.Status)),
        "cancelled" => q.Where(t => t.Status == TripStatus.Cancelled),
        "all" => q,
        _ => q.Where(t => TripStatus.Upcoming.Contains(t.Status) || TripStatus.Live.Contains(t.Status))
    };

    /// <summary>پلاک ممکن است با ارقام فارسی ذخیره شده باشد؛ هر دو شکلِ عبارت جستجو می‌شود.</summary>
    private static IQueryable<Trip> Search(IQueryable<Trip> q, string term)
    {
        if (term.Length == 0) return q;
        var fa = Fa.Digits(term);
        return q.Where(t => t.Code.Contains(term) || t.Load!.Code.Contains(term) ||
                            (t.Vehicle != null && (t.Vehicle.PlateNo.Contains(term) || t.Vehicle.PlateNo.Contains(fa))) ||
                            (t.Driver != null && ((t.Driver.FirstName + " " + t.Driver.LastName).Contains(term) || t.Driver.Mobile.Contains(term))) ||
                            (t.Company != null && t.Company.Name.Contains(term)) ||
                            t.Load!.OriginCity!.Name.Contains(term) || t.Load.DestCity!.Name.Contains(term));
    }

    // ------------------------------------------------------------------
    //  فهرست
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? tab, string? q, int page = 1, CancellationToken ct = default)
    {
        tab = Tabs.Any(t => t.Key == tab) ? tab! : "current";
        ViewData["Title"] = tab switch { "done" => "سفرهای تکمیل‌شده", "cancelled" => "سفرهای لغوشده", "all" => "همهٔ سفرها", _ => "سفرهای جاری" };

        var term = AdminOps.Term(q);
        var src = Search(db.Trips.AsNoTracking(), term);
        var counts = new Dictionary<string, int>();
        foreach (var t in Tabs) counts[t.Key] = await Filter(src, t.Key).CountAsync(ct);

        // جاری: زنده‌ها اول و بر پایهٔ تازه‌ترین GPS؛ سوابق: تازه‌ترین اول
        var filtered = Filter(src, tab);
        var ordered = tab == "current"
            ? filtered.OrderByDescending(t => TripStatus.Live.Contains(t.Status)).ThenByDescending(t => t.LastPointAt).ThenByDescending(t => t.CreatedAt)
            : filtered.OrderByDescending(t => t.CreatedAt);

        var vm = await PageVm<TripRow>.FromAsync(AdminOps.TripRows(ordered), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Tab = tab;
        ViewBag.Counts = counts;
        ViewBag.Problems = await AdminOps.Problems(db.Trips.AsNoTracking(), DateTime.UtcNow).CountAsync(ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  سفرهای مشکل‌دار
    // ------------------------------------------------------------------

    public async Task<IActionResult> Problems(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سفرهای مشکل‌دار";
        var now = DateTime.UtcNow;
        var src = AdminOps.Problems(db.Trips.AsNoTracking(), now)
            .OrderByDescending(t => t.IsProblem).ThenBy(t => t.LastPointAt).ThenByDescending(t => t.CreatedAt);
        var vm = await PageVm<TripRow>.FromAsync(AdminOps.TripRows(src), page, PageLink.For(Request), ct: ct);
        foreach (var r in vm.Rows) r.Reasons = AdminOps.ProblemReasons(r, now);
        ViewBag.Flagged = vm.Rows.Count(r => r.IsProblem);
        return View(vm);
    }

    /// <summary>علامت «مشکل‌دار» وضعیت سفر نیست — پرچم داخلی مدیر است؛ فقط ردّ حسابرسی می‌خواهد.</summary>
    [HttpPost]
    public async Task<IActionResult> MarkProblem(int id, string? note, string? returnUrl, CancellationToken ct)
    {
        var t = await db.Trips.FirstOrDefaultAsync(x => x.TripId == id, ct);
        if (t is null) return NotFound();
        var n = AdminOps.Note(note);
        if (n is null)
        {
            TempData["err"] = "شرح مشکل را بنویسید تا در صف معلوم باشد چرا این سفر اینجاست.";
            return AdminOps.Back(this, returnUrl, $"/Admin/Trips/Case/{id}");
        }
        var was = t.IsProblem;
        t.IsProblem = true;
        t.ProblemNote = n;
        audit.Add("Trip", id, "problem:mark", $"{(was ? "به‌روزرسانی مشکل" : "علامت مشکل‌دار")} سفر {t.Code}: {n}", new { note = n, was });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"سفر {t.Code} در صف سفرهای مشکل‌دار قرار گرفت.";
        return AdminOps.Back(this, returnUrl, $"/Admin/Trips/Case/{id}");
    }

    [HttpPost]
    public async Task<IActionResult> ClearProblem(int id, string? returnUrl, CancellationToken ct)
    {
        var t = await db.Trips.FirstOrDefaultAsync(x => x.TripId == id, ct);
        if (t is null) return NotFound();
        if (!t.IsProblem)
        {
            TempData["err"] = $"سفر {t.Code} علامت مشکل‌دار ندارد. اگر هنوز در صف است، علتش محاسبه‌ای است (بدون GPS یا گذشتن از زمان رسیدن) و با برگشتن GPS یا تحویل خودش پاک می‌شود.";
            return AdminOps.Back(this, returnUrl, $"/Admin/Trips/Case/{id}");
        }
        var note = t.ProblemNote;
        t.IsProblem = false;
        t.ProblemNote = null;
        audit.Add("Trip", id, "problem:clear", $"برداشتن علامت مشکل‌دار از سفر {t.Code}", new { note });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"علامت مشکل‌دار سفر {t.Code} برداشته شد.";
        return AdminOps.Back(this, returnUrl, $"/Admin/Trips/Case/{id}");
    }

    // ------------------------------------------------------------------
    //  رهگیری با کد
    // ------------------------------------------------------------------

    /// <summary>کد سفر، کد بار یا شمارهٔ بارنامه → نقشه و مراحل. چند نتیجه (چند سفر برای یک بار) → تازه‌ترین.</summary>
    public async Task<IActionResult> Track(string? code, CancellationToken ct)
    {
        ViewData["Title"] = "رهگیری سفر";
        var vm = new TripTrackVm { Code = Fa.Latin(code).Trim().ToUpperInvariant() };
        if (vm.Code.Length == 0) return View(vm);

        var c = vm.Code;
        var tripId = await db.Trips.AsNoTracking()
            .Where(t => t.Code == c || t.Load!.Code == c || (t.Waybill != null && t.Waybill.Number == c))
            .OrderByDescending(t => t.TripId).Select(t => (int?)t.TripId).FirstOrDefaultAsync(ct);
        if (tripId is null)
        {
            vm.NotFound = $"سفری با کد «{Fa.Digits(c)}» پیدا نشد. کد سفر (T-…)، کد بار (L-…) یا شمارهٔ بارنامه را وارد کنید.";
            return View(vm);
        }

        vm.Trip = await flow.FindForAsync(tripId.Value, me.ToActor(), ct);
        if (vm.Trip is null) return View(vm);

        vm.Events = await db.TripEvents.AsNoTracking().Where(e => e.TripId == tripId).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var pts = await db.TrackingPoints.AsNoTracking().Where(p => p.TripId == tripId).OrderBy(p => p.RecordedAt)
            .Select(p => new { p.Lat, p.Lng, p.RecordedAt }).ToListAsync(ct);
        vm.PointCount = pts.Count;
        vm.FirstPointAt = pts.FirstOrDefault()?.RecordedAt;
        vm.Line = pts.Select(p => new[] { p.Lat, p.Lng }).ToList();
        vm.Markers = RouteMarkers(vm.Trip, vm.Events);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  پروندهٔ سفر
    // ------------------------------------------------------------------

    public async Task<IActionResult> Case(int id, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        ViewData["Title"] = $"پروندهٔ سفر {trip.Code}";
        var load = trip.Load!;

        var vm = new TripCaseVm { Trip = trip };
        if (trip.OfferId is int oid)
            vm.Offer = await db.Offers.AsNoTracking().Include(o => o.Driver).Include(o => o.Company).Include(o => o.Vehicle)
                .FirstOrDefaultAsync(o => o.OfferId == oid, ct);

        // صاحب بار: عضو بارگو یا شرکتی که بار را برای مشتری‌اش ثبت کرده
        if (load.ShipperId is int sid && load.Shipper is { } sh)
        {
            vm.LoadOwnerName = sh.DisplayName; vm.LoadOwnerMobile = sh.Mobile; vm.LoadOwnerHref = $"/Admin/Users/Shipper/{sid}";
        }
        else if (load.CompanyId is int lcid)
        {
            var oc = await db.Companies.AsNoTracking().Where(x => x.CompanyId == lcid).Select(x => new { x.Name, x.Mobile }).FirstOrDefaultAsync(ct);
            vm.LoadOwnerName = oc?.Name ?? "—"; vm.LoadOwnerMobile = oc?.Mobile; vm.LoadOwnerHref = $"/Admin/Users/Company/{lcid}";
        }

        vm.Events = await db.TripEvents.AsNoTracking().Where(e => e.TripId == id).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        vm.DeliveryEvent = vm.Events.LastOrDefault(e => e.ToStatus == TripStatus.Delivered && e.FromStatus != TripStatus.Delivered);
        vm.LastEvent = vm.Events.LastOrDefault();

        vm.Assignments = await db.TripAssignments.AsNoTracking().Where(a => a.TripId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var driverIds = vm.Assignments.SelectMany(a => new[] { a.DriverId, a.PreviousDriverId ?? 0 }).Where(x => x > 0).Distinct().ToList();
        var vehicleIds = vm.Assignments.SelectMany(a => new[] { a.VehicleId ?? 0, a.PreviousVehicleId ?? 0 }).Where(x => x > 0).Distinct().ToList();
        if (driverIds.Count > 0)
            vm.DriverNames = await db.Drivers.AsNoTracking().Where(d => driverIds.Contains(d.DriverId))
                .ToDictionaryAsync(d => d.DriverId, d => d.FirstName + " " + d.LastName, ct);
        if (vehicleIds.Count > 0)
            vm.VehiclePlates = await db.Vehicles.AsNoTracking().Where(v => vehicleIds.Contains(v.VehicleId))
                .ToDictionaryAsync(v => v.VehicleId, v => v.PlateNo, ct);

        // مسیر طی‌شده — همهٔ نقاط؛ جدول پرحجم است ولی یک سفر بیش از چند هزار نقطه ندارد
        var pts = await db.TrackingPoints.AsNoTracking().Where(p => p.TripId == id).OrderBy(p => p.RecordedAt)
            .Select(p => new { p.Lat, p.Lng, p.RecordedAt, p.SpeedKmh }).ToListAsync(ct);
        vm.PointCount = pts.Count;
        vm.FirstPointAt = pts.FirstOrDefault()?.RecordedAt;
        vm.LastPointAt = pts.LastOrDefault()?.RecordedAt ?? trip.LastPointAt;
        vm.MaxSpeed = pts.Count == 0 ? null : pts.Max(p => p.SpeedKmh);
        vm.Line = pts.Select(p => new[] { p.Lat, p.Lng }).ToList();
        vm.Markers = RouteMarkers(trip, vm.Events);

        vm.Messages = await db.Messages.AsNoTracking().Where(m => m.ThreadKey == "trip:" + id).OrderBy(m => m.CreatedAt).ToListAsync(ct);

        vm.Payments = await db.Payments.AsNoTracking().Where(p => p.TripId == id).OrderBy(p => p.PaymentId).ToListAsync(ct);
        vm.Txns = await db.WalletTransactions.AsNoTracking().Where(t => t.TripId == id).OrderBy(t => t.WalletTransactionId).ToListAsync(ct);
        vm.Invoices = await db.Invoices.AsNoTracking().Where(i => i.TripId == id).OrderBy(i => i.InvoiceId).ToListAsync(ct);
        vm.Refunds = await db.Refunds.AsNoTracking().Where(r => r.TripId == id).OrderBy(r => r.RefundId).ToListAsync(ct);
        vm.Owners = await AdminOps.OwnerRefsAsync(db,
            vm.Payments.Select(p => (p.PayerKind, p.PayerId))
                .Concat(vm.Txns.Select(t => (t.OwnerKind, t.OwnerId)))
                .Concat(vm.Invoices.Select(i => (i.OwnerKind, i.OwnerId)))
                .Concat(vm.Refunds.Select(r => (r.OwnerKind, r.OwnerId))), ct);

        vm.Documents = await db.TripDocuments.AsNoTracking().Where(d => d.TripId == id).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        vm.Complaints = await db.Complaints.AsNoTracking().Where(c => c.TripId == id).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        vm.Tickets = await db.Tickets.AsNoTracking().Where(t => t.TripId == id).OrderByDescending(t => t.UpdatedAt).ToListAsync(ct);
        vm.Ratings = await db.Ratings.AsNoTracking().Where(r => r.TripId == id).ToListAsync(ct);
        vm.Violations = await db.Violations.AsNoTracking().Include(v => v.Driver).Where(v => v.TripId == id).OrderByDescending(v => v.CreatedAt).ToListAsync(ct);

        vm.NextSteps = TripFlow.NextSteps(trip.Status, Roles.Admin).Where(s => s != TripStatus.Settled).ToList();
        vm.CanCancel = TripFlow.CanCancel(trip.Status, Roles.Admin);
        vm.CanSettle = trip.Status == TripStatus.Delivered;
        vm.CanAssign = trip.CarrierKind == CarrierKind.Company && trip.CompanyId is not null &&
                       trip.Status is not (TripStatus.Delivered or TripStatus.Settled or TripStatus.Cancelled);
        if (vm.CanAssign)
        {
            vm.CompanyDrivers = await db.Drivers.AsNoTracking().Where(d => d.CompanyId == trip.CompanyId && d.Status == AccountStatus.Approved)
                .OrderBy(d => d.LastName).Select(d => new SelectOption(d.DriverId, d.FirstName + " " + d.LastName + " · " + d.Mobile)).ToListAsync(ct);
            vm.CompanyVehicles = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == trip.CompanyId && v.Status == VehicleStatus.Active)
                .OrderBy(v => v.PlateNo).Select(v => new SelectOption(v.VehicleId, v.PlateNo + " · " + v.VehicleType!.Name)).ToListAsync(ct);
        }
        vm.DeliveryOtpRequired = await settings.GetBoolAsync(SettingsService.Keys.DeliveryOtp, ct);
        vm.Reasons = AdminOps.ProblemReasons(new TripRow
        {
            Status = trip.Status, IsProblem = trip.IsProblem, ProblemNote = trip.ProblemNote, LastPointAt = trip.LastPointAt, EtaAt = trip.EtaAt
        }, DateTime.UtcNow);

        return View(vm);
    }

    /// <summary>مبدا، مقصد، آخرین موقعیت خودرو و موقعیتِ رویدادهایی که با GPS ثبت شده‌اند.</summary>
    private static List<MapMarker> RouteMarkers(Trip trip, IEnumerable<TripEvent> events)
    {
        var l = trip.Load!;
        var list = new List<MapMarker>();
        var oLat = l.OriginLat ?? l.OriginCity?.Lat;
        var oLng = l.OriginLng ?? l.OriginCity?.Lng;
        var dLat = l.DestLat ?? l.DestCity?.Lat;
        var dLng = l.DestLng ?? l.DestCity?.Lng;
        if (oLat is double ola && oLng is double olg) list.Add(new MapMarker(ola, olg, "origin", $"مبدا: {l.OriginCity?.Name}\n{l.OriginAddress}"));
        if (dLat is double dla && dLng is double dlg) list.Add(new MapMarker(dla, dlg, "dest", $"مقصد: {l.DestCity?.Name}\n{l.DestAddress}"));
        foreach (var e in events.Where(e => e.Lat is not null && e.Lng is not null))
            list.Add(new MapMarker(e.Lat!.Value, e.Lng!.Value, "stale",
                $"{TripStatus.Label(e.ToStatus)}\n{Fa.Stamp(e.CreatedAt)} · {e.ActorName ?? OwnerKind.Label(e.ActorKind)}{(string.IsNullOrEmpty(e.Note) ? "" : "\n" + e.Note)}"));
        if (trip.LastLat is double la && trip.LastLng is double lg)
            list.Add(new MapMarker(la, lg, TripStatus.Live.Contains(trip.Status) ? "truck" : "idle",
                $"{trip.Code} · {TripStatus.Label(trip.Status)}\nآخرین GPS: {(trip.LastPointAt is DateTime lp ? Fa.Ago(lp) : "—")}{(trip.Driver is null ? "" : "\nراننده: " + trip.Driver.FullName)}"));
        return list;
    }

    // ------------------------------------------------------------------
    //  اقدامات — همه از TripFlow؛ اینجا فقط ردّ حسابرسی
    // ------------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> Move(int id, string to, string? note, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        var from = trip.Status;
        try
        {
            if (to == TripStatus.Cancelled) throw new UserError("برای لغو سفر از دکمهٔ «لغو سفر» با علت استفاده کنید.");
            await flow.MoveAsync(trip, to, me.ToActor(), AdminOps.Note(note), ct: ct);
            audit.Add("Trip", id, "move:" + to, $"سفر {trip.Code}: {TripStatus.Label(from)} ← {TripStatus.Label(to)} (توسط مدیر)", new { from, to, note });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"سفر {trip.Code} به مرحلهٔ «{TripStatus.Label(to)}» رفت و طرفین باخبر شدند." +
                             (to == TripStatus.Delivered && trip.Status == TripStatus.Settled ? " کرایه پرداخت‌شده بود و تسویه خودکار انجام شد." : "");
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Case), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Settle(int id, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            await flow.SettleAsync(trip, me.ToActor(), ct);
            audit.Add("Trip", id, "settle", $"تسویهٔ دستی سفر {trip.Code}: سهم حمل‌کننده {Fa.Toman(trip.CarrierShare)}، کمیسیون {Fa.Toman(trip.Commission)}",
                new { trip.Fare, trip.Commission, trip.CarrierShare, trip.DriverShare });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"سفر {trip.Code} تسویه شد؛ {Fa.Toman(trip.CarrierShare)} به کیف پول حمل‌کننده و {Fa.Toman(trip.Commission)} به کیف پول بارگو نشست.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Case), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        var from = trip.Status;
        var wasPaid = trip.IsPaid;
        try
        {
            var r = AdminOps.Note(reason, 500) ?? throw new UserError("علت لغو را بنویسید؛ برای صاحب بار و حمل‌کننده ارسال می‌شود.");
            await flow.CancelAsync(trip, me.ToActor(), r, ct);
            audit.Add("Trip", id, "cancel", $"لغو سفر {trip.Code} توسط مدیر از مرحلهٔ «{TripStatus.Label(from)}»: {r}",
                new { from, reason = r, refunded = wasPaid ? trip.TotalPayable : 0, problem = trip.IsProblem });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"سفر {trip.Code} لغو شد." +
                             (wasPaid ? $" {Fa.Toman(trip.TotalPayable)} به کیف پول صاحب بار مسترد شد." : "") +
                             (trip.IsProblem ? " چون بار روی خودرو بود، سفر در صف مشکل‌دار ماند تا تکلیف بار روشن شود." : " بار دوباره در بازار قرار گرفت.");
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Case), new { id });
    }

    /// <summary>تخصیص/تعویض راننده و خودرو در سفر شرکتی — وقتی شرکت تعلیق شده یا پاسخ نمی‌دهد.</summary>
    [HttpPost]
    public async Task<IActionResult> Assign(int id, int driverId, int? vehicleId, string? reason, CancellationToken ct)
    {
        try
        {
            await flow.AssignAsync(id, driverId, vehicleId, me.ToActor(), AdminOps.Note(reason, 500), ct);
            var code = await db.Trips.AsNoTracking().Where(t => t.TripId == id).Select(t => t.Code).FirstAsync(ct);
            audit.Add("Trip", id, "assign", $"تخصیص راننده به سفر {code} توسط مدیر", new { driverId, vehicleId, reason });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = "تخصیص ثبت شد و راننده باخبر شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Case), new { id });
    }
}
