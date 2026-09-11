namespace Bargo.Web.Areas.CompanyPanel.Controllers;

// using‌ها عمداً داخل فضای نام‌اند: نام «Company/Driver/Admin/Shipper» در Bargo.Web.Areas
// فضای نامِ Areaهاست و بدون این، نام موجودیت‌ها به فضای نام حل می‌شد.
using System.Globalization;
using System.Text.Json;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// پرس‌وجوها و قاعده‌های مشترکِ کنترلرهای عملیات شرکت (داشبورد، تخصیص، ناوگان، رهگیری).
/// هر پرس‌وجو شرط شرکت را خودش دارد؛ فراخواننده فقط CompanyId کاربر جاری را می‌دهد.
/// </summary>
internal static class CompanyOps
{
    public const int OnlineMinutes = 30;

    /// <summary>
    /// پیوند «بار ← مشتری شرکت». جدول Loads هنوز ستون CompanyCustomerId ندارد؛ تا افزوده
    /// شدنش پیوند در ردّ حسابرسی (AuditLog) نشسته و فقط از همین دو متد خوانده/نوشته می‌شود،
    /// پس جابه‌جا کردنش به یک ستون واقعی تغییری یک‌جاست.
    /// </summary>
    public const string CustomerLinkAction = "company_customer";

    /// <summary>وضعیت‌هایی که راننده/خودرو را درگیر می‌کنند (رزرو، پذیرفته، در راه).</summary>
    public static readonly string[] Occupying =
    [
        TripStatus.Assigned, TripStatus.Accepted, TripStatus.ToOrigin, TripStatus.AtOrigin,
        TripStatus.Loaded, TripStatus.InTransit, TripStatus.Arrived, TripStatus.Unloaded
    ];

    /// <summary>راننده یا خودرو واقعاً مشغول است (نه صرفاً «در انتظار قبول»).</summary>
    public static readonly string[] HardBusy =
    [
        TripStatus.Accepted, TripStatus.ToOrigin, TripStatus.AtOrigin,
        TripStatus.Loaded, TripStatus.InTransit, TripStatus.Arrived, TripStatus.Unloaded
    ];

    private static int Rank(string status) => TripStatus.Live.Contains(status) ? 0 : status == TripStatus.Accepted ? 1 : 2;

    // ------------------------------------------------------------------ ورودی

    /// <summary>عدد اعشاری از فرم: «۲۲»، «2.5»، «۲٫۵» → decimal؛ نامعتبر → null.</summary>
    public static decimal? ParseDecimal(string? input)
    {
        var s = Fa.Latin(input).Replace('٫', '.').Replace('/', '.');
        return s.Length > 0 && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static int? ParseInt(string? input) =>
        int.TryParse(Fa.Latin(input), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>خطاهای بایندر (مثلاً تاریخ نامعتبر) به متن فارسی؛ پیام انگلیسیِ پیش‌فرض به کاربر نمی‌رسد.</summary>
    public static List<string> BindingErrors(ModelStateDictionary state) =>
        state.Values.SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage.Any(ch => ch is >= '؀' and <= 'ۿ') ? e.ErrorMessage : "یکی از مقادیر فرم معتبر نیست.")
            .Distinct().ToList();

    /// <summary>پلاک به قالب متعارف «12ب345-67» (Plate.Normalize) — تا «12 ع 345 67» و «۱۲ ع ۳۴۵ ایران ۶۷» یکی شمرده شوند و با ذخیرهٔ همهٔ پنل‌ها بخواند.</summary>
    public static string NormPlate(string? plate) => Plate.Normalize(plate);

    // ------------------------------------------------------------------ دسترسی

    public static async Task<bool> HasAsync(BargoDbContext db, CurrentUser me, CompanyPermission p, CancellationToken ct) =>
        await db.CompanyUsers.AsNoTracking()
            .Where(u => u.CompanyUserId == me.Id && u.IsActive)
            .Select(u => (CompanyPermission?)u.Permissions).FirstOrDefaultAsync(ct) is CompanyPermission perms && perms.HasFlag(p);

    // ------------------------------------------------------------------ داده پایه

    public static Task<List<OpsCityOption>> CitiesAsync(BargoDbContext db, CancellationToken ct) =>
        db.Cities.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.ProvinceId).ThenBy(c => c.Name)
            .Select(c => new OpsCityOption(c.CityId, c.Name, c.Province!.Name))
            .ToListAsync(ct);

