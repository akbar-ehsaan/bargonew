using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// داشبورد صاحب بار — «کارِ امروزِ من چیست؟». به‌جای شمارنده‌های کلی، هر چیزی که
/// منتظر تصمیمِ صاحب بار است (پیشنهادِ بی‌پاسخ، کرایهٔ پرداخت‌نشده، سفرِ تحویل‌شدهٔ
/// بی‌امتیاز، کد تحویل) با پیوند مستقیم به همان اقدام آمده است.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
[AllowUnapproved]
public class DashboardController(BargoDbContext db, CurrentUser me) : Controller
{
    public async Task<IActionResult> Index(string? gate, CancellationToken ct)
    {
        ViewData["Title"] = "داشبورد";
        var actor = me.ToActor();

        var acc = await db.Shippers.AsNoTracking().Where(s => s.ShipperId == me.Id)
            .Select(s => new { s.FullName, s.BusinessName, s.Kind, s.Status, s.StatusReason, s.VerifyStatus, s.WalletBalance })
            .FirstOrDefaultAsync(ct);
        if (acc is null) return NotFound();

        var loads = db.Loads.AsNoTracking().OwnedBy(actor);
        var trips = db.Trips.AsNoTracking().VisibleTo(actor);

        var openLoads = await loads.CountAsync(l => LoadStatus.Market.Contains(l.Status), ct);
        var drafts = await loads.CountAsync(l => l.Status == LoadStatus.Draft, ct);
        var pendingOffers = await db.Offers.AsNoTracking()
            .CountAsync(o => o.Load!.ShipperId == me.Id && o.Status == OfferStatus.Pending && LoadStatus.Market.Contains(o.Load.Status), ct);
        var live = await trips.CountAsync(t => TripStatus.Live.Contains(t.Status), ct);

        var vm = new ShipperDashboardVm
        {
            Role = Roles.Shipper,
            Name = acc.Kind == "business" && !string.IsNullOrWhiteSpace(acc.BusinessName) ? acc.BusinessName! : acc.FullName,
            AccountState = acc.Status,
            StatusReason = acc.StatusReason,
            VerifyStatus = acc.VerifyStatus,
            Gate = gate,
            WalletBalance = acc.WalletBalance
        };

        vm.Stats.Add(new Stat { Label = "بارهای باز", Value = Fa.N(openLoads), Icon = "bi-box-seam", Kind = "p", Href = "/Shipper/Loads?status=waiting" });
        vm.Stats.Add(new Stat { Label = "پیشنهادهای در انتظار تصمیم", Value = Fa.N(pendingOffers), Icon = "bi-tags", Kind = pendingOffers > 0 ? "w" : "i", Href = "/Shipper/Offers?pending=1" });
        vm.Stats.Add(new Stat { Label = "سفرهای در حال حمل", Value = Fa.N(live), Icon = "bi-truck", Kind = live > 0 ? "s" : "i", Href = "/Shipper/Tracking" });
        vm.Stats.Add(new Stat { Label = "موجودی کیف پول", Value = Fa.N(acc.WalletBalance / 10), Unit = "تومان", Icon = "bi-wallet2", Kind = "t", Href = "/Shipper/Finance" });

        // ---- نیاز به اقدام ----
        var withOffers = await loads
            .Where(l => LoadStatus.Market.Contains(l.Status) && l.Offers.Any(o => o.Status == OfferStatus.Pending))
            .OrderByDescending(l => l.Offers.Where(o => o.Status == OfferStatus.Pending).Max(o => o.CreatedAt))
            .Take(6)
            .Select(l => new
            {
                l.LoadId, l.Code, From = l.OriginCity!.Name, To = l.DestCity!.Name,
                Count = l.Offers.Count(o => o.Status == OfferStatus.Pending),
                Lowest = l.Offers.Where(o => o.Status == OfferStatus.Pending).Min(o => o.Amount)
            })
            .ToListAsync(ct);
        foreach (var l in withOffers)
            vm.Todos.Add(new TodoItem(
                $"{Fa.N(l.Count)} پیشنهاد برای بار {l.Code} ({l.From} ← {l.To}) — کمترین {Fa.Toman(l.Lowest)}",
                $"/Shipper/Offers/Compare/{l.LoadId}", "bi-tags"));

        var unloaded = await trips.Where(t => t.Status == TripStatus.Unloaded)
            .Select(t => new { t.TripId, t.Code }).ToListAsync(ct);
        foreach (var t in unloaded)
            vm.Todos.Add(new TodoItem($"بار سفر {t.Code} تخلیه شد — پس از بررسی سلامت بار، کد تحویل را به راننده بدهید",
                $"/Shipper/Orders/Detail/{t.TripId}", "bi-shield-lock", "inf"));

        var unpaid = await trips.Where(t => !t.IsPaid && t.PayMethod != PayMethods.Cash && t.Status != TripStatus.Cancelled)
            .OrderBy(t => t.TripId).Select(t => new { t.TripId, t.Code, Total = t.Fare + t.LoadingFee + t.UnloadingFee + t.WaybillFee + t.Vat }).Take(6).ToListAsync(ct);
        foreach (var t in unpaid)
            vm.Todos.Add(new TodoItem($"کرایه و هزینه‌های سفر {t.Code} ({Fa.Toman(t.Total)}) پرداخت نشده است",
                "/Shipper/Finance/Pay", "bi-credit-card", "no"));

        var unrated = await trips
            .Where(t => TripStatus.Done.Contains(t.Status) &&
                        !db.Ratings.Any(r => r.TripId == t.TripId && r.FromKind == Roles.Shipper && r.FromId == me.Id))
            .OrderByDescending(t => t.DeliveredAt).Select(t => new { t.TripId, t.Code }).Take(4).ToListAsync(ct);
        foreach (var t in unrated)
            vm.Todos.Add(new TodoItem($"سفر {t.Code} تحویل شد — به حمل‌کننده امتیاز بدهید", "/Shipper/Drivers/Rate", "bi-star", "ok"));

        if (drafts > 0)
            vm.Todos.Add(new TodoItem($"{Fa.N(drafts)} بار پیش‌نویس هنوز منتشر نشده است", "/Shipper/Loads?status=waiting", "bi-pencil-square", "mut"));

        // ---- سفرهای جاری و آخرین بارها ----
        vm.ActiveTrips = await trips
            .Where(t => TripStatus.Live.Contains(t.Status) || TripStatus.Upcoming.Contains(t.Status))
            .OrderByDescending(t => t.LastPointAt ?? t.CreatedAt)
            .Take(8)
            .Select(ShipperTripRow.FromTrip)
            .ToListAsync(ct);

        vm.RecentLoads = await loads.OrderByDescending(l => l.LoadId).Take(5)
            .Select(ShipperLoadRow.FromLoad).ToListAsync(ct);

        return View(vm);
    }
}
