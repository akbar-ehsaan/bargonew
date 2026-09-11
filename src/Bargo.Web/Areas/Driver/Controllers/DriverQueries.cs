using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// پرس‌وجوهای مشترکِ چند کنترلر پنل راننده. کارت بار در داشبورد، بازار بار و «ثبت
/// پیشنهاد» یکی است؛ اگر هر کنترلر Select خودش را داشت، اولین ستونِ تازه (مثلاً «شمار
/// پیشنهادها») فقط در یکی از سه صفحه دیده می‌شد.
///
/// نکته: نام نوع «Driver» در این فضای نام به namespace همین Area می‌خورد نه به موجودیت؛
/// برای همین اینجا و در کنترلرها موجودیت راننده فقط با var و db.Drivers خوانده می‌شود.
/// </summary>
internal static class DriverQueries
{
    /// <summary>کارت بار؛ Saved و HasMyOffer مخصوص همین راننده‌اند.</summary>
    public static IQueryable<LoadCardVm> ToCards(this IQueryable<Load> q, BargoDbContext db, int driverId) =>
        q.Select(l => new LoadCardVm
        {
            LoadId = l.LoadId,
            Code = l.Code,
            Title = l.Title,
            CargoType = l.CargoType,
            From = l.OriginCity!.Name,
            To = l.DestCity!.Name,
            FromProvinceId = l.OriginCity!.ProvinceId,
            WeightTon = l.WeightTon,
            VehicleTypeId = l.VehicleTypeId,
            VehicleType = l.VehicleType!.Name,
            LoadingFrom = l.LoadingFrom,
            LoadingTo = l.LoadingTo,
            PriceMode = l.PriceMode,
            Price = l.Price,
            DistanceKm = l.DistanceKm,
            // بارِ بی‌مختصات به مرکز شهرش سنجیده می‌شود؛ خطای چند کیلومتری برای «نزدیک من» مهم نیست
            OriginLat = l.OriginLat ?? l.OriginCity!.Lat,
            OriginLng = l.OriginLng ?? l.OriginCity!.Lng,
            Status = l.Status,
            PublishedAt = l.PublishedAt,
            InsuranceRequested = l.InsuranceRequested,
            OfferCount = l.Offers.Count(o => o.Status == OfferStatus.Pending),
            Saved = db.SavedLoads.Any(s => s.DriverId == driverId && s.LoadId == l.LoadId),
            HasMyOffer = l.Offers.Any(o => o.DriverId == driverId && o.Status == OfferStatus.Pending)
        });

    /// <summary>ردیف فهرست سفرها (سفرهای من، تاریخچه، سفر در حال انجام).</summary>
    public static IQueryable<TripRowVm> ToTripRows(this IQueryable<Trip> q) =>
        q.Select(t => new TripRowVm
        {
            TripId = t.TripId,
            Code = t.Code,
            Title = t.Load!.Title,
            From = t.Load!.OriginCity!.Name,
            To = t.Load!.DestCity!.Name,
            Status = t.Status,
            CarrierKind = t.CarrierKind,
            CompanyName = t.Company!.Name,
            Fare = t.Fare,
            CarrierShare = t.CarrierShare,
            DriverShare = t.DriverShare,
            Plate = t.Vehicle!.PlateNo,
            TravelledKm = t.TravelledKm,
            PlannedKm = t.PlannedKm,
            ScheduledDepartureAt = t.ScheduledDepartureAt,
            DeliveredAt = t.DeliveredAt,
            CancelledAt = t.CancelledAt,
            CancelReason = t.CancelReason,
            CreatedAt = t.CreatedAt
        });

    /// <summary>
    /// موقعیت راننده: آخرین نقطهٔ GPS، وگرنه مختصات شهرِ پروفایل. Live=false یعنی
    /// فاصله‌ها از مرکز شهر سنجیده شده‌اند و صفحه باید همین را بگوید.
    /// </summary>
    public static async Task<(double? Lat, double? Lng, bool Live)> PositionAsync(this BargoDbContext db, int driverId, CancellationToken ct)
    {
        var d = await db.Drivers.AsNoTracking().Where(x => x.DriverId == driverId)
            .Select(x => new { x.LastLat, x.LastLng, CityLat = x.City!.Lat, CityLng = x.City!.Lng })
            .FirstOrDefaultAsync(ct);
        if (d is null) return (null, null, false);
        if (d.LastLat is double la && d.LastLng is double lo) return (la, lo, true);
        return (d.CityLat, d.CityLng, false);
    }

    public static void MeasureFrom(this IEnumerable<LoadCardVm> cards, double? lat, double? lng)
    {
        foreach (var c in cards)
            c.KmFromMe = Geo.Km(lat, lng, c.OriginLat, c.OriginLng) is double km ? Math.Round(km, 1) : null;
    }

