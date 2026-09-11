using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Services;

/// <summary>
/// «آیا این راننده می‌تواند بار بگیرد؟» — یک‌جا، تا بازار بار، ثبت پیشنهاد و تخصیص
/// شرکت هر سه همان قاعده را ببینند.
///
/// مسدودکننده: حساب تأییدنشده، مدرکِ لازمِ بارگذاری‌نشده یا در انتظار، و (با تنظیم
/// Documents.BlockOnExpired) مدرک منقضی. هشدار: مدرکی که تا ExpiryWarnDays روز
/// دیگر منقضی می‌شود.
/// </summary>
public class DriverReadiness(BargoDbContext db, SettingsService settings)
{
    public sealed record Issue(string Text, bool Blocking, string? Link = null);

    public async Task<List<Issue>> CheckAsync(int driverId, int? vehicleId = null, CancellationToken ct = default)
    {
        var list = new List<Issue>();
        var d = await db.Drivers.AsNoTracking().FirstOrDefaultAsync(x => x.DriverId == driverId, ct);
        if (d is null)
        {
            list.Add(new Issue("راننده پیدا نشد.", true));
            return list;
        }

        if (d.Status != AccountStatus.Approved)
            list.Add(new Issue($"وضعیت حساب: {AccountStatus.Label(d.Status)} — تا تأیید مدیر امکان گرفتن بار نیست.", true, "/Driver/Profile"));

        var warnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        var blockExpired = await settings.GetBoolAsync(SettingsService.Keys.BlockOnExpired, ct);
        var now = DateTime.UtcNow;

        var driverDocs = await db.Documents.AsNoTracking()
            .Where(x => x.OwnerKind == OwnerKind.Driver && x.OwnerId == driverId)
            .ToListAsync(ct);
        foreach (var kind in DocumentKind.ForDriver)
            CheckDoc(kind, driverDocs, "", $"/Driver/Vehicle/Documents?kind={kind}");

        // خودرو: یا همانی که پاس داده شده، یا خودروی فعال راننده مستقل. رانندهٔ شرکت
        // خودرو را هنگام تخصیص می‌گیرد، پس نداشتنِ خودرو برایش مانع نیست.
        Vehicle? veh = vehicleId is int vid
            ? await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.VehicleId == vid, ct)
            : await db.Vehicles.AsNoTracking()
                .Where(v => v.DriverId == driverId && v.Status == VehicleStatus.Active)
                .OrderByDescending(v => v.VehicleId).FirstOrDefaultAsync(ct);

        if (veh is null)
        {
            if (d.CompanyId is null)
                list.Add(new Issue("هنوز خودرویی ثبت نکرده‌اید.", true, "/Driver/Vehicle"));
        }
        else
        {
            if (veh.VerifyStatus != AccountStatus.Approved)
                list.Add(new Issue($"خودروی {veh.PlateNo} هنوز تأیید نشده است.", true, "/Driver/Vehicle"));
            if (veh.Status != VehicleStatus.Active)
                list.Add(new Issue($"خودروی {veh.PlateNo} در وضعیت «{VehicleStatus.Label(veh.Status)}» است.", true, "/Driver/Vehicle"));

            var vehDocs = await db.Documents.AsNoTracking()
                .Where(x => x.OwnerKind == OwnerKind.Vehicle && x.OwnerId == veh.VehicleId)
                .ToListAsync(ct);
            foreach (var kind in DocumentKind.ForVehicle)
                CheckDoc(kind, vehDocs, $" خودروی {veh.PlateNo}", $"/Driver/Vehicle/Documents?kind={kind}");
        }

        return list;

        void CheckDoc(string kind, List<Document> docs, string suffix, string link)
        {
            var label = DocumentKind.Label(kind) + suffix;
            var doc = docs.Where(x => x.Kind == kind && x.Status != AccountStatus.Rejected)
                .OrderByDescending(x => x.UploadedAt).FirstOrDefault();
            if (doc is null)
            {
                list.Add(new Issue($"{label} بارگذاری نشده است.", true, link));
                return;
            }
            if (doc.Status == AccountStatus.Pending)
            {
                list.Add(new Issue($"{label} در انتظار بررسی مدیر است.", true, link));
                return;
            }
            if (doc.ExpiresAt is DateTime e)
            {
                if (e < now)
                    list.Add(new Issue($"{label} در {Fa.Date(e)} منقضی شده است.", blockExpired, link));
                else if (e < now.AddDays(warnDays))
                    list.Add(new Issue($"{label} فقط تا {Fa.Date(e)} اعتبار دارد.", false, link));
            }
        }
    }

    public static bool IsReady(IEnumerable<Issue> issues) => !issues.Any(i => i.Blocking);
}
