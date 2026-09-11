namespace Bargo.Web.Areas.CompanyPanel.Controllers;

using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// تکه‌های مشترکِ کنترلرهای «تخصیص، سفر، رهگیری، بارنامه» — چیزهایی که هر چهار کنترلر
/// لازم دارند ولی به هیچ‌کدام تعلق ندارند: خط مسیر از نقاط GPS، نشانگرهای یک سفر،
/// بارگذاری سند سفر و نام روزهای هفته برای برنامهٔ حمل.
/// </summary>
internal static class CompanyTrips
{
    /// <summary>بیش از این تعداد نقطه روی نقشه، مرورگر را کند می‌کند؛ خط رقیق می‌شود.</summary>
    public const int MaxLinePoints = 1500;

    /// <summary>آخرین موقعیت اگر از این کهنه‌تر باشد «قطع ارتباط» شمرده می‌شود.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static readonly string[] AllDocKinds =
    [
        TripDocumentKind.LoadingReceipt, TripDocumentKind.DeliveryReceipt, TripDocumentKind.CargoInsurance,
        TripDocumentKind.Photo, TripDocumentKind.Other
    ];

    private static readonly string[] WeekDays = ["یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه", "شنبه"];

    /// <summary>نام روز هفته به وقت تهران.</summary>
    public static string WeekDay(DateTime utc) => WeekDays[(int)Fa.ToTehran(utc).DayOfWeek];

    // ------------------------------------------------------------------ خط مسیر

