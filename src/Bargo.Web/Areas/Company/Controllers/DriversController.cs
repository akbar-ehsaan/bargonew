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
/// «مدیریت رانندگان» — رانندگانِ عضو شرکت (Driver.CompanyId). شرکت راننده را می‌سازد و
/// مدارکش را به نیابت بارگذاری می‌کند، ولی تأیید حساب با مدیر بارگوست؛ همین‌جا هیچ
/// Status‌ی نوشته نمی‌شود. آمادگیِ راننده برای مأموریت از DriverReadiness خوانده می‌شود
/// تا با قاعدهٔ تخصیص یکی بماند.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Drivers)]
public class DriversController(BargoDbContext db, CurrentUser me, DriverReadiness readiness, SettingsService settings,
    DocumentStorage storage, AuditService audit, NotificationService notify) : Controller
{
    private static readonly string[] StatusFilters =
        [AccountStatus.Approved, AccountStatus.Pending, AccountStatus.Suspended, AccountStatus.Rejected];

    private static readonly string[] ActivityFilters = ["online", "available", "busy", "off"];

    private const int ActivityPageSize = 50;

    // =====================================================================
    //  لیست رانندگان
    // =====================================================================

    public async Task<IActionResult> Index(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "لیست رانندگان";
        var cid = me.CompanyId;
        var all = db.Drivers.AsNoTracking().Where(d => d.CompanyId == cid);

        var vm = new OpsDriverListVm
        {
            Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            Status = StatusFilters.Contains(status) ? status : null
        };

        var byStatus = await all.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        foreach (var s in StatusFilters) vm.Counts[s] = byStatus.Where(x => x.Key == s).Sum(x => x.N);
        vm.Counts["all"] = byStatus.Sum(x => x.N);

        var list = all;
        if (vm.Status is not null) list = list.Where(d => d.Status == vm.Status);
        if (vm.Q is not null)
        {
            var term = vm.Q;
            var mobile = new string(Fa.Latin(term).Where(char.IsAsciiDigit).ToArray());
            list = mobile.Length >= 3
                ? list.Where(d => d.Mobile.Contains(mobile) || d.FirstName.Contains(term) || d.LastName.Contains(term))
                : list.Where(d => d.FirstName.Contains(term) || d.LastName.Contains(term) || (d.FirstName + " " + d.LastName).Contains(term));
        }

        vm.Page = await PageVm<Driver>.FromAsync(
            list.Include(d => d.City)
                .OrderBy(d => d.Status == AccountStatus.Approved ? 0 : d.Status == AccountStatus.Pending ? 1 : 2)
                .ThenBy(d => d.FirstName).ThenBy(d => d.LastName),
            page, PageLink.For(Request), ct: ct);
        vm.Busy = await CompanyOps.BusyDriversAsync(db, cid, ct);
        foreach (var d in vm.Page.Rows)
            vm.Issues[d.DriverId] = await readiness.CheckAsync(d.DriverId, null, ct);
        return View(vm);
    }

    // =====================================================================
    //  افزودن راننده
    // =====================================================================

    [HttpGet]
    public async Task<IActionResult> Add(CancellationToken ct)
    {
        ViewData["Title"] = "افزودن راننده";
        ViewBag.Cities = await CompanyOps.CitiesAsync(db, ct);
        return View(new OpsDriverForm());
    }

    [HttpPost]
    public async Task<IActionResult> Add(OpsDriverForm f, CancellationToken ct)
    {
        ViewData["Title"] = "افزودن راننده";
        var cid = me.CompanyId;
        var errors = CompanyOps.BindingErrors(ModelState);

        var first = (f.FirstName ?? "").Trim();
        if (first.Length is < 2 or > 60) errors.Add("نام راننده را بنویسید (۲ تا ۶۰ نویسه).");
        var last = (f.LastName ?? "").Trim();
        if (last.Length is < 2 or > 60) errors.Add("نام خانوادگی راننده را بنویسید (۲ تا ۶۰ نویسه).");

        var mobile = Fa.NormMobile(f.Mobile);
        if (mobile.Length == 0) errors.Add("شماره موبایل معتبر نیست؛ قالب درست: 09xxxxxxxxx.");
        else if (await db.Drivers.AnyAsync(d => d.Mobile == mobile, ct))
            errors.Add("راننده‌ای با این شماره موبایل قبلاً در بارگو ثبت شده است. اگر رانندهٔ شماست، از پشتیبانی بخواهید حسابش را به شرکت منتقل کند.");

        var nc = Fa.Latin(f.NationalCode);
        if (!IranId.IsNationalCode(nc)) errors.Add("کد ملی معتبر نیست.");
        else if (await db.Drivers.AnyAsync(d => d.NationalCode == nc, ct))
            errors.Add("راننده‌ای با این کد ملی قبلاً ثبت شده است.");

        var license = Cut(Fa.Latin(f.LicenseNo), 30);
        var smart = Cut(Fa.Latin(f.SmartCardNo), 30);
        if (license is null && f.LicenseExpiresAt is not null) errors.Add("شمارهٔ گواهینامه را هم بنویسید.");
        if (smart is null && f.SmartCardExpiresAt is not null) errors.Add("شمارهٔ کارت هوشمند را هم بنویسید.");

        var pass = f.Password ?? "";
        if (pass.Length < 6) errors.Add("گذرواژهٔ اولیه دست‌کم ۶ نویسه باشد.");
        else if (pass.Length > 100) errors.Add("گذرواژه بیش از حد بلند است.");

        if (f.CityId is int cityId && !await db.Cities.AnyAsync(c => c.CityId == cityId && c.IsActive, ct))
            errors.Add("شهر انتخاب‌شده معتبر نیست.");

        if (errors.Count > 0)
        {
            ViewBag.Errors = errors;
            ViewBag.Cities = await CompanyOps.CitiesAsync(db, ct);
            f.Password = null;
            return View(f);
        }

        var companyName = await db.Companies.AsNoTracking().Where(c => c.CompanyId == cid).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "شرکت";
        var driver = new Driver
        {
            FirstName = first, LastName = last, Mobile = mobile, NationalCode = nc,
            PassHash = PasswordHasher.Hash(pass),
            LicenseNo = license, LicenseExpiresAt = license is null ? null : f.LicenseExpiresAt,
            SmartCardNo = smart, SmartCardExpiresAt = smart is null ? null : f.SmartCardExpiresAt,
            CityId = f.CityId,
            CompanyId = cid,
            Status = AccountStatus.Pending,
            IsAvailable = true
        };
        db.Drivers.Add(driver);
        await db.SaveChangesAsync(ct);

        audit.Add("Driver", driver.DriverId, "create", $"افزودن راننده {driver.FullName} به ناوگان {companyName}", new { driver.Mobile, driver.CompanyId });
        notify.Add(OwnerKind.Driver, driver.DriverId, $"به ناوگان {companyName} پیوستید",
            "حساب شما توسط شرکت ساخته شد. برای فعال‌شدن، کارت ملی، گواهینامه و کارت هوشمند را بارگذاری کنید تا مدیر بارگو تأیید کند.",
            "/Driver/Vehicle/Documents", "system");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"راننده {driver.FullName} ثبت شد. پس از بارگذاری مدارک و تأیید مدیر بارگو می‌تواند مأموریت بگیرد؛ مدارک را می‌توانید همین‌جا به نیابت از او بارگذاری کنید.";
        return RedirectToAction(nameof(Documents), new { driverId = driver.DriverId });
    }

    private static string? Cut(string s, int max)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        return s.Length > max ? s[..max] : s;
    }

    // =====================================================================
    //  پروندهٔ راننده
    // =====================================================================

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var driver = await db.Drivers.AsNoTracking()
            .Include(d => d.City).ThenInclude(c => c!.Province)
            .FirstOrDefaultAsync(d => d.DriverId == id && d.CompanyId == cid, ct);
        if (driver is null) return NotFound();
        ViewData["Title"] = driver.FullName;

        var vm = new OpsDriverDetailVm
        {
            Driver = driver,
            WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct),
            Issues = await readiness.CheckAsync(id, null, ct),
            Documents = await db.Documents.AsNoTracking()
                .Where(x => x.OwnerKind == OwnerKind.Driver && x.OwnerId == id)
                .OrderByDescending(x => x.UploadedAt).ThenByDescending(x => x.DocumentId)
                .ToListAsync(ct)
        };

        var trips = db.Trips.AsNoTracking().Where(t => t.DriverId == id);
        vm.TripTotal = await trips.CountAsync(ct);
        vm.Trips = await trips
            .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Vehicle)
            .OrderByDescending(t => t.CreatedAt)
            .Take(10).ToListAsync(ct);

        vm.Current = (await CompanyOps.BusyDriversAsync(db, cid, ct)).GetValueOrDefault(id);
        if (vm.Current is { } cur)
            vm.CurrentTrip = vm.Trips.FirstOrDefault(t => t.TripId == cur.TripId)
                             ?? await db.Trips.AsNoTracking()
                                 .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
                                 .Include(t => t.Load).ThenInclude(l => l!.DestCity)
                                 .Include(t => t.Vehicle)
                                 .FirstOrDefaultAsync(t => t.TripId == cur.TripId, ct);

        vm.Reviews = await db.Ratings.AsNoTracking()
            .Include(r => r.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.OriginCity)
            .Include(r => r.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.DestCity)
            .Where(r => r.ToKind == OwnerKind.Driver && r.ToId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(10).AsSplitQuery().ToListAsync(ct);

        return View(vm);
    }

    // =====================================================================
    //  مدارک رانندگان + بارگذاری به نیابت
    // =====================================================================

    public async Task<IActionResult> Documents(int? driverId, string? kind, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "مدارک رانندگان";
        var cid = me.CompanyId;
        var vm = new OpsDocumentsVm
        {
            Kinds = DocumentKind.ForDriver,
            Kind = DocumentKind.ForDriver.Contains(kind) ? kind : null,
            WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct),
            OwnerOptions = await CompanyOps.DriverListAsync(db, cid, ct)
        };
        if (driverId is int did)
        {
            if (!vm.OwnerOptions.Any(o => o.Id == did)) return NotFound();
            vm.OwnerId = did;
        }

        var q = db.Documents.AsNoTracking()
            .Where(x => x.OwnerKind == OwnerKind.Driver && db.Drivers.Any(d => d.DriverId == x.OwnerId && d.CompanyId == cid));
        if (vm.OwnerId is int oid) q = q.Where(x => x.OwnerId == oid);
        if (vm.Kind is not null) q = q.Where(x => x.Kind == vm.Kind);

        vm.Page = await PageVm<Document>.FromAsync(q.OrderByDescending(x => x.UploadedAt).ThenByDescending(x => x.DocumentId), page, PageLink.For(Request), ct: ct);
        vm.Owners = await db.Drivers.AsNoTracking().Where(d => d.CompanyId == cid)
            .Select(d => new { d.DriverId, Name = d.FirstName + " " + d.LastName })
            .ToDictionaryAsync(x => x.DriverId, x => x.Name.Trim(), ct);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Upload(int? driverId, string? kind, string? number, DateTime? expiresAt, IFormFile? file, CancellationToken ct)
    {
        var cid = me.CompanyId;
        try
        {
            if (!ModelState.IsValid) throw new UserError("تاریخ انقضا معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            var driver = driverId is int did
                ? await db.Drivers.FirstOrDefaultAsync(d => d.DriverId == did && d.CompanyId == cid, ct)
                : null;
            if (driver is null) throw new UserError("راننده را انتخاب کنید.");
            if (kind is null || !DocumentKind.ForDriver.Contains(kind)) throw new UserError("نوع مدرک را انتخاب کنید.");
            if (file is null || file.Length == 0) throw new UserError("تصویر یا فایل PDF مدرک را انتخاب کنید.");

            var label = DocumentKind.Label(kind);
            var expiring = DocumentKind.Expiring.Contains(kind);
            if (expiring && expiresAt is null) throw new UserError($"تاریخ انقضای {label} را وارد کنید.");
            var num = Cut(Fa.Latin(number), 40);

            var path = await storage.SaveAsync(file, "docs", ct);
            var doc = new Document
            {
                OwnerKind = OwnerKind.Driver, OwnerId = driver.DriverId, Kind = kind,
                Number = num, ExpiresAt = expiring ? expiresAt : null, FilePath = path, Status = AccountStatus.Pending
            };
            db.Documents.Add(doc);

            // شماره و تاریخِ روی پروفایل راننده با آخرین مدرک هم‌خوان می‌شود
            if (kind == DocumentKind.License)
            {
                if (num is not null) driver.LicenseNo = Cut(num, 30);
                if (expiresAt is DateTime le) driver.LicenseExpiresAt = le;
            }
            else if (kind == DocumentKind.SmartCard)
            {
                if (num is not null) driver.SmartCardNo = Cut(num, 30);
                if (expiresAt is DateTime se) driver.SmartCardExpiresAt = se;
            }
            await db.SaveChangesAsync(ct);

            audit.Add("Document", doc.DocumentId, "upload", $"بارگذاری {label} برای راننده {driver.FullName} توسط شرکت",
                new { doc.OwnerKind, doc.OwnerId, doc.Kind, doc.ExpiresAt });
            notify.Add(OwnerKind.Driver, driver.DriverId, $"{label} شما توسط شرکت بارگذاری شد",
                "مدرک در صف بررسی مدیر بارگو است؛ نتیجه به شما اعلان می‌شود.", $"/Driver/Vehicle/Documents?kind={kind}", "system");
            await db.SaveChangesAsync(ct);

            TempData["ok"] = $"«{label}» برای {driver.FullName} بارگذاری شد و در صف بررسی مدیر بارگو قرار گرفت.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Documents), new { driverId });
    }

    // =====================================================================
    //  وضعیت فعالیت
    // =====================================================================

    public async Task<IActionResult> Activity(string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "وضعیت فعالیت رانندگان";
        var cid = me.CompanyId;
        var since = DateTime.UtcNow.AddMinutes(-CompanyOps.OnlineMinutes);

        var drivers = await db.Drivers.AsNoTracking().Include(d => d.City)
            .Where(d => d.CompanyId == cid && d.Status == AccountStatus.Approved)
            .OrderByDescending(d => d.LastSeenAt).ThenBy(d => d.FirstName).ThenBy(d => d.LastName)
            .ToListAsync(ct);
        var busy = await CompanyOps.BusyDriversAsync(db, cid, ct);

        bool IsOnline(Driver d) => d.LastSeenAt > since;
        bool IsBusy(Driver d) => busy.GetValueOrDefault(d.DriverId) is { Hard: true };

        var vm = new OpsDriverListVm { Status = ActivityFilters.Contains(status) ? status : null, Busy = busy };
        vm.Counts["all"] = drivers.Count;
        vm.Counts["online"] = drivers.Count(IsOnline);
        vm.Counts["busy"] = drivers.Count(IsBusy);
        vm.Counts["available"] = drivers.Count(d => !IsBusy(d) && d.IsAvailable);
        vm.Counts["off"] = drivers.Count(d => !IsBusy(d) && !d.IsAvailable);

        var filtered = vm.Status switch
        {
            "online" => drivers.Where(IsOnline),
            "busy" => drivers.Where(IsBusy),
            "available" => drivers.Where(d => !IsBusy(d) && d.IsAvailable),
            "off" => drivers.Where(d => !IsBusy(d) && !d.IsAvailable),
            _ => drivers
        };
        var rows = filtered.ToList();
        var pages = rows.Count == 0 ? 1 : (int)Math.Ceiling(rows.Count / (double)ActivityPageSize);
        page = Math.Clamp(page, 1, pages);
        vm.Page = new PageVm<Driver>
        {
            Rows = rows.Skip((page - 1) * ActivityPageSize).Take(ActivityPageSize).ToList(),
            Page = page, PageSize = ActivityPageSize, Total = rows.Count, Link = PageLink.For(Request)
        };
        ViewBag.Since = since;
        return View(vm);
    }

    /// <summary>روشن/خاموش کردن «آماده بار» یک راننده توسط شرکت (راننده هم خودش می‌تواند).</summary>
    [HttpPost]
    public async Task<IActionResult> ToggleAvailable(int id, string? back, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var driver = await db.Drivers.FirstOrDefaultAsync(d => d.DriverId == id && d.CompanyId == cid, ct);
        if (driver is null) return NotFound();

        driver.IsAvailable = !driver.IsAvailable;
        var label = driver.IsAvailable ? "آماده بار" : "خارج از خدمت";
        audit.Add("Driver", id, "availability", $"وضعیت {driver.FullName} توسط شرکت: {label}", new { driver.IsAvailable });
        notify.Add(OwnerKind.Driver, id, $"وضعیت شما «{label}» شد", "این تغییر را شرکت انجام داده است؛ از پنل خود می‌توانید عوضش کنید.", "/Driver/Profile", "system");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"{driver.FullName} اکنون «{label}» است.";
        return back == "detail" ? RedirectToAction(nameof(Detail), new { id }) : RedirectToAction(nameof(Activity));
    }

    // =====================================================================
    //  امتیاز رانندگان
    // =====================================================================

    public async Task<IActionResult> Ratings(int? driverId, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "امتیاز رانندگان";
        var cid = me.CompanyId;

        var drivers = await db.Drivers.AsNoTracking()
            .Where(d => d.CompanyId == cid && d.Status != AccountStatus.Rejected)
            .ToListAsync(ct);
        var vm = new OpsRatingsVm
        {
            Drivers = drivers.OrderByDescending(d => d.RatingCount > 0 ? d.RatingAvg : -1).ThenByDescending(d => d.RatingCount).ThenByDescending(d => d.TripCount).ToList(),
            DriverNames = drivers.ToDictionary(d => d.DriverId, d => d.FullName),
            DriverId = driverId
        };
        if (driverId is int did && !vm.DriverNames.ContainsKey(did)) return NotFound();

        var q = db.Ratings.AsNoTracking()
            .Where(r => r.ToKind == OwnerKind.Driver && db.Drivers.Any(d => d.DriverId == r.ToId && d.CompanyId == cid));
        if (driverId is int d2) q = q.Where(r => r.ToId == d2);

        vm.Reviews = await PageVm<Rating>.FromAsync(
            q.Include(r => r.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.OriginCity)
                .Include(r => r.Trip).ThenInclude(t => t!.Load).ThenInclude(l => l!.DestCity)
                .OrderByDescending(r => r.CreatedAt),
            page, PageLink.For(Request), ct: ct);
        return View(vm);
    }

    // =====================================================================
    //  سوابق سفر
    // =====================================================================

    public async Task<IActionResult> Trips(int? driverId, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سوابق سفر رانندگان";
        var cid = me.CompanyId;
        var vm = new OpsDriverTripsVm { Drivers = await CompanyOps.DriverListAsync(db, cid, ct), DriverId = driverId };
        if (driverId is int did && !vm.Drivers.Any(o => o.Id == did)) return NotFound();

        // همهٔ سفرهای رانندگان شرکت — سفرِ مستقلِ راننده هم دیده می‌شود (با نشان «مستقل»)
        // تا شرکت بداند راننده‌اش کِی در دسترس نبوده؛ پروندهٔ آن سفر اما به شرکت تعلق ندارد.
        var q = db.Trips.AsNoTracking().Where(t => t.DriverId != null && t.Driver!.CompanyId == cid);
        if (driverId is int d2) q = q.Where(t => t.DriverId == d2);

        vm.Page = await PageVm<Trip>.FromAsync(
            q.Include(t => t.Load).ThenInclude(l => l!.OriginCity)
                .Include(t => t.Load).ThenInclude(l => l!.DestCity)
                .Include(t => t.Driver).Include(t => t.Vehicle)
                .OrderByDescending(t => t.CreatedAt),
            page, PageLink.For(Request), ct: ct);
        ViewBag.CompanyId = cid;
        return View(vm);
    }

    // =====================================================================
    //  رانندگان آنلاین
    // =====================================================================

    public async Task<IActionResult> Online(CancellationToken ct)
    {
        ViewData["Title"] = "رانندگان آنلاین";
        var cid = me.CompanyId;
        var since = DateTime.UtcNow.AddMinutes(-CompanyOps.OnlineMinutes);

        var vm = new OpsOnlineVm
        {
            Total = await db.Drivers.CountAsync(d => d.CompanyId == cid && d.Status == AccountStatus.Approved, ct),
            Drivers = await db.Drivers.AsNoTracking().Include(d => d.City)
                .Where(d => d.CompanyId == cid && d.Status == AccountStatus.Approved && d.LastSeenAt > since)
                .OrderByDescending(d => d.LastSeenAt)
                .ToListAsync(ct),
            Busy = await CompanyOps.BusyDriversAsync(db, cid, ct)
        };

        foreach (var d in vm.Drivers)
        {
            if (d.LastLat is not double lat || d.LastLng is not double lng) continue;
            var b = vm.Busy.GetValueOrDefault(d.DriverId);
            var inLiveTrip = b is not null && TripStatus.Live.Contains(b.Status);
            var (label, _) = OpsLabels.Availability(d, b);
            vm.Markers.Add(new OpsMarker(lat, lng, OpsLabels.LiveKind(inLiveTrip, d.LastSeenAt),
                $"{d.FullName}\n{label}\nآخرین حضور: {Fa.Ago(d.LastSeenAt!.Value)}", $"/Company/Drivers/Detail/{d.DriverId}"));
        }
        ViewBag.Minutes = CompanyOps.OnlineMinutes;
        return View(vm);
    }
}
