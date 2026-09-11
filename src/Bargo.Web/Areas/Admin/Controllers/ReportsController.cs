using Bargo.Web.Areas.AdminPanel.Backoffice;
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
/// گزارش‌ها و آمار: مالی، کاربران، بارها، سفرها، رانندگان، شرکت‌ها، استان و شهر،
/// مسیرهای پرتردد — همه با بازهٔ دلخواه (پیش‌فرض ۳۰ روز اخیر).
///
/// تجمیع همیشه در SQL و «روزانه» است (به تاریخ تهران)؛ سطل‌بندی ماهانه در حافظه
/// انجام می‌شود چون SQL Server تقویم شمسی ندارد. بازهٔ کوتاه‌تر از ۴۵ روز روزانه
/// و بلندتر ماهانه نمایش داده می‌شود تا جدول نه خیلی بلند شود و نه بی‌جزئیات.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Reports)]
public class ReportsController(BargoDbContext db) : Controller
{
    private const int DefaultDays = 30;
    private const int MaxDays = 730;
    private const int MonthlyFrom = 45;
    private const int TopN = 20;

    public IActionResult Index() => RedirectToAction(nameof(Finance));

    // ------------------------------------------------------------------
    //  بازه
    // ------------------------------------------------------------------

    /// <summary>
    /// از ابتدای روزِ «از» تا ابتدای روزِ پس از «تا» (انحصاری)، به وقت تهران. ورودی
    /// نامعتبر یا خالی → ۳۰ روز اخیر؛ بازهٔ وارونه جابه‌جا می‌شود؛ سقف دو سال.
    /// </summary>
    private static ReportRangeVm Range(DateTime? from, DateTime? to, string action)
    {
        var today = BackofficeLookup.DayStartUtc(DateTime.UtcNow);
        var end = to is DateTime t ? BackofficeLookup.DayStartUtc(t).AddDays(1) : today.AddDays(1);
        var start = from is DateTime f ? BackofficeLookup.DayStartUtc(f) : end.AddDays(-DefaultDays);
        if (start >= end) (start, end) = (end.AddDays(-1), start.AddDays(1));
        if ((end - start).TotalDays > MaxDays) start = end.AddDays(-MaxDays);
        var days = (int)Math.Round((end - start).TotalDays);
        return new ReportRangeVm { FromUtc = start, ToUtc = end, Monthly = days > MonthlyFrom, Action = action };
    }

    private static TimeSeriesVm Series(ReportRangeVm r, params SeriesVm[] series) =>
        new() { Labels = new ReportBuckets(r.FromUtc, r.ToUtc, r.Monthly).Labels, Series = [.. series], FirstColumn = r.Monthly ? "ماه" : "روز" };

    // ------------------------------------------------------------------
    //  مالی
    // ------------------------------------------------------------------

    public async Task<IActionResult> Finance(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "گزارش مالی";
        var r = Range(from, to, nameof(Finance));
        var off = BackofficeLookup.TehranOffsetMinutes;
        var b = new ReportBuckets(r.FromUtc, r.ToUtc, r.Monthly);

        // کرایه و کمیسیون بر پایهٔ زمان تسویه — همان مبنای «گزارش درآمد» مالی
        var settled = await db.Trips.AsNoTracking()
            .Where(t => t.SettledAt != null && t.SettledAt >= r.FromUtc && t.SettledAt < r.ToUtc)
            .GroupBy(t => t.SettledAt!.Value.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count(), A = g.Sum(t => t.Fare), B = g.Sum(t => t.Commission) })
            .ToListAsync(ct);