    /// <summary>خط مسیر از نقاط مرتب‌شده؛ در صورت زیاد بودن، یک‌درمیان برداشته می‌شود ولی نقطهٔ آخر همیشه می‌ماند.</summary>
    public static List<double[]> Line(IReadOnlyList<(double Lat, double Lng)> points)
    {
        if (points.Count == 0) return [];
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / (double)MaxLinePoints));
        var line = points.Where((_, i) => i % step == 0).Select(p => new[] { p.Lat, p.Lng }).ToList();
        if ((points.Count - 1) % step != 0) line.Add([points[^1].Lat, points[^1].Lng]);
        return line;
    }

    public static async Task<List<(double Lat, double Lng)>> TripPointsAsync(BargoDbContext db, int tripId, CancellationToken ct)
    {
        var rows = await db.TrackingPoints.AsNoTracking().Where(p => p.TripId == tripId)
            .OrderBy(p => p.RecordedAt).ThenBy(p => p.TrackingPointId)
            .Select(p => new { p.Lat, p.Lng }).ToListAsync(ct);
        return rows.Select(p => (p.Lat, p.Lng)).ToList();
    }

    // ------------------------------------------------------------------ نشانگرها

    /// <summary>مبدا، مقصد و آخرین موقعیت خودروی یک سفر (اگر سفر لغو نشده و موقعیتی دارد).</summary>
    public static List<OpsMarker> TripMarkers(Trip trip, (double Lat, double Lng)? lastPoint = null)
    {
        var l = trip.Load!;
        var list = new List<OpsMarker>();
        var oLat = l.OriginLat ?? l.OriginCity?.Lat;
        var oLng = l.OriginLng ?? l.OriginCity?.Lng;
        var dLat = l.DestLat ?? l.DestCity?.Lat;
        var dLng = l.DestLng ?? l.DestCity?.Lng;
        if (oLat is double ola && oLng is double olg)
            list.Add(new OpsMarker(ola, olg, "origin", $"مبدا: {l.OriginCity?.Name}\n{l.OriginAddress}", null));
        if (dLat is double dla && dLng is double dlg)
            list.Add(new OpsMarker(dla, dlg, "dest", $"مقصد: {l.DestCity?.Name}\n{l.DestAddress}", null));

        double? lat = trip.LastLat ?? lastPoint?.Lat, lng = trip.LastLng ?? lastPoint?.Lng;
        if (lat is double tla && lng is double tlg && trip.Status != TripStatus.Cancelled)
        {
            var live = TripStatus.Live.Contains(trip.Status);
            var stale = trip.LastPointAt is DateTime lp && DateTime.UtcNow - lp > StaleAfter;
            var who = string.Join(" · ", new[] { trip.Vehicle?.PlateNo, trip.Driver?.FullName }.Where(s => !string.IsNullOrEmpty(s)));
            list.Add(new OpsMarker(tla, tlg, live ? (stale ? "stale" : "truck") : "idle",
                $"سفر {trip.Code}\n{who}\nآخرین موقعیت: {(trip.LastPointAt is DateTime at ? Fa.Ago(at) : "—")}",
                $"/Company/Trips/Detail/{trip.TripId}"));
        }
        return list;
    }

    // ------------------------------------------------------------------ بارنامه

    /// <summary>قاعدهٔ مشترک ثبت بارنامه از پنل شرکت (پروندهٔ سفر و صفحهٔ «صدور/ثبت بارنامه»). شمارهٔ ثبت‌شده را برمی‌گرداند.</summary>
    public static async Task<string> RegisterWaybillAsync(TripFlow flow, DocumentStorage storage, Trip trip, string? number, IFormFile? file,
        Actor actor, CancellationToken ct)
    {
        var no = Fa.Latin(number);
        if (no.Length == 0) throw new UserError("شمارهٔ بارنامه را وارد کنید.");
        if (no.Length > 40) throw new UserError("شمارهٔ بارنامه حداکثر ۴۰ نویسه است.");
        if (trip.Waybill?.Status == "verified") throw new UserError("بارنامهٔ این سفر توسط مدیر تأیید شده و قابل تغییر نیست.");
        var path = await storage.SaveAsync(file, "trips", ct);
        await flow.RegisterWaybillAsync(trip, no, path, actor, ct);
        return no;
    }

    // ------------------------------------------------------------------ سند سفر

    /// <summary>
    /// افزودن یک سند به سفر از پنل شرکت. قاعده همان قاعدهٔ پنل راننده است، فقط شرکت
    /// «بیمه‌نامه بار» و «سایر» را هم می‌تواند بارگذاری کند.
    /// </summary>
    public static async Task<string> AddDocumentAsync(BargoDbContext db, DocumentStorage storage, NotificationService notify,
        Trip trip, string? kind, string? title, IFormFile? file, Actor actor, CancellationToken ct)
    {
        if (kind is null || !AllDocKinds.Contains(kind)) throw new UserError("نوع سند معتبر نیست.");
        if (trip.Status == TripStatus.Cancelled) throw new UserError("برای سفر لغوشده نمی‌توان سند بارگذاری کرد.");
        if (file is null || file.Length == 0) throw new UserError("فایل سند را انتخاب کنید.");

        var path = await storage.SaveAsync(file, "trips", ct);
        var t = Clean(title, 150) ?? TripDocumentKind.Label(kind);
        db.TripDocuments.Add(new TripDocument
        {
            TripId = trip.TripId, Kind = kind, Title = t, FilePath = path,
            UploadedByKind = actor.Kind, UploadedById = actor.Id
        });

        var load = trip.Load ?? await db.Loads.FirstAsync(l => l.LoadId == trip.LoadId, ct);
        // صاحب بارِ بیرونی خبردار می‌شود؛ بارِ خودِ شرکت اعلان به خودش نمی‌فرستد
        if (load.CompanyId != actor.CompanyId)
            notify.ToLoadOwner(load, $"سند تازه برای سفر {trip.Code}", $"{TripDocumentKind.Label(kind)}: {t}",
                load.ShipperId != null ? $"/Shipper/Tracking?tripId={trip.TripId}" : null);
        if (trip.DriverId is int d && trip.CompanyId == actor.CompanyId)
            notify.Add(OwnerKind.Driver, d, $"سند تازه برای سفر {trip.Code}", t, $"/Driver/Trips/Detail/{trip.TripId}");

        await db.SaveChangesAsync(ct);
        return t;
    }

    public static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
