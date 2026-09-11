using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.CompanyPanel.Controllers;

/// <summary>
/// «گزارش‌ها» — عملکرد ناوگان شرکت در یک بازهٔ تاریخ (پیش‌فرض ۳۰ روز اخیر). مبنا
/// سفرهایی است که شرکت حمل کرده (Trip.CompanyId)، بر پایهٔ تاریخ ساخت سفر؛ بارهای
/// شرکت که حمل‌کنندهٔ دیگری برده، عملکرد ناوگان نیست و در «مالی» دیده می‌شود.
///
/// تاریخ‌های نامعتبر در کوئری (ModelState) نادیده گرفته می‌شوند و بازهٔ پیش‌فرض می‌نشیند؛
/// گزارش هیچ‌وقت با پیام خطا خالی نمی‌ماند.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Reports)]
public class ReportsController(BargoDbContext db, CurrentUser me) : Controller
{
    private static readonly string[] DoneStatuses = TripStatus.Done;

    private ReportRange Range(DateTime? from, DateTime? to)
    {
        var r = ReportRange.From(from, to, 30);
        ViewBag.Range = r;
        return r;
    }

    /// <summary>سفرهای شرکت در بازه.</summary>
    private IQueryable<Trip> TripsIn(ReportRange r) => db.Trips.AsNoTracking()
        .Where(t => t.CompanyId == me.CompanyId && t.CreatedAt >= r.FromUtc && t.CreatedAt < r.ToUtc);

    // ------------------------------------------------------------------
    //  عملکرد رانندگان
    // ------------------------------------------------------------------

    public async Task<IActionResult> Drivers(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "عملکرد رانندگان";
        var cid = me.CompanyId;
        var r = Range(from, to);

        var rows = await TripsIn(r).Where(t => t.DriverId != null)
            .GroupBy(t => t.DriverId!.Value)
            .Select(g => new DriverPerfRow
            {
                DriverId = g.Key,
                Trips = g.Count(),
                Done = g.Count(t => DoneStatuses.Contains(t.Status)),
                Cancelled = g.Count(t => t.Status == TripStatus.Cancelled),
                Km = g.Sum(t => t.TravelledKm),
                Revenue = g.Sum(t => DoneStatuses.Contains(t.Status) ? t.CarrierShare : 0),
                DriverShare = g.Sum(t => DoneStatuses.Contains(t.Status) ? (t.DriverShare ?? 0) : 0)
            })
            .ToListAsync(ct);

        // رانندگان شرکت که در این بازه سفری نداشتند هم دیده شوند — «چه کسی بیکار مانده» خودش گزارش است
        var ids = rows.Select(x => x.DriverId).ToHashSet();
        var drivers = await db.Drivers.AsNoTracking()
            .Where(d => d.CompanyId == cid || ids.Contains(d.DriverId))
            .Select(d => new { d.DriverId, Name = d.FirstName + " " + d.LastName, d.Mobile, InCompany = d.CompanyId == cid, d.RatingAvg, d.RatingCount, d.Status })
            .ToListAsync(ct);
        foreach (var d in drivers.Where(d => !ids.Contains(d.DriverId) && d.Status != AccountStatus.Rejected))
            rows.Add(new DriverPerfRow { DriverId = d.DriverId });

        var info = drivers.ToDictionary(d => d.DriverId);
        var allIds = rows.Select(x => x.DriverId).ToList();
        var ratings = await db.Ratings.AsNoTracking()
            .Where(x => x.ToKind == OwnerKind.Driver && allIds.Contains(x.ToId) && x.CreatedAt >= r.FromUtc && x.CreatedAt < r.ToUtc)
            .GroupBy(x => x.ToId).Select(g => new { g.Key, Avg = g.Average(x => (double)x.Score), N = g.Count() })
            .ToDictionaryAsync(x => x.Key, ct);

        foreach (var row in rows)
        {
            if (info.TryGetValue(row.DriverId, out var d))
            {
                row.Name = d.Name.Trim();
                row.Mobile = d.Mobile;
                row.InCompany = d.InCompany;
                row.RatingAvg = d.RatingAvg;
                row.RatingCount = d.RatingCount;
            }
            if (ratings.TryGetValue(row.DriverId, out var rt))
            {
                row.PeriodRating = rt.Avg;
                row.PeriodRatingCount = rt.N;
            }
        }

        return View(rows.OrderByDescending(x => x.Done).ThenByDescending(x => x.Trips).ThenBy(x => x.Name).ToList());
    }

