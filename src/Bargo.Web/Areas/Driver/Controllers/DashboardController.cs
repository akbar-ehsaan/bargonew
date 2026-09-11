using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// داشبورد راننده — «الان باید چه کنم؟». بالای صفحه هر چیزی که جلوی گرفتن بار را
/// گرفته (حساب، مدرک، خودرو)، بعد سفر جاری با دکمهٔ گام بعد، بعد نزدیک‌ترین بارها.
///
/// [AllowUnapproved]: دروازهٔ تأیید، رانندهٔ تأییدنشده را به همین صفحه برمی‌گرداند
/// (?gate=pending)؛ پس همین صفحه باید دقیق بگوید چه چیزی مانده و کجا درستش کند.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
[AllowUnapproved]
public class DashboardController(BargoDbContext db, CurrentUser me, DriverReadiness readiness, TripFlow flow) : Controller
{
    public async Task<IActionResult> Index(string? gate, CancellationToken ct)
    {
        ViewData["Title"] = "داشبورد راننده";
        var driver = await db.Drivers.AsNoTracking().Include(x => x.City).Include(x => x.Company)
            .FirstOrDefaultAsync(x => x.DriverId == me.Id, ct);
        if (driver is null) return NotFound();

        var vm = new DriverDashboardVm { Driver = driver, Gate = gate };
        vm.Issues = await readiness.CheckAsync(me.Id, null, ct);

        // ---- سفر جاری ----
        if (await db.CurrentTripIdAsync(me.Id, ct) is int tripId)
        {
            vm.CurrentTrip = await flow.FindForAsync(tripId, me.ToActor(), ct);
            if (vm.CurrentTrip is not null) vm.CurrentNext = TripFlow.NextSteps(vm.CurrentTrip.Status, Roles.Driver);
        }

        // ---- شمارنده‌ها ----
        var pendingOffers = await db.Offers.CountAsync(o => o.DriverId == me.Id && o.Status == OfferStatus.Pending, ct);
        var monthStart = Fa.MonthStartUtc;
        // درآمد = سهم کرایهٔ رانندهٔ مستقل + سهمی که شرکت به رانندهٔ عضوش پرداخته
        var monthIncome = await db.WalletTransactions.AsNoTracking().Of((OwnerKind.Driver, me.Id))
            .Where(t => (t.Kind == WalletTxnKind.FareIncome || t.Kind == WalletTxnKind.DriverShare) && t.Amount > 0 && t.CreatedAt >= monthStart)
            .SumAsync(t => (long?)t.Amount, ct) ?? 0;

        vm.Stats.Add(new Stat
        {
            Label = "سفر جاری",
            Value = vm.CurrentTrip is null ? "ندارید" : TripStatus.Label(vm.CurrentTrip.Status),
            Unit = vm.CurrentTrip?.Code,
            Icon = "bi-truck", Kind = vm.CurrentTrip is null ? "i" : "p", Href = "/Driver/Trips/Current"
        });
        vm.Stats.Add(new Stat
        {
            Label = "پیشنهادهای در انتظار", Value = Fa.N(pendingOffers), Unit = "پیشنهاد",
            Icon = "bi-hourglass-split", Kind = pendingOffers > 0 ? "w" : "i", Href = "/Driver/Offers?status=pending"
        });
        vm.Stats.Add(new Stat
        {
            Label = "موجودی کیف پول", Value = Fa.N(driver.WalletBalance / 10), Unit = "تومان",
            Icon = "bi-wallet2", Kind = "s", Href = "/Driver/Finance"
        });
        vm.Stats.Add(new Stat
        {
            Label = "درآمد این ماه", Value = Fa.N(monthIncome / 10), Unit = "تومان",
            Icon = "bi-graph-up-arrow", Kind = "t", Href = "/Driver/Finance/Earnings"
        });
        vm.Stats.Add(new Stat
        {
            Label = "امتیاز من",
            Value = driver.RatingCount == 0 ? "—" : Fa.N(driver.RatingAvg, 1),
            Unit = driver.RatingCount == 0 ? "بدون نظر" : $"از {Fa.N(driver.RatingCount)} نظر",
            Icon = "bi-star-half", Kind = "p", Href = "/Driver/Ratings"
        });

        // ---- کارهای در انتظار ----
        var assigned = await db.Trips.CountAsync(t => t.DriverId == me.Id && t.Status == TripStatus.Assigned, ct);
        if (assigned > 0)
            vm.Todos.Add(new TodoItem($"{Fa.N(assigned)} مأموریت از طرف شرکت در انتظار قبول شماست", "/Driver/Trips?tab=upcoming", "bi-person-check"));

        if (vm.CurrentTrip is { } ct0)
        {
            if (ct0.Waybill is null && ct0.Status is TripStatus.AtOrigin or TripStatus.Loaded)
                vm.Todos.Add(new TodoItem($"شمارهٔ بارنامهٔ سفر {ct0.Code} را پیش از شروع سفر ثبت کنید", $"/Driver/Trips/Detail/{ct0.TripId}", "bi-file-earmark-text"));
            if (ct0.Status == TripStatus.Unloaded)
                vm.Todos.Add(new TodoItem($"کد تحویل سفر {ct0.Code} را از گیرنده بگیرید و ثبت کنید", "/Driver/Operations?step=delivered", "bi-shield-lock"));
        }

        var unreadMessages = await db.Messages.CountAsync(m => m.ToKind == OwnerKind.Driver && m.ToId == me.Id && m.ReadAt == null, ct);
        if (unreadMessages > 0)
            vm.Todos.Add(new TodoItem($"{Fa.N(unreadMessages)} پیام خوانده‌نشده", "/Driver/Messages", "bi-chat-dots", "inf"));

        var unrated = await db.Trips.CountAsync(t => t.DriverId == me.Id && t.Load!.ShipperId != null &&
                                                     (t.Status == TripStatus.Delivered || t.Status == TripStatus.Settled) &&
                                                     !db.Ratings.Any(r => r.TripId == t.TripId && r.FromKind == Roles.Driver && r.FromId == me.Id), ct);
        if (unrated > 0)
            vm.Todos.Add(new TodoItem($"به صاحب بارِ {Fa.N(unrated)} سفر تکمیل‌شده امتیاز نداده‌اید", "/Driver/Trips?tab=done", "bi-star", "mut"));

        // ---- نزدیک‌ترین بارها ----
        var (lat, lng, _) = await db.PositionAsync(me.Id, ct);
        vm.HasPosition = lat is not null && lng is not null;
        var cards = await db.Loads.AsNoTracking().Market()
            .OrderByDescending(l => l.PublishedAt).Take(300)
            .ToCards(db, me.Id).ToListAsync(ct);
        cards.MeasureFrom(lat, lng);
        vm.Nearby = (vm.HasPosition
                ? cards.OrderBy(c => c.KmFromMe ?? double.MaxValue).ThenBy(c => c.LoadingFrom)
                : cards.OrderBy(c => c.LoadingFrom).AsEnumerable())
            .Take(6).ToList();

        return View(vm);
    }

    /// <summary>روشن/خاموش کردن «آماده بار» — فقط یک پرچم روی پروفایل، بدون اثر روی سفرهای جاری.</summary>
    [HttpPost]
    public async Task<IActionResult> Availability(bool available, CancellationToken ct)
    {
        await db.Drivers.Where(x => x.DriverId == me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAvailable, available), ct);
        TempData["ok"] = available
            ? "وضعیت شما «آماده بار» شد."
            : "وضعیت شما «غیرفعال برای بار تازه» شد. سفرهای جاری تغییری نمی‌کنند.";
        return RedirectToAction(nameof(Index));
    }
}
