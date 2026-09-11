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
/// «مدیریت رانندگان» — صف تأیید (با چک‌لیست آمادگی)، مدارک، خودروهای رانندگان مستقل،
/// سوابق، امتیاز، تخلفات و تعلیق حساب.
///
/// تأیید/رد/تعلیق/فعال‌سازی از <see cref="AdminOps.SetAccountStatusAsync"/> می‌گذرد؛
/// ثبت تخلفِ «تعلیق» هم همان مسیر را می‌رود تا اعلان و ردّ حسابرسی یکسان بماند.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Users)]
public class DriversController(
    BargoDbContext db,
    NotificationService notify,
    AuditService audit,
    DriverReadiness readiness,
    SettingsService settings,
    CurrentUser me) : Controller
{
    private static readonly string[] Penalties = ["warning", "fine", "suspension"];

    private static bool ValidStatus(string? s) => s is not null && AdminOps.AccountStatuses.Contains(s);

    private static IQueryable<Driver> Search(IQueryable<Driver> src, string term) =>
        term.Length == 0 ? src : src.Where(d => (d.FirstName + " " + d.LastName).Contains(term) || d.Mobile.Contains(term) || d.NationalCode.Contains(term));

    // ------------------------------------------------------------------
    //  تأیید راننده
    // ------------------------------------------------------------------

    /// <summary>
    /// رانندگان در انتظار تأیید — قدیمی‌ترین اول — هر کدام با چک‌لیست مدارک خودش و
    /// خودروی فعالش، و نتیجهٔ <see cref="DriverReadiness"/>. مدیر بدون رفتن به پرونده
    /// می‌بیند چه چیزی کم است.
    /// </summary>
    public async Task<IActionResult> Pending(string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تأیید راننده";
        var term = AdminOps.Term(q);
        var src = Search(db.Drivers.AsNoTracking().Include(d => d.City).Include(d => d.Company).Where(d => d.Status == AccountStatus.Pending), term);
        var pv = await PageVm<Driver>.FromAsync(src.OrderBy(d => d.CreatedAt), page, PageLink.For(Request), 10, ct);

        var ids = pv.Rows.Select(d => d.DriverId).ToList();
        var driverDocs = ids.Count == 0
            ? []
            : await db.Documents.AsNoTracking().Where(x => x.OwnerKind == OwnerKind.Driver && ids.Contains(x.OwnerId)).ToListAsync(ct);
        // همان خودرویی که DriverReadiness می‌سنجد: تازه‌ترین خودروی فعالِ راننده
        var vehicles = ids.Count == 0
            ? []
            : await db.Vehicles.AsNoTracking().Include(v => v.VehicleType)
                .Where(v => v.DriverId != null && ids.Contains(v.DriverId.Value) && v.Status == VehicleStatus.Active)
                .OrderByDescending(v => v.VehicleId).ToListAsync(ct);
        var vehicleIds = vehicles.Select(v => v.VehicleId).ToList();
        var vehicleDocs = vehicleIds.Count == 0
            ? []
            : await db.Documents.AsNoTracking().Where(x => x.OwnerKind == OwnerKind.Vehicle && vehicleIds.Contains(x.OwnerId)).ToListAsync(ct);

        var rows = new List<PendingDriverVm>();
        foreach (var d in pv.Rows)
        {
            var veh = vehicles.FirstOrDefault(v => v.DriverId == d.DriverId);
            var issues = await readiness.CheckAsync(d.DriverId, null, ct);
            rows.Add(new PendingDriverVm
            {
                Driver = d,
                Checklist = DocCheck.For(DocumentKind.ForDriver, driverDocs.Where(x => x.OwnerId == d.DriverId)),
                Vehicle = veh,
                VehicleChecklist = veh is null ? [] : DocCheck.For(DocumentKind.ForVehicle, vehicleDocs.Where(x => x.OwnerId == veh.VehicleId)),
                // «حساب در انتظار تأیید است» در صف تأیید بدیهی است — همان ردیفِ پیوند به پروفایل
                Issues = issues.Where(i => i.Link != "/Driver/Profile").ToList()
            });
        }

        var vm = new PageVm<PendingDriverVm> { Rows = rows, Page = pv.Page, PageSize = pv.PageSize, Total = pv.Total, Link = pv.Link };
        ViewBag.Q = term;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, string status, string? reason, string? returnUrl, CancellationToken ct)
    {
        try
        {
            TempData["ok"] = await AdminOps.SetAccountStatusAsync(db, notify, audit, OwnerKind.Driver, id, status, reason, ct);
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Drivers/Pending");
    }

    // ------------------------------------------------------------------
    //  مدارک
    // ------------------------------------------------------------------

    /// <summary>
    /// مدارک رانندگان و خودروهای رانندگان مستقل. بررسی از همین‌جا به
    /// <c>/Admin/Documents/Review</c> می‌رود (دسترسی «احراز هویت» لازم است).
    /// </summary>
    public async Task<IActionResult> Documents(string? status, string? kind, string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "مدارک رانندگان";
        var term = AdminOps.Term(q);
        var src = db.Documents.AsNoTracking()
            .Where(d => d.OwnerKind == OwnerKind.Driver ||
                        (d.OwnerKind == OwnerKind.Vehicle && db.Vehicles.Any(v => v.VehicleId == d.OwnerId && v.DriverId != null && v.CompanyId == null)));
        if (term.Length > 0)
            src = src.Where(d =>
                (d.OwnerKind == OwnerKind.Driver && db.Drivers.Any(x => x.DriverId == d.OwnerId && ((x.FirstName + " " + x.LastName).Contains(term) || x.Mobile.Contains(term)))) ||
                (d.OwnerKind == OwnerKind.Vehicle && db.Vehicles.Any(v => v.VehicleId == d.OwnerId && (v.PlateNo.Contains(term) ||
                    (v.Driver != null && ((v.Driver.FirstName + " " + v.Driver.LastName).Contains(term) || v.Driver.Mobile.Contains(term)))))));

        string[] kinds = [.. DocumentKind.ForDriver, .. DocumentKind.ForVehicle, DocumentKind.Other];
        if (kind is not null && kinds.Contains(kind)) src = src.Where(d => d.Kind == kind);

        ViewBag.Counts = await src.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(status)) src = src.Where(d => d.Status == status);

        var ordered = status == AccountStatus.Pending || status is null
            ? src.OrderBy(d => d.Status == AccountStatus.Pending ? 0 : 1).ThenBy(d => d.UploadedAt)
            : src.OrderByDescending(d => d.ReviewedAt ?? d.UploadedAt);
        var pv = await PageVm<Document>.FromAsync(ordered, page, PageLink.For(Request), ct: ct);
        var vm = await DocumentRowsAsync(pv, ct);

        ViewBag.Q = term;
        ViewBag.Status = ValidStatus(status) ? status : null;
        ViewBag.Kind = kind is not null && kinds.Contains(kind) ? kind : null;
        ViewBag.Kinds = kinds;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    private async Task<PageVm<DocumentRowVm>> DocumentRowsAsync(PageVm<Document> pv, CancellationToken ct)
    {
        var owners = await AdminOps.OwnerRefsAsync(db, pv.Rows.Select(d => (d.OwnerKind, d.OwnerId)), ct);
        var admins = await AdminOps.AdminNamesAsync(db, pv.Rows.Select(d => d.ReviewedByAdminId), ct);
        return new PageVm<DocumentRowVm>
        {
            Rows = pv.Rows.Select(d => new DocumentRowVm
            {
                Doc = d,
                Owner = owners.GetValueOrDefault(AdminOps.Key(d.OwnerKind, d.OwnerId)) ?? new OwnerRef("—"),
                ReviewerName = d.ReviewedByAdminId is int a ? admins.GetValueOrDefault(a) : null
            }).ToList(),
            Page = pv.Page, PageSize = pv.PageSize, Total = pv.Total, Link = pv.Link
        };
    }

    // ------------------------------------------------------------------
    //  خودروهای رانندگان مستقل
    // ------------------------------------------------------------------

    public async Task<IActionResult> Vehicles(string? q, string? verify, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "خودروهای رانندگان";
        var term = AdminOps.Term(q);
        var src = db.Vehicles.AsNoTracking().Where(v => v.DriverId != null && v.CompanyId == null);
        if (term.Length > 0)
            src = src.Where(v => v.PlateNo.Contains(term) || (v.Driver!.FirstName + " " + v.Driver.LastName).Contains(term) || v.Driver!.Mobile.Contains(term));

        ViewBag.Counts = await src.GroupBy(v => v.VerifyStatus).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(verify)) src = src.Where(v => v.VerifyStatus == verify);

        var ordered = src.OrderBy(v => v.VerifyStatus == AccountStatus.Pending ? 0 : 1).ThenByDescending(v => v.CreatedAt);
        var vm = await PageVm<VehicleRow>.FromAsync(AdminOps.VehicleRows(db, ordered), page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Verify = ValidStatus(verify) ? verify : null;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  سوابق
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? q, string? status, string? kind, string sort = "new", int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سوابق رانندگان";
        var term = AdminOps.Term(q);
        var src = Search(db.Drivers.AsNoTracking(), term);
        if (kind == "independent") src = src.Where(d => d.CompanyId == null);
        else if (kind == "company") src = src.Where(d => d.CompanyId != null);

        ViewBag.Counts = await src.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(status)) src = src.Where(d => d.Status == status);

        var rows = AdminOps.DriverRows(db, src);
        sort = sort is "trips" or "cancelled" or "rating" or "violations" ? sort : "new";
        var ordered = sort switch
        {
            "trips" => rows.OrderByDescending(r => r.TripCount).ThenByDescending(r => r.CreatedAt),
            "cancelled" => rows.OrderByDescending(r => r.Cancelled).ThenByDescending(r => r.TripCount),
            "rating" => rows.OrderByDescending(r => r.RatingCount > 0 ? 1 : 0).ThenByDescending(r => r.RatingAvg).ThenByDescending(r => r.RatingCount),
            "violations" => rows.OrderByDescending(r => r.Violations).ThenByDescending(r => r.Cancelled),
            _ => rows.OrderByDescending(r => r.CreatedAt)
        };
        var vm = await PageVm<DriverRow>.FromAsync(ordered, page, PageLink.For(Request), ct: ct);
        ViewBag.Q = term;
        ViewBag.Status = ValidStatus(status) ? status : null;
        ViewBag.Kind = kind is "independent" or "company" ? kind : null;
        ViewBag.Sort = sort;
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  امتیاز
    // ------------------------------------------------------------------

    /// <summary>رتبه‌بندی رانندگان امتیازدار + تازه‌ترین امتیازهای پایین (≤۲) برای پیگیری.</summary>
    public async Task<IActionResult> Ratings(string? q, string sort = "top", int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "امتیاز رانندگان";
        var term = AdminOps.Term(q);
        var src = Search(db.Drivers.AsNoTracking().Where(d => d.RatingCount > 0), term);
        var rows = AdminOps.DriverRows(db, src);
        sort = sort is "low" or "most" ? sort : "top";
        var ordered = sort switch
        {
            "low" => rows.OrderBy(r => r.RatingAvg).ThenByDescending(r => r.RatingCount),
            "most" => rows.OrderByDescending(r => r.RatingCount).ThenByDescending(r => r.RatingAvg),
            _ => rows.OrderByDescending(r => r.RatingAvg).ThenByDescending(r => r.RatingCount)
        };

        var vm = new DriverRatingsVm
        {
            Page = await PageVm<DriverRow>.FromAsync(ordered, page, PageLink.For(Request), ct: ct),
            Sort = sort,
            Q = term
        };
        var rated = db.Drivers.AsNoTracking().Where(d => d.RatingCount > 0);
        vm.RatedDrivers = await rated.CountAsync(ct);
        // میانگین وزنی روی همهٔ امتیازها، نه میانگینِ میانگین‌ها
        vm.OverallAvg = await db.Ratings.AsNoTracking().Where(r => r.ToKind == OwnerKind.Driver).Select(r => (double?)r.Score).AverageAsync(ct) ?? 0;
        vm.LowCount30 = await db.Ratings.CountAsync(r => r.ToKind == OwnerKind.Driver && r.Score <= 2 && r.CreatedAt >= DateTime.UtcNow.AddDays(-30), ct);

        vm.Low = await db.Ratings.AsNoTracking().Where(r => r.ToKind == OwnerKind.Driver && r.Score <= 2)
            .OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
        var lowDriverIds = vm.Low.Select(r => r.ToId).Distinct().ToList();
        vm.DriverNames = lowDriverIds.Count == 0
            ? []
            : await db.Drivers.AsNoTracking().Where(d => lowDriverIds.Contains(d.DriverId))
                .ToDictionaryAsync(d => d.DriverId, d => d.FirstName + " " + d.LastName, ct);
        var lowTripIds = vm.Low.Select(r => r.TripId).Distinct().ToList();
        vm.TripCodes = lowTripIds.Count == 0
            ? []
            : await db.Trips.AsNoTracking().Where(t => lowTripIds.Contains(t.TripId)).ToDictionaryAsync(t => t.TripId, t => t.Code, ct);
        vm.Raters = await AdminOps.OwnerRefsAsync(db, vm.Low.Select(r => (r.FromKind, r.FromId)), ct);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  تخلفات
    // ------------------------------------------------------------------

    /// <summary>
    /// فهرست تخلفات ثبت‌شده + فرم ثبت تخلف تازه. راننده با شمارهٔ موبایل (یا شناسه از
    /// پیوند پرونده) پیدا می‌شود؛ فرم فقط وقتی باز است که راننده پیدا شده باشد تا
    /// تخلف روی آدم اشتباه ثبت نشود.
    /// </summary>
    public async Task<IActionResult> Violations(string? q, string? penalty, string? mobile, int? driver, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تخلفات رانندگان";
        var term = AdminOps.Term(q);
        var src = db.Violations.AsNoTracking().Include(v => v.Driver).Where(v => v.DriverId != null);
        if (term.Length > 0)
            src = src.Where(v => v.Title.Contains(term) || (v.Driver!.FirstName + " " + v.Driver.LastName).Contains(term) || v.Driver!.Mobile.Contains(term));

        var vm = new DriverViolationsVm
        {
            Counts = await src.GroupBy(v => v.Penalty).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct),
            Penalty = penalty is not null && Penalties.Contains(penalty) ? penalty : null,
            Q = term
        };
        if (vm.Penalty is not null) src = src.Where(v => v.Penalty == vm.Penalty);
        vm.Page = await PageVm<Violation>.FromAsync(src.OrderByDescending(v => v.CreatedAt), page, PageLink.For(Request), ct: ct);
        vm.AdminNames = await AdminOps.AdminNamesAsync(db, vm.Page.Rows.Select(v => (int?)v.CreatedByAdminId), ct);
        var tripIds = vm.Page.Rows.Where(v => v.TripId != null).Select(v => v.TripId!.Value).Distinct().ToList();
        vm.TripCodes = tripIds.Count == 0
            ? []
            : await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.TripId)).ToDictionaryAsync(t => t.TripId, t => t.Code, ct);

        // جستجوی رانندهٔ فرم
        var normMobile = Fa.NormMobile(mobile);
        vm.Mobile = string.IsNullOrWhiteSpace(mobile) ? null : mobile;
        if (driver is int did)
            vm.Found = await db.Drivers.AsNoTracking().Include(d => d.Company).FirstOrDefaultAsync(d => d.DriverId == did, ct);
        else if (normMobile.Length > 0)
            vm.Found = await db.Drivers.AsNoTracking().Include(d => d.Company).FirstOrDefaultAsync(d => d.Mobile == normMobile, ct);
        else if (!string.IsNullOrWhiteSpace(mobile))
            ViewBag.SearchErr = "شمارهٔ موبایل معتبر نیست؛ قالب درست: ۰۹۱۲۳۴۵۶۷۸۹";

        if (vm.Found is null && (driver is not null || normMobile.Length > 0))
            ViewBag.SearchErr = "راننده‌ای با این مشخصات پیدا نشد.";

        if (vm.Found is not null)
        {
            vm.FoundTrips = await AdminOps.TripRows(db.Trips.AsNoTracking().Where(t => t.DriverId == vm.Found.DriverId).OrderByDescending(t => t.CreatedAt)).Take(8).ToListAsync(ct);
            vm.FoundViolations = await db.Violations.CountAsync(v => v.DriverId == vm.Found.DriverId, ct);
        }
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> AddViolation(int driverId, string? tripCode, string? title, string? description, string? penalty,
        string? fineAmountToman, DateTime? suspendedUntil, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Violations), new { driver = driverId });
        try
        {
            if (!ModelState.IsValid) throw new UserError("تاریخ پایان تعلیق معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            var d = await db.Drivers.FirstOrDefaultAsync(x => x.DriverId == driverId, ct) ?? throw new UserError("راننده پیدا نشد.");

            title = AdminOps.Note(title, 60);
            if (title is null || title.Length < 2) throw new UserError("عنوان تخلف را بنویسید (۲ تا ۶۰ نویسه).");
            description = AdminOps.Note(description);
            if (penalty is null || !Penalties.Contains(penalty)) throw new UserError("نوع مجازات را انتخاب کنید.");

            long? fine = null;
            if (penalty == "fine")
            {
                fine = Fa.ParseToman(fineAmountToman);
                if (fine is null || fine.Value <= 0) throw new UserError("مبلغ جریمه را به تومان وارد کنید.");
            }
            DateTime? until = null;
            if (penalty == "suspension")
            {
                until = suspendedUntil;
                if (until is not null && until.Value <= DateTime.UtcNow) throw new UserError("تاریخ پایان تعلیق باید در آینده باشد؛ برای تعلیق نامحدود خالی بگذارید.");
            }

            int? tripId = null;
            var code = Fa.Latin(tripCode).ToUpperInvariant();
            if (code.Length > 0)
            {
                var trip = await db.Trips.AsNoTracking().Where(t => t.Code == code).Select(t => new { t.TripId, t.DriverId }).FirstOrDefaultAsync(ct)
                           ?? throw new UserError($"سفری با کد «{code}» پیدا نشد.");
                if (trip.DriverId != driverId) throw new UserError($"سفر {code} برای این راننده نیست.");
                tripId = trip.TripId;
            }

            var v = new Violation
            {
                DriverId = driverId,
                CompanyId = d.CompanyId,
                TripId = tripId,
                Title = title,
                Description = description,
                Penalty = penalty,
                FineAmount = fine,
                SuspendedUntil = until,
                CreatedByAdminId = me.Id
            };
            db.Violations.Add(v);

            var label = OpsUi.PenaltyLabel(penalty);
            var body = title + (description is null ? "" : " — " + description)
                       + (fine is long f ? $" · جریمه: {Fa.Toman(f)}" : "")
                       + (until is DateTime u ? $" · تعلیق تا {Fa.Date(u)}" : penalty == "suspension" ? " · تعلیق تا اطلاع ثانوی" : "");
            audit.Add("Driver", driverId, "violation:" + penalty, $"تخلف رانندهٔ «{d.FullName}» ({label}): {title}",
                new { title, description, penalty, fine, until, tripId, code = code.Length == 0 ? null : code });

            var msg = $"تخلف «{title}» برای «{d.FullName}» ثبت شد ({label}).";
            if (penalty == "suspension" && d.Status != AccountStatus.Suspended)
            {
                // تعلیق از همان مسیر پروندهٔ کاربر: علت، اعلان، ردّ حسابرسی و SaveChanges (تخلف هم همراهش ذخیره می‌شود)
                msg = await AdminOps.SetAccountStatusAsync(db, notify, audit, OwnerKind.Driver, driverId, AccountStatus.Suspended, body, ct);
                msg = $"تخلف «{title}» ثبت شد و {msg}";
            }
            else
            {
                notify.Add(OwnerKind.Driver, driverId,
                    penalty switch { "fine" => "جریمه برای شما ثبت شد", "suspension" => "تخلف تازه‌ای روی حساب تعلیق‌شدهٔ شما ثبت شد", _ => "اخطار برای شما ثبت شد" },
                    body, "/Driver/Profile", "system");
                if (d.CompanyId is int cid)
                    notify.Add(OwnerKind.Company, cid, $"تخلف رانندهٔ شما ({d.FullName}) ثبت شد", body, "/Company/Drivers", "system");
                await db.SaveChangesAsync(ct);
            }
            TempData["ok"] = msg;
            return RedirectToAction(nameof(Violations));
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
            return back;
        }
    }

    // ------------------------------------------------------------------
    //  تعلیق حساب
    // ------------------------------------------------------------------

    /// <summary>
    /// رانندگان تعلیق‌شده با زمان تعلیق (از ردّ حسابرسی) و پایان تعلیقِ اعلام‌شده در
    /// آخرین تخلف؛ آن‌هایی که مهلتشان گذشته بالای فهرست می‌آیند تا مدیر فعالشان کند.
    /// </summary>
    public async Task<IActionResult> Suspended(string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تعلیق حساب رانندگان";
        var term = AdminOps.Term(q);
        var src = Search(db.Drivers.AsNoTracking().Where(d => d.Status == AccountStatus.Suspended), term);
        var pv = await PageVm<DriverRow>.FromAsync(AdminOps.DriverRows(db, src.OrderByDescending(d => d.CreatedAt)), page, PageLink.For(Request), ct: ct);

        var ids = pv.Rows.Select(r => r.DriverId).ToList();
        if (ids.Count > 0)
        {
            var suspendedAt = await db.AuditLogs.AsNoTracking()
                .Where(a => a.Entity == "Driver" && a.Action == "status:" + AccountStatus.Suspended && ids.Contains(a.EntityId))
                .GroupBy(a => a.EntityId).Select(g => new { g.Key, At = g.Max(a => a.CreatedAt) })
                .ToDictionaryAsync(x => x.Key, x => x.At, ct);
            // آخرین تخلفِ «تعلیق» هر راننده — مجموعه کوچک است، گروه‌بندی در حافظه
            var suspensions = await db.Violations.AsNoTracking()
                .Where(v => v.DriverId != null && ids.Contains(v.DriverId.Value) && v.Penalty == "suspension")
                .OrderByDescending(v => v.CreatedAt)
                .Select(v => new { DriverId = v.DriverId!.Value, v.SuspendedUntil })
                .ToListAsync(ct);
            foreach (var r in pv.Rows)
            {
                r.SuspendedAt = suspendedAt.GetValueOrDefault(r.DriverId);
                r.SuspendedUntil = suspensions.FirstOrDefault(s => s.DriverId == r.DriverId)?.SuspendedUntil;
            }
        }

        var now = DateTime.UtcNow;
        var vm = new PageVm<DriverRow>
        {
            Rows = pv.Rows.OrderBy(r => r.SuspendedUntil != null && r.SuspendedUntil < now ? 0 : 1).ThenByDescending(r => r.SuspendedAt ?? r.CreatedAt).ToList(),
            Page = pv.Page, PageSize = pv.PageSize, Total = pv.Total, Link = pv.Link
        };
        ViewBag.Q = term;
        ViewBag.Overdue = vm.Rows.Count(r => r.SuspendedUntil != null && r.SuspendedUntil < now);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Reactivate(int id, string? reason, string? returnUrl, CancellationToken ct)
    {
        try
        {
            TempData["ok"] = await AdminOps.SetAccountStatusAsync(db, notify, audit, OwnerKind.Driver, id, AccountStatus.Approved, reason, ct);
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Drivers/Suspended");
    }
}
