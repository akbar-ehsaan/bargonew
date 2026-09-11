using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// قاعده‌های مشترکِ کنترلرهای عملیاتی پنل مدیر. هر قاعده‌ای که دو صفحه باید یکسان
/// ببینند اینجاست — «سفر مشکل‌دار» در داشبورد، صف مشکلات و هشدارهای زنده؛ «تغییر
/// وضعیت حساب» در کاربران، شرکت‌ها و رانندگان — تا عددِ کارت داشبورد و طولِ صف هرگز
/// از هم جدا نیفتند.
/// </summary>
internal static class AdminOps
{
    /// <summary>سفر زنده‌ای که این مدت GPS نفرستاده «بی‌خبر» است.</summary>
    public const double StaleHours = 2;

    public const int MaxNote = 1000;

    // ------------------------------------------------------------------
    //  ورودی
    // ------------------------------------------------------------------

    /// <summary>عبارت جستجو: ارقام فارسی → لاتین تا «۰۹۱۲…» هم پیدا شود.</summary>
    public static string Term(string? q)
    {
        var s = Fa.Latin(q);
        return s.Length > 60 ? s[..60] : s;
    }

    /// <summary>یادداشت پاک‌شده؛ خالی = null (نه رشتهٔ تهی) تا شرط «علت الزامی» ساده بماند.</summary>
    public static string? Note(string? s, int max = MaxNote)
    {
        var n = (s ?? "").Trim();
        if (n.Length == 0) return null;
        return n.Length > max ? n[..max] : n;
    }

