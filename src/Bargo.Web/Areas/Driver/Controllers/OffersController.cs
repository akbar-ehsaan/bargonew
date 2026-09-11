using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// «پیشنهادهای من». تب «ردشده» پیشنهادهای پس‌گرفته و منقضی را هم نشان می‌دهد: از دید
/// راننده هر سه یعنی «این بار به من نرسید».
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class OffersController(BargoDbContext db, CurrentUser me, TripFlow flow, DriverReadiness readiness) : Controller
{
    public async Task<IActionResult> Index(string? status, int page = 1, CancellationToken ct = default)
    {
        status = status is "pending" or "accepted" or "rejected" ? status : null;
        ViewData["Title"] = status switch
        {
            "pending" => "پیشنهادهای در انتظار",
            "accepted" => "پیشنهادهای پذیرفته‌شده",
            "rejected" => "پیشنهادهای ردشده",
            _ => "پیشنهادهای من"
        };

        var meId = me.Id;
        var mine = db.Offers.AsNoTracking().Where(o => o.DriverId == meId);
        var counts = await mine.GroupBy(o => o.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        int Count(params string[] statuses) => counts.Where(x => statuses.Contains(x.Key)).Sum(x => x.N);

        var q = status switch
        {
            "pending" => mine.Where(o => o.Status == OfferStatus.Pending),
            "accepted" => mine.Where(o => o.Status == OfferStatus.Accepted),
            "rejected" => mine.Where(o => o.Status == OfferStatus.Rejected || o.Status == OfferStatus.Withdrawn || o.Status == OfferStatus.Expired),
            _ => mine
        };

        var rows = q.OrderByDescending(o => o.OfferId).Select(o => new OfferRowVm
        {
            OfferId = o.OfferId,
            LoadId = o.LoadId,
            LoadCode = o.Load!.Code,
            Title = o.Load!.Title,
            From = o.Load!.OriginCity!.Name,
            To = o.Load!.DestCity!.Name,
            PriceMode = o.Load!.PriceMode,
            LoadPrice = o.Load!.Price,
            LoadStatus = o.Load!.Status,
            Amount = o.Amount,
            EtaHours = o.EtaHours,
            Note = o.Note,
            Status = o.Status,
            Plate = o.Vehicle!.PlateNo,
            CreatedAt = o.CreatedAt,
            RespondedAt = o.RespondedAt,
            TripId = db.Trips.Where(t => t.OfferId == o.OfferId && t.DriverId == meId).Select(t => (int?)t.TripId).FirstOrDefault()
        });

        var vm = new OffersVm
        {
            Status = status,
            Page = await PageVm<OfferRowVm>.FromAsync(rows, page, PageLink.For(Request), ct: ct),
            CountAll = counts.Sum(x => x.N),
            CountPending = Count(OfferStatus.Pending),
            CountAccepted = Count(OfferStatus.Accepted),
            CountRejected = Count(OfferStatus.Rejected, OfferStatus.Withdrawn, OfferStatus.Expired)
        };
        return View(vm);
    }

    /// <summary>«ثبت پیشنهاد قیمت» — فهرست فشردهٔ بازار برای پیشنهاد سریع؛ خودِ فرم در صفحهٔ بار است.</summary>
    public async Task<IActionResult> Create(int? originProvinceId, int? vehicleTypeId, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "ثبت پیشنهاد قیمت";
        var q = db.Loads.AsNoTracking().Market();
        if (originProvinceId is int op) q = q.Where(l => l.OriginCity!.ProvinceId == op);
        if (vehicleTypeId is int vt) q = q.Where(l => l.VehicleTypeId == vt);

        var issues = await readiness.CheckAsync(me.Id, null, ct);
        var vm = new OfferCreateVm
        {
            OriginProvinceId = originProvinceId,
            VehicleTypeId = vehicleTypeId,
            Page = await PageVm<LoadCardVm>.FromAsync(q.OrderBy(l => l.LoadingFrom).ToCards(db, me.Id), page, PageLink.For(Request), ct: ct),
            Provinces = await db.ProvinceListAsync(ct),
            VehicleTypes = await db.VehicleTypeListAsync(ct),
            Blocking = issues.Where(i => i.Blocking).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Withdraw(int id, string? returnUrl, CancellationToken ct)
    {
        if (!await db.Offers.AnyAsync(o => o.OfferId == id && o.DriverId == me.Id, ct)) return NotFound();
        try
        {
            await flow.WithdrawOfferAsync(id, me.ToActor(), ct);
            TempData["ok"] = "پیشنهاد پس گرفته شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Index), new { status = "pending" });
    }
}