    /// <summary>
    /// سفرِ «جاری» راننده: اول سفرِ در جریان (از حرکت به مبدا تا تخلیه)، وگرنه نزدیک‌ترین
    /// سفرِ پذیرفته یا در انتظار قبول. داشبورد، «سفر در حال انجام»، عملیات و بارنامه همین را می‌بینند.
    /// </summary>
    public static async Task<int?> CurrentTripIdAsync(this BargoDbContext db, int driverId, CancellationToken ct)
    {
        var live = await db.Trips.AsNoTracking()
            .Where(t => t.DriverId == driverId && TripStatus.Live.Contains(t.Status))
            .OrderByDescending(t => t.TripId).Select(t => (int?)t.TripId).FirstOrDefaultAsync(ct);
        if (live is not null) return live;
        return await db.Trips.AsNoTracking()
            .Where(t => t.DriverId == driverId && (t.Status == TripStatus.Accepted || t.Status == TripStatus.Assigned))
            .OrderBy(t => t.ScheduledDepartureAt).ThenBy(t => t.TripId)
            .Select(t => (int?)t.TripId).FirstOrDefaultAsync(ct);
    }

    /// <summary>صفحه‌بندیِ فهرستی که در حافظه مرتب شده (فاصله یا امتیاز پیشنهاد).</summary>
    public static PageVm<T> PageInMemory<T>(IReadOnlyList<T> all, int page, Func<int, string> link, int pageSize = 24)
    {
        var pages = all.Count == 0 ? 1 : (int)Math.Ceiling(all.Count / (double)pageSize);
        page = Math.Clamp(page, 1, pages);
        return new PageVm<T>
        {
            Rows = all.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page, PageSize = pageSize, Total = all.Count, Link = link
        };
    }

    public static Task<List<Province>> ProvinceListAsync(this BargoDbContext db, CancellationToken ct) =>
        db.Provinces.AsNoTracking().OrderBy(p => p.ProvinceId).ToListAsync(ct);

    public static Task<List<CityOpt>> CityListAsync(this BargoDbContext db, CancellationToken ct) =>
        db.Cities.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.ProvinceId).ThenBy(c => c.Name)
            .Select(c => new CityOpt(c.CityId, c.Name, c.ProvinceId))
            .ToListAsync(ct);

    public static Task<List<VehicleType>> VehicleTypeListAsync(this BargoDbContext db, CancellationToken ct) =>
        db.VehicleTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToListAsync(ct);

    /// <summary>
    /// همهٔ آنچه صفحهٔ سفر و صفحهٔ «عملیات سفر» لازم دارند. سفر باید از
    /// <see cref="TripFlow.FindForAsync"/> آمده باشد (شرط مالکیت همان‌جاست).
    /// </summary>
    public static async Task<TripDetailVm> TripDetailAsync(this BargoDbContext db, Trip trip, int driverId,
        SettingsService settings, HttpRequest req, CancellationToken ct)
    {
        var l = trip.Load!;
        var vm = new TripDetailVm { Trip = trip };

        vm.Events = await db.TripEvents.AsNoTracking().Where(e => e.TripId == trip.TripId)
            .OrderBy(e => e.CreatedAt).ToListAsync(ct);
        vm.Documents = await db.TripDocuments.AsNoTracking().Where(d => d.TripId == trip.TripId)
            .OrderByDescending(d => d.CreatedAt).ToListAsync(ct);

        // جدول نقاط پرحجم است: فقط ۵۰۰ نقطهٔ آخر، سپس به ترتیب زمان برای رسم خط
        var pts = await db.TrackingPoints.AsNoTracking().Where(p => p.TripId == trip.TripId)
            .OrderByDescending(p => p.RecordedAt).Take(500)
            .Select(p => new { p.Lat, p.Lng }).ToListAsync(ct);
        pts.Reverse();
        vm.Line = pts.Select(p => new[] { Math.Round(p.Lat, 5), Math.Round(p.Lng, 5) }).ToList();

        // هویت صاحب بار تا پرداخت کرایه از حمل‌کننده مخفی است (Privacy)
        var revealed = trip.IsPaid;
        if (l.Shipper is not null)
        {
            vm.OwnerName = Privacy.Name(l.Shipper.DisplayName, revealed);
            vm.OwnerMobile = Privacy.Optional(l.Shipper.Mobile, revealed);
        }
        else if (l.CompanyId is int cid)
        {
            var c = await db.Companies.AsNoTracking().Where(x => x.CompanyId == cid)
                .Select(x => new { x.Name, x.Mobile, x.Phone }).FirstOrDefaultAsync(ct);
            vm.OwnerName = Privacy.Name(c?.Name, revealed);
            vm.OwnerMobile = Privacy.Optional(c?.Mobile, revealed);
            vm.OwnerPhone = Privacy.Optional(c?.Phone, revealed);
            vm.OwnerIsCompany = true;
        }

        vm.Next = TripFlow.NextSteps(trip.Status, Roles.Driver);
        vm.CanCancel = TripFlow.CanCancel(trip.Status, Roles.Driver);
        vm.OtpRequired = await settings.GetBoolAsync(SettingsService.Keys.DeliveryOtp, ct);
        vm.WaybillRequired = await settings.GetBoolAsync(SettingsService.Keys.RequireWaybill, ct);
        vm.Rated = await db.Ratings.AnyAsync(r => r.TripId == trip.TripId && r.FromKind == Roles.Driver && r.FromId == driverId, ct);
        vm.CanRate = !vm.Rated && l.ShipperId is not null && (trip.Status is TripStatus.Delivered or TripStatus.Settled);
        vm.TrackUrl = $"{req.Scheme}://{req.Host}/t/{trip.TrackToken}";
        return vm;
    }
}
