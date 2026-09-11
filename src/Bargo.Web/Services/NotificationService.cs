using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Services;

/// <summary>
/// اعلان درون‌برنامه‌ای. SaveChanges با فراخواننده است تا اعلان همراه با خودِ رویداد
/// (مثلاً پذیرش پیشنهاد) ذخیره شود و هرگز اعلانِ کاری که انجام نشده نرود.
/// نقطهٔ اتصال Push در آینده همین‌جاست.
/// </summary>
public class NotificationService(BargoDbContext db)
{
    public void Add(string ownerKind, int ownerId, string title, string? body = null, string? link = null, string kind = "info")
    {
        if (ownerId <= 0) return;
        db.Notifications.Add(new Notification
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Title = title,
            Body = body,
            Link = link,
            Kind = kind
        });
    }

    /// <summary>اعلان به صاحب بار — صاحب بار عضو یا شرکتی که بار را برای مشتری‌اش ثبت کرده.</summary>
    public void ToLoadOwner(Load load, string title, string? body = null, string? link = null, string kind = "trip")
    {
        if (load.ShipperId is int s) Add(OwnerKind.Shipper, s, title, body, link ?? $"/Shipper/Loads/Detail/{load.LoadId}", kind);
        else if (load.CompanyId is int c) Add(OwnerKind.Company, c, title, body, link ?? $"/Company/Loads/Detail/{load.LoadId}", kind);
    }

    /// <summary>اعلان به حمل‌کنندهٔ سفر (راننده مستقل، یا شرکت و رانندهٔ تخصیص‌یافته‌اش).</summary>
    public void ToCarrier(Trip trip, string title, string? body = null, string kind = "trip")
    {
        if (trip.CompanyId is int c) Add(OwnerKind.Company, c, title, body, $"/Company/Trips/Detail/{trip.TripId}", kind);
        if (trip.DriverId is int d) Add(OwnerKind.Driver, d, title, body, $"/Driver/Trips/Detail/{trip.TripId}", kind);
    }

    public Task<int> UnreadAsync(string ownerKind, int ownerId, CancellationToken ct = default) =>
        db.Notifications.CountAsync(n => n.OwnerKind == ownerKind && n.OwnerId == ownerId && n.ReadAt == null, ct);

    /// <summary>اعلان عمومی مدیر. شمار گیرندگان را برمی‌گرداند؛ ذخیره با فراخواننده.</summary>
    public async Task<int> BroadcastAsync(string audience, string title, string body, string? link, CancellationToken ct = default)
    {
        var n = 0;
        if (audience is "all" or "drivers")
            foreach (var id in await db.Drivers.Where(d => d.Status == AccountStatus.Approved).Select(d => d.DriverId).ToListAsync(ct))
            { Add(OwnerKind.Driver, id, title, body, link, "system"); n++; }
        if (audience is "all" or "shippers")
            foreach (var id in await db.Shippers.Where(s => s.Status == AccountStatus.Approved).Select(s => s.ShipperId).ToListAsync(ct))
            { Add(OwnerKind.Shipper, id, title, body, link, "system"); n++; }
        if (audience is "all" or "companies")
            foreach (var id in await db.Companies.Where(c => c.Status == AccountStatus.Approved).Select(c => c.CompanyId).ToListAsync(ct))
            { Add(OwnerKind.Company, id, title, body, link, "system"); n++; }
        return n;
    }
}
