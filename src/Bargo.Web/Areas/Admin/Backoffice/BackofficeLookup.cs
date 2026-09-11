using System.Globalization;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.AdminPanel.Backoffice;

/// <summary>
/// کمک‌های مشترکِ کنترلرهای مالی/پشتیبانی/گزارش پنل مدیر: نامِ صاحبان، تجزیهٔ عدد
/// با ارقام فارسی، و مرزهای روز و ماه شمسی.
/// </summary>
public static class BackofficeLookup
{
    internal static readonly PersianCalendar Pc = new();

    private static readonly string[] MonthNames =
        ["", "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];

    /// <summary>صاحبانِ کیف پول که مدیر می‌تواند موجودی‌شان را ببیند یا اصلاح کند.</summary>
    public static readonly string[] WalletOwners = [OwnerKind.Driver, OwnerKind.Shipper, OwnerKind.Company];

    /// <summary>نام و موبایلِ صاحبانِ ردیف‌های یک صفحه — سه پرس‌وجوی کوچک، نه یکی برای هر ردیف.</summary>
    public static async Task<NameMap> NamesAsync(BargoDbContext db, IEnumerable<(string Kind, int Id)> owners, CancellationToken ct = default)
    {
        var map = new NameMap();
        var list = owners.Where(o => o.Id > 0).Distinct().ToList();

        var dIds = list.Where(o => o.Kind == OwnerKind.Driver).Select(o => o.Id).ToList();
        var sIds = list.Where(o => o.Kind == OwnerKind.Shipper).Select(o => o.Id).ToList();
        var cIds = list.Where(o => o.Kind == OwnerKind.Company).Select(o => o.Id).ToList();

        if (dIds.Count > 0)
            foreach (var d in await db.Drivers.AsNoTracking().Where(x => dIds.Contains(x.DriverId))
                         .Select(x => new { x.DriverId, x.FirstName, x.LastName, x.Mobile }).ToListAsync(ct))
                map.Set(OwnerKind.Driver, d.DriverId, $"{d.FirstName} {d.LastName}".Trim(), d.Mobile);

        if (sIds.Count > 0)
            foreach (var s in await db.Shippers.AsNoTracking().Where(x => sIds.Contains(x.ShipperId))
                         .Select(x => new { x.ShipperId, x.Kind, x.FullName, x.BusinessName, x.Mobile }).ToListAsync(ct))
                map.Set(OwnerKind.Shipper, s.ShipperId,
                    s.Kind == "business" && !string.IsNullOrWhiteSpace(s.BusinessName) ? s.BusinessName! : s.FullName, s.Mobile);

        if (cIds.Count > 0)
            foreach (var c in await db.Companies.AsNoTracking().Where(x => cIds.Contains(x.CompanyId))
                         .Select(x => new { x.CompanyId, x.Name, x.Mobile }).ToListAsync(ct))
                map.Set(OwnerKind.Company, c.CompanyId, c.Name, c.Mobile);

        return map;
    }

    /// <summary>کد خوانای سفرها برای ردیف‌های یک صفحه.</summary>
    public static async Task<Dictionary<int, string>> TripCodesAsync(BargoDbContext db, IEnumerable<int?> tripIds, CancellationToken ct = default)
    {
        var ids = tripIds.Where(i => i is > 0).Select(i => i!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Trips.AsNoTracking().Where(t => ids.Contains(t.TripId))
            .ToDictionaryAsync(t => t.TripId, t => t.Code, ct);
    }

    /// <summary>نام مدیرها (بررسی‌کننده، رسیدگی‌کننده).</summary>
    public static async Task<Dictionary<int, string>> AdminNamesAsync(BargoDbContext db, IEnumerable<int?> adminIds, CancellationToken ct = default)
    {
        var ids = adminIds.Where(i => i is > 0).Select(i => i!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Admins.AsNoTracking().Where(a => ids.Contains(a.AdminId))
            .ToDictionaryAsync(a => a.AdminId, a => a.Name, ct);
    }

    /// <summary>Area پنلِ هر صاحب — برای پیوندِ اعلان.</summary>
    public static string PanelOf(string ownerKind) => ownerKind switch
    {
        OwnerKind.Driver => "Driver",
        OwnerKind.Shipper => "Shipper",
        OwnerKind.Company => "Company",
        _ => ""
    };

    public static async Task<bool> OwnerExistsAsync(BargoDbContext db, string kind, int id, CancellationToken ct) => kind switch
    {
        OwnerKind.Driver => await db.Drivers.AnyAsync(x => x.DriverId == id, ct),
        OwnerKind.Shipper => await db.Shippers.AnyAsync(x => x.ShipperId == id, ct),
        OwnerKind.Company => await db.Companies.AnyAsync(x => x.CompanyId == id, ct),
        _ => false
    };

    // ------------------------------------------------------------------ عدد

    public static decimal? ParseDecimal(string? s)
    {
        var t = Fa.Latin(s).Replace('٫', '.');
        return t.Length > 0 && decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static double? ParseDouble(string? s)
    {
        var t = Fa.Latin(s).Replace('٫', '.');
        return t.Length > 0 && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static int? ParseInt(string? s)
    {
        var t = Fa.Latin(s);
        return t.Length > 0 && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static string Invariant(double? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
    public static string Invariant(decimal? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";

    // ------------------------------------------------------------------ زمان

    /// <summary>ابتدای روزِ تقویمیِ تهران که این لحظه در آن است، به UTC.</summary>
    public static DateTime DayStartUtc(DateTime utc) => Fa.ToUtc(Fa.ToTehran(utc).Date);

    /// <summary>
    /// اختلاف تهران با UTC به دقیقه — برای گروه‌بندی روزانه در خودِ SQL
    /// (<c>CreatedAt.AddMinutes(off).Date</c>). ایران از ۱۴۰۱ ساعت تابستانی ندارد، پس
    /// یک عدد ثابت برای کل بازه درست است.
    /// </summary>
    public static int TehranOffsetMinutes => (int)Math.Round((Fa.ToTehran(DateTime.UtcNow) - DateTime.UtcNow).TotalMinutes);

    public static string MonthLabel(int y, int m) => Fa.Digits($"{MonthNames[m]} {y}");

    public static string DayLabel(DateTime tehranDate) =>
        Fa.Digits($"{Pc.GetYear(tehranDate):0000}/{Pc.GetMonth(tehranDate):00}/{Pc.GetDayOfMonth(tehranDate):00}");

    /// <summary>n ماه شمسیِ اخیر (از قدیم به جدید) با ابتدای هر ماه به UTC؛ ماه جاری آخرین است.</summary>
    public static List<(int Y, int M, DateTime StartUtc)> LastPersianMonths(int n)
    {
        var now = Fa.Now;
        var y = Pc.GetYear(now);
        var m = Pc.GetMonth(now);
        var list = new List<(int, int, DateTime)>();
        for (var i = 0; i < n; i++)
        {
            list.Add((y, m, Fa.ToUtc(Pc.ToDateTime(y, m, 1, 0, 0, 0, 0))));
            if (--m == 0) { m = 12; y--; }
        }
        list.Reverse();
        return list;
    }
}

/// <summary>ردیفِ تجمیعِ روزانه از SQL — Day تاریخِ تقویمیِ تهران است (بدون ساعت).</summary>
public sealed class DayAgg
{
    public DateTime Day { get; set; }
    public int N { get; set; }
    public long A { get; set; }
    public long B { get; set; }
    public long C { get; set; }
    public long D { get; set; }
}

/// <summary>
/// سطل‌های زمانیِ گزارش (روز یا ماه شمسی). تجمیع در SQL روزانه انجام می‌شود و
/// اینجا فقط روزها در ماهِ شمسیِ خودشان جمع می‌شوند — SQL Server تقویم شمسی ندارد.
/// </summary>
public sealed class ReportBuckets
{
    private readonly Dictionary<string, int> _index = new();
    public bool Monthly { get; }
    public List<string> Labels { get; } = [];

    public ReportBuckets(DateTime fromUtc, DateTime toUtc, bool monthly)
    {
        Monthly = monthly;
        var end = Fa.ToTehran(toUtc).Date;
        for (var d = Fa.ToTehran(fromUtc).Date; d < end; d = d.AddDays(1))
        {
            var key = Key(d);
            if (_index.ContainsKey(key)) continue;
            _index[key] = Labels.Count;
            Labels.Add(monthly
                ? BackofficeLookup.MonthLabel(BackofficeLookup.Pc.GetYear(d), BackofficeLookup.Pc.GetMonth(d))
                : BackofficeLookup.DayLabel(d));
        }
    }

    private string Key(DateTime tehranDate) => Monthly
        ? $"{BackofficeLookup.Pc.GetYear(tehranDate)}-{BackofficeLookup.Pc.GetMonth(tehranDate)}"
        : tehranDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public long[] Fill(IEnumerable<DayAgg> rows, Func<DayAgg, long> pick)
    {
        var arr = new long[Labels.Count];
        foreach (var r in rows)
            if (_index.TryGetValue(Key(r.Day.Date), out var i)) arr[i] += pick(r);
        return arr;
    }
}