    // ------------------------------------------------------------------
    //  عملکرد خودروها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Vehicles(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "عملکرد خودروها";
        var cid = me.CompanyId;
        var r = Range(from, to);

        var agg = await TripsIn(r).Where(t => t.VehicleId != null)
            .GroupBy(t => t.VehicleId!.Value)
            .Select(g => new
            {
                VehicleId = g.Key,
                Trips = g.Count(),
                Done = g.Count(t => DoneStatuses.Contains(t.Status)),
                Km = g.Sum(t => t.TravelledKm),
                Revenue = g.Sum(t => DoneStatuses.Contains(t.Status) ? t.CarrierShare : 0)
            })
            .ToDictionaryAsync(x => x.VehicleId, ct);

        var ids = agg.Keys.ToList();
        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(v => v.CompanyId == cid || ids.Contains(v.VehicleId))
            .Select(v => new { v.VehicleId, v.PlateNo, Type = v.VehicleType!.Name, v.Status, InCompany = v.CompanyId == cid })
            .ToListAsync(ct);
        var allIds = vehicles.Select(v => v.VehicleId).ToList();

        var tracked = await db.TrackingPoints.AsNoTracking()
            .Where(p => p.VehicleId != null && allIds.Contains(p.VehicleId.Value) && p.RecordedAt >= r.FromUtc && p.RecordedAt < r.ToUtc)
            .GroupBy(p => p.VehicleId!.Value).Select(g => new { g.Key, Km = g.Sum(p => p.StepKm) })
            .ToDictionaryAsync(x => x.Key, x => x.Km, ct);
        var lastTrip = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId == cid && t.VehicleId != null && allIds.Contains(t.VehicleId.Value) && t.Status != TripStatus.Cancelled)
            .GroupBy(t => t.VehicleId!.Value).Select(g => new { g.Key, At = g.Max(t => t.CreatedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.At, ct);

        var rows = vehicles.Select(v =>
        {
            agg.TryGetValue(v.VehicleId, out var a);
            return new VehiclePerfRow
            {
                VehicleId = v.VehicleId, Plate = v.PlateNo, Type = v.Type, Status = v.Status, InCompany = v.InCompany,
                Trips = a?.Trips ?? 0, Done = a?.Done ?? 0, Km = a?.Km ?? 0, Revenue = a?.Revenue ?? 0,
                TrackedKm = tracked.GetValueOrDefault(v.VehicleId),
                LastTripAt = lastTrip.TryGetValue(v.VehicleId, out var at) ? at : null
            };
        }).OrderByDescending(x => x.Done).ThenByDescending(x => x.Trips).ThenBy(x => x.Plate).ToList();

        return View(rows);
    }

    // ------------------------------------------------------------------
    //  تعداد سفر
    // ------------------------------------------------------------------

    public async Task<IActionResult> Trips(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "تعداد سفر";
        var cid = me.CompanyId;
        var r = Range(from, to);

        var trips = await TripsIn(r)
            .Select(t => new { t.CreatedAt, t.Status, t.Fare, t.CarrierShare, t.TravelledKm, Tons = t.Load!.WeightTon, Own = t.Load.CompanyId == cid })
            .ToListAsync(ct);

        var months = PersianMonths.Between(r.FromUtc, r.ToUtc);
        string[] statusOrder = [.. TripStatus.Upcoming, .. TripStatus.Live, .. TripStatus.Done, TripStatus.Cancelled];
        string[] weekdays = ["شنبه", "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه"];
        static int WeekdayIndex(DateTime utc) => ((int)Fa.ToTehran(utc).DayOfWeek + 1) % 7; // شنبه = ۰

        var done = trips.Where(t => DoneStatuses.Contains(t.Status)).ToList();
        var vm = new TripsReportVm
        {
            Range = r,
            Total = trips.Count,
            Done = done.Count,
            Cancelled = trips.Count(t => t.Status == TripStatus.Cancelled),
            Active = trips.Count(t => t.Status != TripStatus.Cancelled && !DoneStatuses.Contains(t.Status)),
            Fare = done.Sum(t => t.Fare),
            CarrierShare = done.Sum(t => t.CarrierShare),
            Km = trips.Sum(t => t.TravelledKm),
            Tons = done.Sum(t => t.Tons),
            ByStatus = statusOrder.Select(s => new LabelCount
            {
                Key = s, Label = TripStatus.Label(s),
                Count = trips.Count(t => t.Status == s), Amount = trips.Where(t => t.Status == s).Sum(t => t.Fare)
            }).Where(x => x.Count > 0).ToList(),
            ByMonth = months.Select(m => new LabelCount
            {
                Key = $"{m.Year}-{m.Month}", Label = m.Label,
                Count = trips.Count(t => m.Contains(t.CreatedAt)),
                Amount = trips.Where(t => m.Contains(t.CreatedAt) && DoneStatuses.Contains(t.Status)).Sum(t => t.Fare)
            }).ToList(),
            BySource = new List<LabelCount>
            {
                new() { Key = "market", Label = "بازار بار و درخواست‌های مستقیم", Count = trips.Count(t => !t.Own), Amount = trips.Where(t => !t.Own && DoneStatuses.Contains(t.Status)).Sum(t => t.Fare) },
                new() { Key = "own", Label = "بارهای مشتریان شرکت", Count = trips.Count(t => t.Own), Amount = trips.Where(t => t.Own && DoneStatuses.Contains(t.Status)).Sum(t => t.Fare) }
            },
            ByWeekday = weekdays.Select((w, i) => new LabelCount
            {
                Key = i.ToString(), Label = w, Count = trips.Count(t => WeekdayIndex(t.CreatedAt) == i)
            }).ToList()
        };
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  درآمد دوره‌ای
    // ------------------------------------------------------------------

    public async Task<IActionResult> Revenue(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "درآمد دوره‌ای";
        // پیش‌فرض این گزارش شش ماه است، نه سی روز — درآمدِ یک ماه به‌تنهایی روند نشان نمی‌دهد
        var r = ReportRange.From(from, to, 180);
        ViewBag.Range = r;
        var months = await CompanyLedger.MonthlyAsync(db, me.CompanyId, PersianMonths.Between(r.FromUtc, r.ToUtc), ct);
        return View(new LedgerReportVm { Months = months, Range = r });
    }

    // ------------------------------------------------------------------
    //  بارهای حمل‌شده
    // ------------------------------------------------------------------

    public async Task<IActionResult> Cargo(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "بارهای حمل‌شده";
        var r = Range(from, to);

        var rows = await TripsIn(r)
            .GroupBy(t => t.Load!.CargoType)
            .Select(g => new CargoRow
            {
                CargoType = g.Key,
                Trips = g.Count(),
                Done = g.Count(t => DoneStatuses.Contains(t.Status)),
                Tons = g.Sum(t => DoneStatuses.Contains(t.Status) ? t.Load!.WeightTon : 0),
                Fare = g.Sum(t => t.Status != TripStatus.Cancelled ? t.Fare : 0)
            })
            .ToListAsync(ct);

        return View(rows.OrderByDescending(x => x.Trips).ThenByDescending(x => x.Fare).ToList());
    }

    // ------------------------------------------------------------------
    //  مسیرهای پرتردد
    // ------------------------------------------------------------------

    public async Task<IActionResult> Routes(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "مسیرهای پرتردد";
        var r = Range(from, to);

        var agg = await TripsIn(r)
            .GroupBy(t => new { Origin = t.Load!.OriginCity!.Name, Dest = t.Load.DestCity!.Name })
            .Select(g => new
            {
                g.Key.Origin, g.Key.Dest,
                Trips = g.Count(),
                Done = g.Count(t => DoneStatuses.Contains(t.Status)),
                AvgFare = g.Average(t => t.Status != TripStatus.Cancelled ? (double?)t.Fare : null),
                AvgKm = g.Average(t => t.PlannedKm),
                Tons = g.Sum(t => DoneStatuses.Contains(t.Status) ? t.Load!.WeightTon : 0)
            })
            .ToListAsync(ct);

        var rows = agg.Select(a => new RouteRow
        {
            Origin = a.Origin, Dest = a.Dest, Trips = a.Trips, Done = a.Done,
            AvgFare = a.AvgFare is double f ? (long)Math.Round(f) : 0,
            AvgKm = a.AvgKm, Tons = a.Tons
        }).OrderByDescending(x => x.Trips).ThenByDescending(x => x.Done).ToList();

        return View(rows);
    }
}