    public static Task<List<OpsOption>> DriverListAsync(BargoDbContext db, int companyId, CancellationToken ct) =>
        db.Drivers.AsNoTracking().Where(d => d.CompanyId == companyId)
            .OrderBy(d => d.FirstName).ThenBy(d => d.LastName)
            .Select(d => new OpsOption(d.DriverId, d.FirstName + " " + d.LastName + " — " + d.Mobile))
            .ToListAsync(ct);

    public static Task<List<OpsOption>> VehicleListAsync(BargoDbContext db, int companyId, CancellationToken ct) =>
        db.Vehicles.AsNoTracking().Where(v => v.CompanyId == companyId)
            .OrderBy(v => v.PlateNo)
            .Select(v => new OpsOption(v.VehicleId, v.PlateNo + " — " + v.VehicleType!.Name))
            .ToListAsync(ct);

    // ------------------------------------------------------------------ مشغولیت

    /// <summary>برای هر رانندهٔ شرکت، مهم‌ترین سفرِ درگیرش (در راه ← پذیرفته ← در انتظار قبول).</summary>
    public static async Task<Dictionary<int, OpsBusy>> BusyDriversAsync(BargoDbContext db, int companyId, CancellationToken ct)
    {
        var rows = await db.Trips.AsNoTracking()
            .Where(t => t.DriverId != null && t.Driver!.CompanyId == companyId && Occupying.Contains(t.Status))
            .Select(t => new { DriverId = t.DriverId!.Value, t.TripId, t.Code, t.Status })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.DriverId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => Rank(r.Status)).Select(r => new OpsBusy(r.TripId, r.Code, r.Status)).First());
    }

    /// <summary>برای هر خودروی شرکت، سفرِ درگیرش به‌همراه نام راننده.</summary>
    public static async Task<Dictionary<int, OpsBusy>> BusyVehiclesAsync(BargoDbContext db, int companyId, CancellationToken ct)
    {
        var rows = await db.Trips.AsNoTracking()
            .Where(t => t.VehicleId != null && t.Vehicle!.CompanyId == companyId && Occupying.Contains(t.Status))
            .Select(t => new { VehicleId = t.VehicleId!.Value, t.TripId, t.Code, t.Status, Driver = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.VehicleId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => Rank(r.Status)).Select(r => new OpsBusy(r.TripId, r.Code, r.Status, r.Driver)).First());
    }

    // ------------------------------------------------------------------ گزینه‌های تخصیص

    /// <summary>
    /// رانندگان تأییدشدهٔ شرکت با وضعیت آمادگی (همان قاعدهٔ DriverReadiness که AssignAsync
    /// هم می‌سنجد)، مشغولیت و فاصله تا مبدا.
    /// </summary>
    public static async Task<List<OpsDriverOption>> DriverOptionsAsync(BargoDbContext db, DriverReadiness readiness, int companyId,
        double? originLat, double? originLng, CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.Status == AccountStatus.Approved)
            .OrderBy(d => d.FirstName).ThenBy(d => d.LastName)
            .ToListAsync(ct);
        var busy = await BusyDriversAsync(db, companyId, ct);

        var list = new List<OpsDriverOption>(drivers.Count);
        foreach (var d in drivers)
        {
            var issues = await readiness.CheckAsync(d.DriverId, null, ct);
            var km = Geo.Km(d.LastLat, d.LastLng, originLat, originLng);
            list.Add(new OpsDriverOption
            {
                DriverId = d.DriverId, Name = d.FullName, Mobile = d.Mobile, IsAvailable = d.IsAvailable, LastSeenAt = d.LastSeenAt,
                Busy = busy.GetValueOrDefault(d.DriverId),
                Blocking = issues.FirstOrDefault(i => i.Blocking)?.Text,
                Warning = issues.FirstOrDefault(i => !i.Blocking)?.Text,
                DistanceKm = km is double k ? Math.Round(k * Geo.RoadFactor) : null,
                RatingAvg = d.RatingAvg, RatingCount = d.RatingCount, TripCount = d.TripCount
            });
        }
        return list;
    }

    /// <summary>خودروهای فعال و تأییدشدهٔ شرکت، از کم‌ظرفیت به پرظرفیت.</summary>
    public static async Task<List<OpsVehicleOption>> VehicleOptionsAsync(BargoDbContext db, int companyId, CancellationToken ct)
    {
        var vehicles = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType)
            .Where(v => v.CompanyId == companyId && v.Status == VehicleStatus.Active && v.VerifyStatus == AccountStatus.Approved)
            .OrderBy(v => v.CapacityTon).ThenBy(v => v.PlateNo)
            .ToListAsync(ct);
        var busy = await BusyVehiclesAsync(db, companyId, ct);
        var now = DateTime.UtcNow;
        return vehicles.Select(v => new OpsVehicleOption
        {
            VehicleId = v.VehicleId, Plate = v.PlateNo, TypeName = v.VehicleType?.Name ?? "", VehicleTypeId = v.VehicleTypeId,
            CapacityTon = v.CapacityTon, Busy = busy.GetValueOrDefault(v.VehicleId),
            Warning = v.InsuranceExpiresAt < now ? "بیمه منقضی" : v.InspectionExpiresAt < now ? "معاینه فنی منقضی" : null
        }).ToList();
    }

    // ------------------------------------------------------------------ هشدار انقضا

    /// <summary>
    /// مدارک رانندگان و خودروهای شرکت که منقضی شده‌اند یا تا warnDays روز دیگر منقضی می‌شوند.
    /// از هر نوع مدرکِ هر صاحب فقط آخرین بارگذاری سنجیده می‌شود (بیمه‌نامهٔ تمدیدشده دیگر هشدار
    /// نمی‌دهد). تاریخ‌های ثبت‌شده روی خودرو/راننده فقط وقتی شمرده می‌شوند که مدرکی از آن نوع نباشد.
    /// </summary>
    public static async Task<List<OpsExpiryAlert>> ExpiryAlertsAsync(BargoDbContext db, int companyId, int warnDays, CancellationToken ct)
    {
        var limit = DateTime.UtcNow.AddDays(Math.Max(0, warnDays));

        var drivers = await db.Drivers.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.Status != AccountStatus.Rejected)
            .Select(d => new { d.DriverId, Name = d.FirstName + " " + d.LastName, d.LicenseExpiresAt, d.SmartCardExpiresAt })
            .ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(v => v.CompanyId == companyId)
            .Select(v => new { v.VehicleId, v.PlateNo, v.InsuranceExpiresAt, v.InspectionExpiresAt })
            .ToListAsync(ct);
        var dIds = drivers.Select(d => d.DriverId).ToList();
        var vIds = vehicles.Select(v => v.VehicleId).ToList();

        var docs = await db.Documents.AsNoTracking()
            .Where(x => x.Status != AccountStatus.Rejected &&
                        ((x.OwnerKind == OwnerKind.Driver && dIds.Contains(x.OwnerId)) ||
                         (x.OwnerKind == OwnerKind.Vehicle && vIds.Contains(x.OwnerId))))
            .Select(x => new { x.DocumentId, x.OwnerKind, x.OwnerId, x.Kind, x.ExpiresAt, x.Status, x.UploadedAt })
            .ToListAsync(ct);
        var latest = docs.GroupBy(x => (x.OwnerKind, x.OwnerId, x.Kind))
            .Select(g => g.OrderByDescending(x => x.UploadedAt).ThenByDescending(x => x.DocumentId).First())
            .ToList();
        var covered = latest.Select(x => (x.OwnerKind, x.OwnerId, x.Kind)).ToHashSet();

        var dName = drivers.ToDictionary(d => d.DriverId, d => d.Name.Trim());
        var vName = vehicles.ToDictionary(v => v.VehicleId, v => v.PlateNo);
        static string Href(string kind, int id) =>
            kind == OwnerKind.Driver ? $"/Company/Drivers/Documents?driverId={id}" : $"/Company/Fleet/Documents?vehicleId={id}";

        var list = new List<OpsExpiryAlert>();
        foreach (var x in latest)
        {
            if (x.ExpiresAt is not DateTime e || e >= limit) continue;
            var subject = x.OwnerKind == OwnerKind.Driver ? dName.GetValueOrDefault(x.OwnerId, "") : vName.GetValueOrDefault(x.OwnerId, "");
            list.Add(new OpsExpiryAlert(x.OwnerKind, x.OwnerId, subject, x.Kind, e, true, x.Status, Href(x.OwnerKind, x.OwnerId)));
        }

        void Field(string ownerKind, int id, string subject, string kind, DateTime? expires)
        {
            if (expires is DateTime e && e < limit && !covered.Contains((ownerKind, id, kind)))
                list.Add(new OpsExpiryAlert(ownerKind, id, subject, kind, e, false, null, Href(ownerKind, id)));
        }
        foreach (var d in drivers)
        {
            Field(OwnerKind.Driver, d.DriverId, d.Name.Trim(), DocumentKind.License, d.LicenseExpiresAt);
            Field(OwnerKind.Driver, d.DriverId, d.Name.Trim(), DocumentKind.SmartCard, d.SmartCardExpiresAt);
        }
        foreach (var v in vehicles)
        {
            Field(OwnerKind.Vehicle, v.VehicleId, v.PlateNo, DocumentKind.Insurance, v.InsuranceExpiresAt);
            Field(OwnerKind.Vehicle, v.VehicleId, v.PlateNo, DocumentKind.Inspection, v.InspectionExpiresAt);
        }
        return list.OrderBy(a => a.ExpiresAt).ToList();
    }

    // ------------------------------------------------------------------ مشتریِ بار

    public static void LinkCustomer(AuditService audit, int loadId, CompanyCustomer customer) =>
        audit.Add("Load", loadId, CustomerLinkAction, customer.Name, new { customerId = customer.CompanyCustomerId });

    /// <summary>مشتریِ هر بار (فقط مشتریانِ همین شرکت؛ پیوندِ ناهمخوان نادیده گرفته می‌شود).</summary>
    public static async Task<Dictionary<int, CompanyCustomer>> CustomersOfLoadsAsync(BargoDbContext db, int companyId,
        IEnumerable<int> loadIds, CancellationToken ct)
    {
        var ids = loadIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var links = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Entity == "Load" && a.Action == CustomerLinkAction && ids.Contains(a.EntityId))
            .OrderBy(a => a.AuditLogId)
            .Select(a => new { a.EntityId, a.DetailJson })
            .ToListAsync(ct);

        var map = new Dictionary<int, int>();
        foreach (var l in links)
            if (CustomerIdOf(l.DetailJson) is int cuId) map[l.EntityId] = cuId;
        if (map.Count == 0) return [];

        var custIds = map.Values.Distinct().ToList();
        var customers = await db.CompanyCustomers.AsNoTracking().Include(c => c.Shipper)
            .Where(c => c.CompanyId == companyId && custIds.Contains(c.CompanyCustomerId))
            .ToDictionaryAsync(c => c.CompanyCustomerId, ct);

        return map.Where(kv => customers.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => customers[kv.Value]);
    }

    private static int? CustomerIdOf(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("customerId", out var p) && p.TryGetInt32(out var v) ? v : null;
        }
        catch (JsonException) { return null; }
    }
}