        var payouts = await db.PayoutRequests.AsNoTracking()
            .Where(p => p.Status == PayoutStatus.Paid && p.PaidAt != null && p.PaidAt >= r.FromUtc && p.PaidAt < r.ToUtc)
            .GroupBy(p => p.PaidAt!.Value.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count(), A = g.Sum(p => p.Amount) })
            .ToListAsync(ct);

        var refunds = await db.Refunds.AsNoTracking()
            .Where(x => x.Status == "done" && x.DoneAt != null && x.DoneAt >= r.FromUtc && x.DoneAt < r.ToUtc)
            .GroupBy(x => x.DoneAt!.Value.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count(), A = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var charges = await db.Payments.AsNoTracking()
            .Where(p => p.Purpose == "charge" && p.Status == PaymentStatus.Paid && p.PaidAt != null && p.PaidAt >= r.FromUtc && p.PaidAt < r.ToUtc)
            .GroupBy(p => p.PaidAt!.Value.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count(), A = g.Sum(p => p.Amount) })
            .ToListAsync(ct);

        return View(new ReportFinanceVm
        {
            Range = r,
            Series = new TimeSeriesVm
            {
                Labels = b.Labels,
                FirstColumn = r.Monthly ? "ماه" : "روز",
                Series =
                [
                    new("کرایهٔ تسویه‌شده", b.Fill(settled, d => d.A), true),
                    new("کمیسیون بارگو", b.Fill(settled, d => d.B), true),
                    new("واریز به حمل‌کنندگان", b.Fill(payouts, d => d.A), true),
                    new("استرداد", b.Fill(refunds, d => d.A), true),
                    new("شارژ کیف پول", b.Fill(charges, d => d.A), true),
                ]
            },
            Fares = settled.Sum(d => d.A),
            Commission = settled.Sum(d => d.B),
            Payouts = payouts.Sum(d => d.A),
            Refunds = refunds.Sum(d => d.A),
            Charges = charges.Sum(d => d.A),
            SettledTrips = settled.Sum(d => d.N)
        });
    }

    // ------------------------------------------------------------------
    //  کاربران
    // ------------------------------------------------------------------

    public async Task<IActionResult> Users(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "گزارش کاربران";
        var r = Range(from, to, nameof(Users));
        var off = BackofficeLookup.TehranOffsetMinutes;
        var b = new ReportBuckets(r.FromUtc, r.ToUtc, r.Monthly);

        var drivers = await db.Drivers.AsNoTracking()
            .Where(d => d.CreatedAt >= r.FromUtc && d.CreatedAt < r.ToUtc)
            .GroupBy(d => d.CreatedAt.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count() }).ToListAsync(ct);
        var shippers = await db.Shippers.AsNoTracking()
            .Where(s => s.CreatedAt >= r.FromUtc && s.CreatedAt < r.ToUtc)
            .GroupBy(s => s.CreatedAt.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count() }).ToListAsync(ct);
        var companies = await db.Companies.AsNoTracking()
            .Where(c => c.CreatedAt >= r.FromUtc && c.CreatedAt < r.ToUtc)
            .GroupBy(c => c.CreatedAt.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count() }).ToListAsync(ct);

        return View(new ReportUsersVm
        {
            Range = r,
            Series = new TimeSeriesVm
            {
                Labels = b.Labels,
                FirstColumn = r.Monthly ? "ماه" : "روز",
                Series =
                [
                    new("رانندهٔ تازه", b.Fill(drivers, d => d.N)),
                    new("صاحب بار تازه", b.Fill(shippers, d => d.N)),
                    new("شرکت تازه", b.Fill(companies, d => d.N)),
                ]
            },
            NewDrivers = drivers.Sum(d => d.N),
            NewShippers = shippers.Sum(d => d.N),
            NewCompanies = companies.Sum(d => d.N),
            DriverStatus = await db.Drivers.AsNoTracking().GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct),
            CompanyStatus = await db.Companies.AsNoTracking().GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct),
            ShipperVerify = await db.Shippers.AsNoTracking().GroupBy(s => s.VerifyStatus ?? "none").Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct)
        });
    }

    // ------------------------------------------------------------------
    //  بارها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Loads(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "گزارش بارها";
        var r = Range(from, to, nameof(Loads));
        var off = BackofficeLookup.TehranOffsetMinutes;
        var b = new ReportBuckets(r.FromUtc, r.ToUtc, r.Monthly);

        // Load زمانِ لغو/انقضا ندارد؛ پس همهٔ ستون‌ها بر پایهٔ تاریخ ثبت بار است و
        // «قطعی‌شده» یعنی باری که در این بازه ثبت شده و به سفر رسیده.
        var loads = await db.Loads.AsNoTracking()
            .Where(l => l.CreatedAt >= r.FromUtc && l.CreatedAt < r.ToUtc)
            .GroupBy(l => l.CreatedAt.AddMinutes(off).Date)
            .Select(g => new DayAgg
            {
                Day = g.Key,
                N = g.Count(),
                A = g.Count(l => l.Status == LoadStatus.Booked || l.Status == LoadStatus.Completed),
                B = g.Count(l => l.Status == LoadStatus.Cancelled),
                C = g.Count(l => l.Status == LoadStatus.Expired)
            })
            .ToListAsync(ct);

        var byCargo = await db.Loads.AsNoTracking()
            .Where(l => l.CreatedAt >= r.FromUtc && l.CreatedAt < r.ToUtc)
            .GroupBy(l => l.CargoType)
            .Select(g => new { Cargo = g.Key, N = g.Count(), Booked = g.Count(l => l.Status == LoadStatus.Booked || l.Status == LoadStatus.Completed), W = g.Sum(l => l.WeightTon) })
            .OrderByDescending(x => x.N).ThenBy(x => x.Cargo)
            .Take(TopN)
            .ToListAsync(ct);

        return View(new ReportLoadsVm
        {
            Range = r,
            Series = new TimeSeriesVm
            {
                Labels = b.Labels,
                FirstColumn = r.Monthly ? "ماه" : "روز",
                Series =
                [
                    new("ثبت‌شده", b.Fill(loads, d => d.N)),
                    new("قطعی‌شده", b.Fill(loads, d => d.A)),
                    new("لغوشده", b.Fill(loads, d => d.B)),
                    new("منقضی", b.Fill(loads, d => d.C)),
                ]
            },
            Created = loads.Sum(d => d.N),
            Booked = (int)loads.Sum(d => d.A),
            Cancelled = (int)loads.Sum(d => d.B),
            Expired = (int)loads.Sum(d => d.C),
            ByCargo = byCargo.Select(c => new CargoRowVm(string.IsNullOrWhiteSpace(c.Cargo) ? "نامشخص" : c.Cargo, c.N, c.Booked, c.W)).ToList()
        });
    }

    // ------------------------------------------------------------------
    //  سفرها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Trips(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "گزارش سفرها";
        var r = Range(from, to, nameof(Trips));
        var off = BackofficeLookup.TehranOffsetMinutes;
        var b = new ReportBuckets(r.FromUtc, r.ToUtc, r.Monthly);
        var all = db.Trips.AsNoTracking();

        var created = await all.Where(t => t.CreatedAt >= r.FromUtc && t.CreatedAt < r.ToUtc)
            .GroupBy(t => t.CreatedAt.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count(), A = g.Sum(t => t.Fare) }).ToListAsync(ct);
        var delivered = await all.Where(t => t.DeliveredAt != null && t.DeliveredAt >= r.FromUtc && t.DeliveredAt < r.ToUtc)
            .GroupBy(t => t.DeliveredAt!.Value.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count() }).ToListAsync(ct);
        var cancelled = await all.Where(t => t.CancelledAt != null && t.CancelledAt >= r.FromUtc && t.CancelledAt < r.ToUtc)
            .GroupBy(t => t.CancelledAt!.Value.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count() }).ToListAsync(ct);

        var deliveredQ = all.Where(t => t.DeliveredAt != null && t.DeliveredAt >= r.FromUtc && t.DeliveredAt < r.ToUtc);
        var avgMinutes = await deliveredQ.Where(t => t.LoadedAt != null)
            .Select(t => (double?)EF.Functions.DateDiffMinute(t.LoadedAt, t.DeliveredAt)).AverageAsync(ct);
        var avgKm = await deliveredQ.Where(t => t.TravelledKm > 0).Select(t => (double?)t.TravelledKm).AverageAsync(ct);
        var createdN = created.Sum(d => d.N);

        return View(new ReportTripsVm
        {
            Range = r,
            Series = new TimeSeriesVm
            {
                Labels = b.Labels,
                FirstColumn = r.Monthly ? "ماه" : "روز",
                Series =
                [
                    new("ساخته‌شده", b.Fill(created, d => d.N)),
                    new("تحویل‌شده", b.Fill(delivered, d => d.N)),
                    new("لغوشده", b.Fill(cancelled, d => d.N)),
                ]
            },
            Created = createdN,
            Delivered = delivered.Sum(d => d.N),
            Cancelled = cancelled.Sum(d => d.N),
            AvgDeliveryMinutes = avgMinutes,
            AvgKm = avgKm,
            AvgFare = createdN == 0 ? 0 : created.Sum(d => d.A) / createdN,
            ByStatus = await all.Where(t => t.CreatedAt >= r.FromUtc && t.CreatedAt < r.ToUtc)
                .GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct)
        });
    }

    // ------------------------------------------------------------------
    //  رانندگان و شرکت‌ها
    // ------------------------------------------------------------------

    private static string NormBy(string? by) => by == "revenue" ? "revenue" : "trips";

    private static IEnumerable<RankAgg> Order(IEnumerable<RankAgg> rows, string by) =>
        by == "revenue" ? rows.OrderByDescending(x => x.Income).ThenByDescending(x => x.Trips) : rows.OrderByDescending(x => x.Trips).ThenByDescending(x => x.Income);

    public async Task<IActionResult> Drivers(DateTime? from, DateTime? to, string? by, CancellationToken ct)
    {
        ViewData["Title"] = "گزارش رانندگان";
        var r = Range(from, to, nameof(Drivers));
        by = NormBy(by);

        // سفرهای تحویل‌شده در بازه؛ درآمد راننده: سهم حمل‌کننده (مستقل) یا سهم رانندهٔ شرکت
        var agg = await db.Trips.AsNoTracking()
            .Where(t => t.DriverId != null && TripStatus.Done.Contains(t.Status) && t.DeliveredAt >= r.FromUtc && t.DeliveredAt < r.ToUtc)
            .GroupBy(t => t.DriverId!.Value)
            .Select(g => new RankAgg
            {
                Id = g.Key,
                Trips = g.Count(),
                Fare = g.Sum(t => t.Fare),
                Commission = g.Sum(t => t.Commission),
                Income = g.Sum(t => t.CarrierKind == CarrierKind.Driver ? t.CarrierShare : (t.DriverShare ?? 0))
            })
            .ToListAsync(ct);

        var top = Order(agg, by).Take(TopN).ToList();
        var ids = top.Select(x => x.Id).ToList();
        var people = await db.Drivers.AsNoTracking().Where(d => ids.Contains(d.DriverId))
            .Select(d => new { d.DriverId, d.FirstName, d.LastName, d.Mobile, d.RatingAvg, d.RatingCount, Company = d.Company!.Name, Cancelled = db.Trips.Count(t => t.DriverId == d.DriverId && t.Status == TripStatus.Cancelled && t.CancelledAt >= r.FromUtc && t.CancelledAt < r.ToUtc) })
            .ToDictionaryAsync(d => d.DriverId, ct);

        var rangeRatings = await db.Ratings.AsNoTracking()
            .Where(x => x.ToKind == OwnerKind.Driver && x.CreatedAt >= r.FromUtc && x.CreatedAt < r.ToUtc)
            .GroupBy(_ => 1).Select(g => new { N = g.Count(), Avg = g.Average(x => (double)x.Score) }).FirstOrDefaultAsync(ct);

        return View("Rank", new ReportRankVm
        {
            Range = r,
            By = by,
            Active = agg.Count,
            AvgRating = await db.Drivers.AsNoTracking().Where(d => d.RatingCount > 0).Select(d => (double?)d.RatingAvg).AverageAsync(ct),
            RangeRatingAvg = rangeRatings?.Avg,
            RangeRatingCount = rangeRatings?.N ?? 0,
            Rows = top.Select(x =>
            {
                people.TryGetValue(x.Id, out var p);
                return new RankRowVm
                {
                    Id = x.Id,
                    Name = p is null ? $"راننده #{Fa.N(x.Id)}" : $"{p.FirstName} {p.LastName}".Trim(),
                    Sub = p is null ? "" : string.IsNullOrEmpty(p.Company) ? p.Mobile : $"{p.Mobile} · {p.Company}",
                    Trips = x.Trips, Fare = x.Fare, Income = x.Income, Commission = x.Commission,
                    Rating = p?.RatingAvg ?? 0, RatingCount = p?.RatingCount ?? 0, Extra = p?.Cancelled ?? 0
                };
            }).ToList()
        });
    }

    public async Task<IActionResult> Companies(DateTime? from, DateTime? to, string? by, CancellationToken ct)
    {
        ViewData["Title"] = "گزارش شرکت‌ها";
        var r = Range(from, to, nameof(Companies));
        by = NormBy(by);

        var agg = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId != null && TripStatus.Done.Contains(t.Status) && t.DeliveredAt >= r.FromUtc && t.DeliveredAt < r.ToUtc)
            .GroupBy(t => t.CompanyId!.Value)
            .Select(g => new RankAgg { Id = g.Key, Trips = g.Count(), Fare = g.Sum(t => t.Fare), Commission = g.Sum(t => t.Commission), Income = g.Sum(t => t.CarrierShare) })
            .ToListAsync(ct);

        var top = Order(agg, by).Take(TopN).ToList();
        var ids = top.Select(x => x.Id).ToList();
        var companies = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.CompanyId))
            .Select(c => new { c.CompanyId, c.Name, c.Mobile, City = c.City!.Name, c.RatingAvg, c.RatingCount, Drivers = db.Drivers.Count(d => d.CompanyId == c.CompanyId) })
            .ToDictionaryAsync(c => c.CompanyId, ct);

        var rangeRatings = await db.Ratings.AsNoTracking()
            .Where(x => x.ToKind == OwnerKind.Company && x.CreatedAt >= r.FromUtc && x.CreatedAt < r.ToUtc)
            .GroupBy(_ => 1).Select(g => new { N = g.Count(), Avg = g.Average(x => (double)x.Score) }).FirstOrDefaultAsync(ct);

        return View("Rank", new ReportRankVm
        {
            Range = r,
            By = by,
            Active = agg.Count,
            AvgRating = await db.Companies.AsNoTracking().Where(c => c.RatingCount > 0).Select(c => (double?)c.RatingAvg).AverageAsync(ct),
            RangeRatingAvg = rangeRatings?.Avg,
            RangeRatingCount = rangeRatings?.N ?? 0,
            Rows = top.Select(x =>
            {
                companies.TryGetValue(x.Id, out var c);
                return new RankRowVm
                {
                    Id = x.Id,
                    Name = c?.Name ?? $"شرکت #{Fa.N(x.Id)}",
                    Sub = c is null ? "" : string.IsNullOrEmpty(c.City) ? c.Mobile : $"{c.Mobile} · {c.City}",
                    Trips = x.Trips, Fare = x.Fare, Income = x.Income, Commission = x.Commission,
                    Rating = c?.RatingAvg ?? 0, RatingCount = c?.RatingCount ?? 0, Extra = c?.Drivers ?? 0
                };
            }).ToList()
        });
    }

    // ------------------------------------------------------------------
    //  استان و شهر
    // ------------------------------------------------------------------

    public async Task<IActionResult> Regions(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "گزارش استان و شهر";
        var r = Range(from, to, nameof(Regions));
        var loads = db.Loads.AsNoTracking().Where(l => l.CreatedAt >= r.FromUtc && l.CreatedAt < r.ToUtc);

        async Task<List<RegionRowVm>> RowsAsync(IQueryable<IGrouping<string, Load>> grouped, int? take)
        {
            var q = grouped.Select(g => new { Name = g.Key, N = g.Count(), W = g.Sum(l => l.WeightTon) })
                .OrderByDescending(x => x.N).ThenBy(x => x.Name);
            var list = await (take is int n ? q.Take(n) : q).ToListAsync(ct);
            return list.Select(x => new RegionRowVm(x.Name, x.N, x.W)).ToList();
        }

        return View(new ReportRegionsVm
        {
            Range = r,
            Total = await loads.CountAsync(ct),
            Origins = await RowsAsync(loads.GroupBy(l => l.OriginCity!.Province!.Name), null),
            Dests = await RowsAsync(loads.GroupBy(l => l.DestCity!.Province!.Name), null),
            TopCities = await RowsAsync(loads.GroupBy(l => l.OriginCity!.Name + " (" + l.OriginCity.Province!.Name + ")"), TopN)
        });
    }

    // ------------------------------------------------------------------
    //  مسیرهای پرتردد
    // ------------------------------------------------------------------

    public async Task<IActionResult> Routes(DateTime? from, DateTime? to, CancellationToken ct)
    {
        ViewData["Title"] = "مسیرهای پرتردد";
        var r = Range(from, to, nameof(Routes));

        var pairs = await db.Trips.AsNoTracking()
            .Where(t => t.CreatedAt >= r.FromUtc && t.CreatedAt < r.ToUtc)
            .GroupBy(t => new { t.Load!.OriginCityId, t.Load.DestCityId, From = t.Load.OriginCity!.Name, To = t.Load.DestCity!.Name })
            .Select(g => new
            {
                g.Key.OriginCityId, g.Key.DestCityId, g.Key.From, g.Key.To,
                N = g.Count(),
                Fare = g.Average(t => (double)t.Fare),
                Km = g.Average(t => t.Load!.DistanceKm)
            })
            .OrderByDescending(x => x.N).ThenBy(x => x.From)
            .Take(TopN)
            .ToListAsync(ct);

        var cityIds = pairs.SelectMany(p => new[] { p.OriginCityId, p.DestCityId }).Distinct().ToList();
        var lanes = cityIds.Count == 0 ? [] : await db.RouteLanes.AsNoTracking()
            .Where(l => cityIds.Contains(l.OriginCityId) && cityIds.Contains(l.DestCityId))
            .Select(l => new { l.OriginCityId, l.DestCityId, l.DistanceKm, l.BaseRatePerTon })
            .ToListAsync(ct);
        var laneMap = lanes.ToDictionary(l => (l.OriginCityId, l.DestCityId));

        return View(new ReportRoutesVm
        {
            Range = r,
            Rows = pairs.Select(p =>
            {
                laneMap.TryGetValue((p.OriginCityId, p.DestCityId), out var lane);
                return new RouteRowVm
                {
                    From = p.From, To = p.To, Trips = p.N,
                    AvgFare = (long)Math.Round(p.Fare),
                    AvgKm = p.Km,
                    LaneKm = lane?.DistanceKm,
                    LaneRatePerTon = lane?.BaseRatePerTon
                };
            }).ToList()
        });
    }
}

/// <summary>تجمیعِ سفرهای یک راننده/شرکت در بازهٔ گزارش — خروجی مستقیم SQL.</summary>
internal sealed class RankAgg
{
    public int Id { get; init; }
    public int Trips { get; init; }
    public long Fare { get; init; }
    public long Commission { get; init; }
    public long Income { get; init; }
}