    /// <summary>«driver» یا «driver:12» → (نوع، شناسه).</summary>
    public static (string? Kind, int? Id) ParseOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return (null, null);
        var parts = owner.Split(':', 2);
        var kind = parts[0] is OwnerKind.Driver or OwnerKind.Vehicle or OwnerKind.Company or OwnerKind.Shipper ? parts[0] : null;
        int? id = parts.Length == 2 && int.TryParse(parts[1], out var v) ? v : null;
        return (kind, kind is null ? null : id);
    }

    public static IActionResult Back(Controller c, string? returnUrl, string fallback) =>
        c.Redirect(!string.IsNullOrEmpty(returnUrl) && c.Url.IsLocalUrl(returnUrl) ? returnUrl : fallback);

    public static string Key(string kind, int id) => $"{kind}:{id}";

    // ------------------------------------------------------------------
    //  سفر مشکل‌دار
    // ------------------------------------------------------------------

    /// <summary>
    /// سه نشانهٔ مشکل: مدیر یا گردش کار علامتش زده (مثلاً لغو پس از بارگیری)؛ سفرِ
    /// زنده‌ای که بیش از دو ساعت GPS نفرستاده؛ و سفری که از زمان تقریبی رسیدن گذشته
    /// و هنوز تحویل نشده. دو نشانهٔ آخر محاسبه‌ای‌اند و با برگشتنِ GPS خودشان پاک می‌شوند.
    /// </summary>
    public static IQueryable<Trip> Problems(IQueryable<Trip> q, DateTime now)
    {
        var stale = now.AddHours(-StaleHours);
        return q.Where(t => t.IsProblem
                            || (TripStatus.Live.Contains(t.Status) && (t.LastPointAt == null || t.LastPointAt < stale))
                            || (TripStatus.Live.Contains(t.Status) && t.EtaAt != null && t.EtaAt < now));
    }

    public static List<string> ProblemReasons(TripRow t, DateTime now)
    {
        var list = new List<string>();
        if (t.IsProblem) list.Add(string.IsNullOrEmpty(t.ProblemNote) ? "علامت‌گذاری‌شده به‌عنوان مشکل‌دار" : t.ProblemNote!);
        if (TripStatus.Live.Contains(t.Status))
        {
            if (t.LastPointAt is null) list.Add("سفر زنده بدون هیچ موقعیت GPS");
            else if (t.LastPointAt < now.AddHours(-StaleHours)) list.Add($"بدون GPS از {Fa.Ago(t.LastPointAt.Value)}");
            if (t.EtaAt is DateTime eta && eta < now) list.Add($"گذشتن از زمان تقریبی رسیدن ({Fa.Stamp(eta)})");
        }
        return list;
    }

    /// <summary>ستون‌های مشترک فهرست‌های سفر — یک پرس‌وجو، بدون Include.</summary>
    public static IQueryable<TripRow> TripRows(IQueryable<Trip> q) => q.Select(t => new TripRow
    {
        TripId = t.TripId,
        Code = t.Code,
        LoadId = t.LoadId,
        LoadCode = t.Load!.Code,
        From = t.Load.OriginCity!.Name,
        To = t.Load.DestCity!.Name,
        CarrierKind = t.CarrierKind,
        CompanyId = t.CompanyId,
        CompanyName = t.Company!.Name,
        DriverId = t.DriverId,
        DriverName = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null,
        DriverMobile = t.Driver!.Mobile,
        Plate = t.Vehicle!.PlateNo,
        Status = t.Status,
        Fare = t.Fare,
        IsPaid = t.IsPaid,
        IsProblem = t.IsProblem,
        ProblemNote = t.ProblemNote,
        TravelledKm = t.TravelledKm,
        PlannedKm = t.PlannedKm,
        EtaAt = t.EtaAt,
        LastPointAt = t.LastPointAt,
        ScheduledDepartureAt = t.ScheduledDepartureAt,
        DeliveredAt = t.DeliveredAt,
        CancelledAt = t.CancelledAt,
        CancelReason = t.CancelReason,
        LoadTitle = t.Load.Title,
        WeightTon = t.Load.WeightTon,
        CreatedAt = t.CreatedAt
    });

    public static IQueryable<LoadRow> LoadRows(IQueryable<Load> q) => q.Select(l => new LoadRow
    {
        LoadId = l.LoadId,
        Code = l.Code,
        From = l.OriginCity!.Name,
        To = l.DestCity!.Name,
        Title = l.Title,
        CargoType = l.CargoType,
        WeightTon = l.WeightTon,
        OwnerName = l.Shipper != null
            ? (l.Shipper.Kind == "business" && l.Shipper.BusinessName != null && l.Shipper.BusinessName != "" ? l.Shipper.BusinessName : l.Shipper.FullName)
            : l.Company != null ? l.Company.Name : "",
        ShipperId = l.ShipperId,
        CompanyId = l.CompanyId,
        Price = l.Price,
        PriceMode = l.PriceMode,
        LoadingFrom = l.LoadingFrom,
        Status = l.Status,
        PendingOffers = l.Offers.Count(o => o.Status == OfferStatus.Pending),
        TripId = l.Trips.OrderByDescending(t => t.TripId).Select(t => (int?)t.TripId).FirstOrDefault(),
        TripCode = l.Trips.OrderByDescending(t => t.TripId).Select(t => t.Code).FirstOrDefault(),
        TripStatus = l.Trips.OrderByDescending(t => t.TripId).Select(t => t.Status).FirstOrDefault(),
        IsReported = l.IsReported,
        ReportNote = l.ReportNote,
        CancelReason = l.CancelReason,
        CreatedAt = l.CreatedAt
    });

    public static IQueryable<DriverRow> DriverRows(BargoDbContext db, IQueryable<Driver> q) => q.Select(d => new DriverRow
    {
        DriverId = d.DriverId,
        FullName = d.FirstName + " " + d.LastName,
        Mobile = d.Mobile,
        NationalCode = d.NationalCode,
        City = d.City!.Name,
        CompanyId = d.CompanyId,
        CompanyName = d.Company!.Name,
        Status = d.Status,
        StatusReason = d.StatusReason,
        TripCount = d.TripCount,
        Cancelled = db.Trips.Count(t => t.DriverId == d.DriverId && t.Status == TripStatus.Cancelled),
        Violations = db.Violations.Count(v => v.DriverId == d.DriverId),
        RatingAvg = d.RatingAvg,
        RatingCount = d.RatingCount,
        LastSeenAt = d.LastSeenAt,
        CreatedAt = d.CreatedAt
    });

    public static IQueryable<VehicleRow> VehicleRows(BargoDbContext db, IQueryable<Vehicle> q) => q.Select(v => new VehicleRow
    {
        VehicleId = v.VehicleId,
        PlateNo = v.PlateNo,
        TypeName = v.VehicleType!.Name,
        Brand = v.Brand,
        Model = v.Model,
        Year = v.Year,
        CapacityTon = v.CapacityTon,
        DriverId = v.DriverId,
        DriverName = v.Driver != null ? v.Driver.FirstName + " " + v.Driver.LastName : null,
        DriverMobile = v.Driver!.Mobile,
        CompanyId = v.CompanyId,
        CompanyName = v.Company!.Name,
        InsuranceExpiresAt = v.InsuranceExpiresAt,
        InspectionExpiresAt = v.InspectionExpiresAt,
        Status = v.Status,
        VerifyStatus = v.VerifyStatus,
        PendingDocs = db.Documents.Count(d => d.OwnerKind == OwnerKind.Vehicle && d.OwnerId == v.VehicleId && d.Status == AccountStatus.Pending),
        LastSeenAt = v.LastSeenAt,
        LastLat = v.LastLat,
        LastLng = v.LastLng,
        LiveTripId = db.Trips.Where(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status)).Select(t => (int?)t.TripId).FirstOrDefault(),
        LiveTripCode = db.Trips.Where(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status)).Select(t => t.Code).FirstOrDefault(),
        CreatedAt = v.CreatedAt
    });

    public static IQueryable<CompanyRow> CompanyRows(BargoDbContext db, IQueryable<Company> q, DateTime since) => q.Select(c => new CompanyRow
    {
        CompanyId = c.CompanyId,
        Name = c.Name,
        NationalId = c.NationalId,
        ManagerName = c.ManagerName,
        Mobile = c.Mobile,
        City = c.City!.Name,
        Status = c.Status,
        StatusReason = c.StatusReason,
        Drivers = db.Drivers.Count(d => d.CompanyId == c.CompanyId),
        Vehicles = db.Vehicles.Count(v => v.CompanyId == c.CompanyId),
        ActiveTrips = db.Trips.Count(t => t.CompanyId == c.CompanyId && TripStatus.Live.Contains(t.Status)),
        Trips30 = db.Trips.Count(t => t.CompanyId == c.CompanyId && t.CreatedAt >= since),
        LastLoginAt = c.Users.Max(u => u.LastLoginAt),
        LicenseNo = c.LicenseNo,
        LicenseExpiresAt = c.LicenseExpiresAt,
        WalletBalance = c.WalletBalance,
        CreatedAt = c.CreatedAt
    });

    /// <summary>
    /// مدرکی که مدرکِ تأییدشدهٔ تازه‌تری از همان نوع جایش را گرفته، دیگر «منقضی» یا
    /// «در آستانهٔ انقضا» نیست — بدون این شرط، راننده‌ای که بیمه‌اش را تمدید کرده تا
    /// ابد در صف هشدار می‌ماند.
    /// </summary>
    public static IQueryable<Document> NotSuperseded(BargoDbContext db, IQueryable<Document> q) =>
        q.Where(d => !db.Documents.Any(n => n.OwnerKind == d.OwnerKind && n.OwnerId == d.OwnerId && n.Kind == d.Kind &&
                                            n.Status == AccountStatus.Approved && n.UploadedAt > d.UploadedAt));

    // ------------------------------------------------------------------
    //  نقشهٔ زنده
    // ------------------------------------------------------------------

    /// <summary>
    /// نشانگرهای نقشهٔ کشور: هر سفر زنده یک کامیون روی آخرین موقعیتش؛ و (اختیاری)
    /// خودروهایی که در ۲۴ ساعت اخیر دیده شده‌اند ولی در سفر نیستند — «آزاد» اگر در
    /// یک ساعت اخیر، وگرنه «کهنه».
    /// </summary>
    public static async Task<List<MapMarker>> LiveMarkersAsync(BargoDbContext db, bool withVehicles, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var trips = await db.Trips.AsNoTracking()
            .Where(t => TripStatus.Live.Contains(t.Status) && t.LastLat != null && t.LastLng != null)
            .Select(t => new
            {
                t.TripId, t.Code, t.Status, t.LastLat, t.LastLng, t.LastPointAt, t.EtaAt,
                From = t.Load!.OriginCity!.Name, To = t.Load.DestCity!.Name,
                Driver = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null,
                Mobile = t.Driver!.Mobile,
                Plate = t.Vehicle!.PlateNo
            })
            .ToListAsync(ct);

        var list = trips.Select(t => new MapMarker(t.LastLat!.Value, t.LastLng!.Value, "truck",
            $"{t.Code} · {TripStatus.Label(t.Status)}\n{t.From} ← {t.To}\nراننده: {t.Driver ?? "تعیین نشده"}{(t.Mobile is null ? "" : " · " + t.Mobile)}" +
            $"{(t.Plate is null ? "" : "\nپلاک: " + t.Plate)}\nآخرین GPS: {(t.LastPointAt is DateTime lp ? Fa.Ago(lp) : "ثبت نشده")}" +
            $"{(t.EtaAt is DateTime eta ? "\nرسیدن تقریبی: " + Fa.Stamp(eta) : "")}",
            $"/Admin/Trips/Case/{t.TripId}")).ToList();

        if (!withVehicles) return list;

        var since = now.AddHours(-24);
        var idle = await db.Vehicles.AsNoTracking()
            .Where(v => v.LastLat != null && v.LastLng != null && v.LastSeenAt >= since &&
                        !db.Trips.Any(t => t.VehicleId == v.VehicleId && TripStatus.Live.Contains(t.Status)))
            .Select(v => new
            {
                v.VehicleId, v.PlateNo, v.LastLat, v.LastLng, v.LastSeenAt, v.DriverId, v.CompanyId,
                Type = v.VehicleType!.Name,
                Owner = v.Company != null ? v.Company.Name : v.Driver != null ? v.Driver.FirstName + " " + v.Driver.LastName : null
            })
            .ToListAsync(ct);

        list.AddRange(idle.Select(v => new MapMarker(v.LastLat!.Value, v.LastLng!.Value,
            v.LastSeenAt >= now.AddHours(-1) ? "idle" : "stale",
            $"{v.PlateNo} · {v.Type}\n{v.Owner ?? "—"}\nبدون سفر فعال · دیده‌شده {Fa.Ago(v.LastSeenAt!.Value)}",
            v.CompanyId is int c ? $"/Admin/Users/Company/{c}" : v.DriverId is int d ? $"/Admin/Users/Driver/{d}" : null)));
        return list;
    }

    // ------------------------------------------------------------------
    //  نام صاحبان
    // ------------------------------------------------------------------

    /// <summary>
    /// نام آدمیِ صاحبان (راننده، صاحب بار، شرکت، خودرو) برای یک صفحه — چهار پرس‌وجوی
    /// گروهی، نه یکی برای هر ردیف. کلید: «kind:id».
    /// </summary>
    public static async Task<Dictionary<string, OwnerRef>> OwnerRefsAsync(BargoDbContext db, IEnumerable<(string Kind, int Id)> owners, CancellationToken ct)
    {
        var all = owners.Distinct().ToList();
        var map = new Dictionary<string, OwnerRef>();

        var driverIds = all.Where(o => o.Kind == OwnerKind.Driver).Select(o => o.Id).ToList();
        var shipperIds = all.Where(o => o.Kind == OwnerKind.Shipper).Select(o => o.Id).ToList();
        var companyIds = all.Where(o => o.Kind == OwnerKind.Company).Select(o => o.Id).ToList();
        var vehicleIds = all.Where(o => o.Kind == OwnerKind.Vehicle).Select(o => o.Id).ToList();

        if (driverIds.Count > 0)
            foreach (var d in await db.Drivers.AsNoTracking().Where(d => driverIds.Contains(d.DriverId))
                         .Select(d => new { d.DriverId, d.FirstName, d.LastName, d.Mobile }).ToListAsync(ct))
                map[Key(OwnerKind.Driver, d.DriverId)] = new($"{d.FirstName} {d.LastName}".Trim(), "راننده · " + d.Mobile, $"/Admin/Users/Driver/{d.DriverId}");

        if (shipperIds.Count > 0)
            foreach (var s in await db.Shippers.AsNoTracking().Where(s => shipperIds.Contains(s.ShipperId))
                         .Select(s => new { s.ShipperId, s.FullName, s.BusinessName, s.Kind, s.Mobile }).ToListAsync(ct))
                map[Key(OwnerKind.Shipper, s.ShipperId)] = new(s.Kind == "business" && !string.IsNullOrWhiteSpace(s.BusinessName) ? s.BusinessName! : s.FullName,
                    "صاحب بار · " + s.Mobile, $"/Admin/Users/Shipper/{s.ShipperId}");

        if (companyIds.Count > 0)
            foreach (var c in await db.Companies.AsNoTracking().Where(c => companyIds.Contains(c.CompanyId))
                         .Select(c => new { c.CompanyId, c.Name, c.Mobile }).ToListAsync(ct))
                map[Key(OwnerKind.Company, c.CompanyId)] = new(c.Name, "شرکت · " + c.Mobile, $"/Admin/Users/Company/{c.CompanyId}");

        if (vehicleIds.Count > 0)
            foreach (var v in await db.Vehicles.AsNoTracking().Where(v => vehicleIds.Contains(v.VehicleId))
                         .Select(v => new
                         {
                             v.VehicleId, v.PlateNo, v.DriverId, v.CompanyId,
                             Owner = v.Company != null ? v.Company.Name : v.Driver != null ? v.Driver.FirstName + " " + v.Driver.LastName : null
                         }).ToListAsync(ct))
                map[Key(OwnerKind.Vehicle, v.VehicleId)] = new("خودروی " + v.PlateNo, v.Owner is null ? "بدون مالک" : "مالک: " + v.Owner,
                    v.CompanyId is int c ? $"/Admin/Users/Company/{c}" : v.DriverId is int d ? $"/Admin/Users/Driver/{d}" : null);

        if (all.Any(o => o.Kind == OwnerKind.Platform))
            map[Key(OwnerKind.Platform, 0)] = new("کیف پول بارگو", "کمیسیون سامانه");

        return map;
    }

    /// <summary>
    /// گیرندهٔ اعلانِ یک مدرک یا خودرو: مدرکِ خودرو به رانندهٔ مالک یا شرکتِ مالک می‌رسد،
    /// چون خودرو حساب کاربری ندارد.
    /// </summary>
    public static async Task<(string Kind, int Id, string Link)?> NotifyTargetAsync(BargoDbContext db, string ownerKind, int ownerId, string docKind, CancellationToken ct)
    {
        switch (ownerKind)
        {
            case OwnerKind.Driver:
                return (OwnerKind.Driver, ownerId, $"/Driver/Vehicle/Documents?kind={docKind}");
            case OwnerKind.Company:
                return (OwnerKind.Company, ownerId, "/Company/Account/Documents");
            case OwnerKind.Shipper:
                return (OwnerKind.Shipper, ownerId, "/Shipper/Profile/Verification");
            case OwnerKind.Vehicle:
                var v = await db.Vehicles.AsNoTracking().Where(x => x.VehicleId == ownerId)
                    .Select(x => new { x.DriverId, x.CompanyId }).FirstOrDefaultAsync(ct);
                if (v?.CompanyId is int c) return (OwnerKind.Company, c, $"/Company/Fleet/Documents?kind={docKind}");
                if (v?.DriverId is int d) return (OwnerKind.Driver, d, $"/Driver/Vehicle/Documents?kind={docKind}");
                return null;
            default:
                return null;
        }
    }

    public static async Task<Dictionary<int, string>> AdminNamesAsync(BargoDbContext db, IEnumerable<int?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return [];
        return await db.Admins.AsNoTracking().Where(a => list.Contains(a.AdminId)).ToDictionaryAsync(a => a.AdminId, a => a.Name, ct);
    }

    // ------------------------------------------------------------------
    //  وضعیت حساب
    // ------------------------------------------------------------------

    public static readonly string[] AccountStatuses = [AccountStatus.Pending, AccountStatus.Approved, AccountStatus.Rejected, AccountStatus.Suspended];

    /// <summary>
    /// تأیید / رد / تعلیق / فعال‌سازی دوبارهٔ حساب راننده، صاحب بار یا شرکت.
    ///
    /// چرا یک‌جا: این تغییر از پنج صفحه انجام می‌شود (پروندهٔ کاربر، فهرست شرکت‌ها،
    /// درخواست عضویت، تأیید راننده، تعلیق/تخلف) و هر پنج باید همان سه کار را بکنند —
    /// علتِ اجباری برای رد و تعلیق، اعلان به صاحب حساب، ردّ حسابرسی. اثرِ تعلیق
    /// بی‌درنگ است: ApprovalGateFilter وضعیت را در هر درخواست از پایگاه‌داده می‌خواند.
    /// SaveChanges همین‌جا صدا زده می‌شود.
    /// </summary>
    public static async Task<string> SetAccountStatusAsync(BargoDbContext db, NotificationService notify, AuditService audit,
        string kind, int id, string status, string? reason, CancellationToken ct)
    {
        if (status is not (AccountStatus.Approved or AccountStatus.Rejected or AccountStatus.Suspended))
            throw new UserError("وضعیت انتخاب‌شده معتبر نیست.");
        reason = Note(reason);
        if (status != AccountStatus.Approved && reason is null)
            throw new UserError("برای رد یا تعلیق حساب، علت را بنویسید؛ صاحب حساب باید بداند چه چیزی را اصلاح کند.");

        var now = DateTime.UtcNow;
        string name, from, entity, panel;
        var hint = "";

        switch (kind)
        {
            case OwnerKind.Driver:
            {
                var d = await db.Drivers.FirstOrDefaultAsync(x => x.DriverId == id, ct) ?? throw new UserError("راننده پیدا نشد.");
                from = d.Status;
                if (from == status) throw new UserError($"حساب این راننده از قبل «{AccountStatus.Label(status)}» است.");
                d.Status = status;
                d.StatusReason = status == AccountStatus.Approved ? null : reason;
                if (status == AccountStatus.Approved) d.ApprovedAt ??= now;
                name = d.FullName; entity = "Driver"; panel = "/Driver/Profile";

                if (status == AccountStatus.Approved)
                {
                    var pendingDocs = await db.Documents.CountAsync(x => x.OwnerKind == OwnerKind.Driver && x.OwnerId == id && x.Status == AccountStatus.Pending, ct);
                    if (pendingDocs > 0) hint = $" توجه: {Fa.N(pendingDocs)} مدرک این راننده هنوز در انتظار بررسی است و تا تأیید آن‌ها امکان گرفتن بار ندارد.";
                }
                else if (await db.Trips.AnyAsync(t => t.DriverId == id && TripStatus.Live.Contains(t.Status), ct))
                {
                    // سفرِ در راه با تعلیق حساب متوقف نمی‌شود — بار روی خودروست؛ مدیر باید تصمیم بگیرد
                    hint = " توجه: این راننده هم‌اکنون سفر در حال انجام دارد؛ وضعیت آن سفر را از «سفرهای جاری» پیگیری کنید.";
                }
                break;
            }
            case OwnerKind.Shipper:
            {
                var s = await db.Shippers.FirstOrDefaultAsync(x => x.ShipperId == id, ct) ?? throw new UserError("صاحب بار پیدا نشد.");
                from = s.Status;
                if (from == status) throw new UserError($"حساب این صاحب بار از قبل «{AccountStatus.Label(status)}» است.");
                s.Status = status;
                s.StatusReason = status == AccountStatus.Approved ? null : reason;
                name = s.DisplayName; entity = "Shipper"; panel = "/Shipper/Profile";
                if (status != AccountStatus.Approved &&
                    await db.Loads.AnyAsync(l => l.ShipperId == id && LoadStatus.Market.Contains(l.Status), ct))
                    hint = " توجه: بارهای منتشرشدهٔ این صاحب بار هنوز در بازار است؛ در صورت نیاز از «مدیریت بارها» لغوشان کنید.";
                break;
            }
            case OwnerKind.Company:
            {
                var c = await db.Companies.FirstOrDefaultAsync(x => x.CompanyId == id, ct) ?? throw new UserError("شرکت پیدا نشد.");
                from = c.Status;
                if (from == status) throw new UserError($"وضعیت این شرکت از قبل «{AccountStatus.Label(status)}» است.");
                c.Status = status;
                c.StatusReason = status == AccountStatus.Approved ? null : reason;
                if (status == AccountStatus.Approved) c.ApprovedAt ??= now;
                name = c.Name; entity = "Company"; panel = "/Company/Account";
                if (status == AccountStatus.Approved)
                {
                    var pendingDocs = await db.Documents.CountAsync(x => x.OwnerKind == OwnerKind.Company && x.OwnerId == id && x.Status == AccountStatus.Pending, ct);
                    if (pendingDocs > 0) hint = $" توجه: {Fa.N(pendingDocs)} مدرک این شرکت هنوز در انتظار بررسی است.";
                }
                else if (await db.Trips.AnyAsync(t => t.CompanyId == id && (TripStatus.Live.Contains(t.Status) || TripStatus.Upcoming.Contains(t.Status)), ct))
                    hint = " توجه: این شرکت سفر جاری دارد؛ کاربرانش دیگر به پنل دسترسی ندارند و پیگیری آن سفرها با مدیر است.";
                break;
            }
            default:
                throw new UserError("نوع حساب نامعتبر است.");
        }

        var (title, body) = (from, status) switch
        {
            (AccountStatus.Suspended, AccountStatus.Approved) => ("حساب شما دوباره فعال شد", "می‌توانید فعالیت در بارگو را از سر بگیرید."),
            (_, AccountStatus.Approved) => ("حساب شما تأیید شد", "به بارگو خوش آمدید؛ همهٔ امکانات پنل برایتان باز است."),
            (_, AccountStatus.Rejected) => ("درخواست عضویت شما تأیید نشد", reason),
            _ => ("حساب شما تعلیق شد", reason)
        };
        notify.Add(kind, id, title, body, panel, "system");

        audit.Add(entity, id, "status:" + status,
            $"{OwnerKind.Label(kind)} «{name}»: {AccountStatus.Label(from)} ← {AccountStatus.Label(status)}",
            new { from, to = status, reason });
        await db.SaveChangesAsync(ct);

        var verb = status switch
        {
            AccountStatus.Approved => from == AccountStatus.Suspended ? "دوباره فعال شد" : "تأیید شد",
            AccountStatus.Rejected => "رد شد",
            _ => "تعلیق شد"
        };
        return $"حساب «{name}» {verb}.{hint}";
    }
}
