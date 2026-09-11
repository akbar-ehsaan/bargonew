using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// امتیاز و سوابق عملکرد راننده — فقط خواندنی. امتیاز دادن به صاحب بار از صفحهٔ
/// خود سفر (Trips/Rate) انجام می‌شود تا همیشه به یک سفر مشخص گره بخورد.
///
/// میانگین و شمار امتیاز از خودِ جدول Ratings حساب می‌شود نه از کشِ Driver.RatingAvg،
/// تا این صفحه با فهرست نظرات زیرش هرگز ناسازگار نباشد.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class RatingsController(BargoDbContext db, CurrentUser me) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "امتیاز من";
        var toMe = ToMe();
        var dist = await DistAsync(toMe, ct);
        var count = dist.Sum();

        var vm = new RatingsVm
        {
            Count = count,
            Avg = count == 0 ? 0 : await toMe.AverageAsync(r => (double)r.Score, ct),
            Dist = dist,
            Recent = await toMe.OrderByDescending(r => r.RatingId).Take(5).ToReviewRows(db).ToListAsync(ct),
            WithComment = await toMe.CountAsync(r => r.Comment != null && r.Comment != "", ct),
            GivenByMe = await db.Ratings.AsNoTracking().CountAsync(r => r.FromKind == Roles.Driver && r.FromId == me.Id, ct),
            AwaitingMyRating = await db.Trips.AsNoTracking()
                .CountAsync(t => t.DriverId == me.Id && t.Load!.ShipperId != null && TripStatus.Done.Contains(t.Status) &&
                                 !db.Ratings.Any(r => r.TripId == t.TripId && r.FromKind == Roles.Driver && r.FromId == me.Id), ct)
        };
        return View(vm);
    }

    public async Task<IActionResult> Reviews(int? score, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "نظرات صاحبان بار";
        var toMe = ToMe();
        var dist = await DistAsync(toMe, ct);
        if (score is < 1 or > 5) score = null;

        var q = score is int s ? toMe.Where(r => r.Score == s) : toMe;
        return View(new ReviewsVm
        {
            Score = score,
            Count = dist.Sum(),
            Dist = dist,
            Page = await PageVm<ReviewRowVm>.FromAsync(q.OrderByDescending(r => r.RatingId).ToReviewRows(db), page, PageLink.For(Request), 20, ct)
        });
    }

    public async Task<IActionResult> Performance(CancellationToken ct)
    {
        ViewData["Title"] = "سوابق عملکرد";
        var driver = await db.Drivers.AsNoTracking().Where(d => d.DriverId == me.Id)
            .Select(d => new { d.CreatedAt, d.RatingAvg, d.RatingCount }).FirstOrDefaultAsync(ct);
        if (driver is null) return NotFound();

        var trips = db.Trips.AsNoTracking().Where(t => t.DriverId == me.Id);
        var done = trips.Where(t => TripStatus.Done.Contains(t.Status));
        var income = db.WalletTransactions.AsNoTracking().Of(me.Owner)
            .Where(t => (t.Kind == WalletTxnKind.FareIncome || t.Kind == WalletTxnKind.DriverShare) && t.Amount > 0);

        var months = DriverMonth.Last(6);
        var from = months[^1].FromUtc;

        // سه پرس‌وجوی خام برای شش ماه، گروه‌بندی در حافظه — مرز ماه شمسی در SQL ساخته نمی‌شود
        var tripRows = await trips
            .Where(t => (t.DeliveredAt != null && t.DeliveredAt >= from) || (t.CancelledAt != null && t.CancelledAt >= from))
            .Select(t => new { t.Status, t.DeliveredAt, t.CancelledAt, t.TravelledKm }).ToListAsync(ct);
        var incomeRows = await income.Where(t => t.CreatedAt >= from).Select(t => new { t.CreatedAt, t.Amount }).ToListAsync(ct);
        var ratingRows = await ToMe().Where(r => r.CreatedAt >= from).Select(r => new { r.CreatedAt, r.Score }).ToListAsync(ct);

        var rows = months.Select(m =>
        {
            var delivered = tripRows.Where(t => TripStatus.Done.Contains(t.Status) && t.DeliveredAt >= m.FromUtc && t.DeliveredAt < m.ToUtc).ToList();
            var cancelled = tripRows.Count(t => t.Status == TripStatus.Cancelled && t.CancelledAt >= m.FromUtc && t.CancelledAt < m.ToUtc);
            var ratings = ratingRows.Where(r => r.CreatedAt >= m.FromUtc && r.CreatedAt < m.ToUtc).ToList();
            return new PerfMonthRow(
                m.Label,
                delivered.Count,
                cancelled,
                Math.Round(delivered.Sum(t => t.TravelledKm)),
                incomeRows.Where(t => t.CreatedAt >= m.FromUtc && t.CreatedAt < m.ToUtc).Sum(t => t.Amount),
                ratings.Count == 0 ? null : ratings.Average(r => r.Score),
                ratings.Count);
        }).ToList();

        var vm = new PerformanceVm
        {
            Done = await done.CountAsync(ct),
            Cancelled = await trips.CountAsync(t => t.Status == TripStatus.Cancelled, ct),
            Live = await trips.CountAsync(t => TripStatus.Live.Contains(t.Status), ct),
            Km = await done.SumAsync(t => t.TravelledKm, ct),
            Income = await income.SumAsync(t => (long?)t.Amount, ct) ?? 0,
            OffersTotal = await db.Offers.AsNoTracking().CountAsync(o => o.DriverId == me.Id, ct),
            OffersAccepted = await db.Offers.AsNoTracking().CountAsync(o => o.DriverId == me.Id && o.Status == OfferStatus.Accepted, ct),
            RatingAvg = driver.RatingAvg,
            RatingCount = driver.RatingCount,
            Complaints = await db.Complaints.AsNoTracking().CountAsync(c => c.AgainstKind == Roles.Driver && c.AgainstId == me.Id, ct),
            Months = rows,
            MemberSince = driver.CreatedAt
        };
        return View(vm);
    }

    // ------------------------------------------------------------------

    private IQueryable<Rating> ToMe() => db.Ratings.AsNoTracking().Where(r => r.ToKind == Roles.Driver && r.ToId == me.Id);

    /// <summary>شمار امتیازهای ۱ تا ۵؛ اندیس ۰ بی‌استفاده.</summary>
    private static async Task<int[]> DistAsync(IQueryable<Rating> q, CancellationToken ct)
    {
        var dist = new int[6];
        foreach (var g in await q.GroupBy(r => r.Score).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            if (g.Key is >= 1 and <= 5) dist[g.Key] = g.N;
        return dist;
    }
}

internal static class RatingQueries
{
    public static IQueryable<ReviewRowVm> ToReviewRows(this IQueryable<Rating> q, BargoDbContext db) =>
        q.Select(r => new ReviewRowVm
        {
            RatingId = r.RatingId,
            Score = r.Score,
            Comment = r.Comment,
            CreatedAt = r.CreatedAt,
            TripId = r.TripId,
            TripCode = r.Trip!.Code,
            From = r.Trip!.Load!.OriginCity!.Name,
            To = r.Trip!.Load!.DestCity!.Name,
            FromKind = r.FromKind,
            FromName = r.FromKind == Roles.Shipper
                ? db.Shippers.Where(s => s.ShipperId == r.FromId)
                    .Select(s => s.Kind == "business" && s.BusinessName != null && s.BusinessName != "" ? s.BusinessName : s.FullName)
                    .FirstOrDefault()
                : r.FromKind == Roles.Company
                    ? db.Companies.Where(c => c.CompanyId == r.FromId).Select(c => c.Name).FirstOrDefault()
                    : null
        });
}
