using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// «داشبورد کل سامانه» — نخستین صفحهٔ مدیر. سه پرسش را جواب می‌دهد: سامانه چقدر بزرگ
/// است (کاربران و ناوگان)، امروز چه می‌گذرد (بار، سفر، پول)، و چه چیزی منتظر من است
/// (صف‌های کار). بدون دسترسی خاص باز است؛ پیوندِ هر کاشی به بخشی می‌رود که دسترسی
/// خودش را می‌سنجد.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
public class DashboardController(BargoDbContext db, WalletService wallet) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "داشبورد کل سامانه";
        var now = DateTime.UtcNow;
        var today = Fa.TodayStartUtc;
        var month = Fa.MonthStartUtc;
        var onlineSince = now.AddMinutes(-15);
        var vm = new AdminDashboardVm();

        // ---------- کاربران و ناوگان ----------
        var drivers = await db.Drivers.CountAsync(ct);
        var shippers = await db.Shippers.CountAsync(ct);
        var companyUsers = await db.CompanyUsers.CountAsync(ct);
        var companies = await db.Companies.CountAsync(ct);
        var vehicles = await db.Vehicles.CountAsync(ct);

        vm.People =
        [
            // «کاربر» یعنی کسی که می‌تواند وارد شود: شرکت خودش وارد نمی‌شود، کاربرانش وارد می‌شوند
            new Stat { Label = "تعداد کاربران", Value = Fa.N(drivers + shippers + companyUsers), Icon = "bi-people", Kind = "t", Href = "/Admin/Users/Drivers" },
            new Stat { Label = "رانندگان", Value = Fa.N(drivers), Icon = "bi-person-badge", Kind = "p", Href = "/Admin/Users/Drivers" },
            new Stat { Label = "صاحبان بار", Value = Fa.N(shippers), Icon = "bi-person-workspace", Kind = "p", Href = "/Admin/Users/Shippers" },
            new Stat { Label = "شرکت‌ها", Value = Fa.N(companies), Icon = "bi-buildings", Kind = "p", Href = "/Admin/Users/Companies" },
            new Stat { Label = "خودروها", Value = Fa.N(vehicles), Icon = "bi-truck-front", Kind = "i", Href = "/Admin/Fleet" },
        ];

        // ---------- عملیات امروز ----------
        var loadsToday = await db.Loads.CountAsync(l => l.CreatedAt >= today, ct);
        var waiting = await db.Loads.CountAsync(l => LoadStatus.Market.Contains(l.Status), ct);
        var live = await db.Trips.CountAsync(t => TripStatus.Live.Contains(t.Status), ct);
        var deliveredMonth = await db.Trips.CountAsync(t => t.DeliveredAt >= month, ct);
        // شارژ کیف پول کنار گذاشته می‌شود: همان پول بعداً به‌صورت «پرداخت کرایه از کیف پول»
        // دوباره ثبت می‌شود و حجم معاملات دو برابر نشان داده می‌شد.
        var volume = await db.Payments
            .Where(p => p.Status == PaymentStatus.Paid && p.PaidAt >= today && p.Purpose != "charge")
            .SumAsync(p => (long?)p.Amount, ct) ?? 0;
        var revenue = await db.WalletTransactions
            .Where(t => t.OwnerKind == OwnerKind.Platform && t.Kind == WalletTxnKind.Commission && t.CreatedAt >= month)
            .SumAsync(t => (long?)t.Amount, ct) ?? 0;
        var complaintsOpen = await db.Complaints.CountAsync(c => ComplaintStatus.Open.Contains(c.Status), ct);
        var online = await db.Drivers.CountAsync(d => d.LastSeenAt >= onlineSince, ct);

        vm.Ops =
        [
            new Stat { Label = "بارهای امروز", Value = Fa.N(loadsToday), Icon = "bi-box-seam", Kind = "p", Href = "/Admin/Loads" },
            new Stat { Label = "بارهای بدون راننده", Value = Fa.N(waiting), Icon = "bi-hourglass-split", Kind = "w", Href = "/Admin/Loads?status=waiting" },
            new Stat { Label = "سفرهای فعال", Value = Fa.N(live), Icon = "bi-truck", Kind = "p", Href = "/Admin/Live/Trips" },
            new Stat { Label = "تحویل‌شده در این ماه", Value = Fa.N(deliveredMonth), Icon = "bi-check2-all", Kind = "s", Href = "/Admin/Trips?tab=done" },
            new Stat { Label = "حجم معاملات امروز", Value = Fa.N(volume / 10), Unit = "تومان", Icon = "bi-arrow-left-right", Kind = "t", Href = "/Admin/Finance" },
            new Stat { Label = "درآمد بارگو در این ماه", Value = Fa.N(revenue / 10), Unit = "تومان", Icon = "bi-cash-stack", Kind = "s", Href = "/Admin/Finance/Commission" },
            new Stat { Label = "شکایات باز", Value = Fa.N(complaintsOpen), Icon = "bi-exclamation-diamond", Kind = complaintsOpen > 0 ? "r" : "i", Href = "/Admin/Complaints?status=reviewing" },
            new Stat { Label = "رانندگان آنلاین (۱۵ دقیقه)", Value = Fa.N(online), Icon = "bi-broadcast", Kind = "i", Href = "/Admin/Live/Vehicles" },
        ];

        // ---------- صف‌های کار ----------
        // فقط صف‌های غیرخالی نشان داده می‌شوند: فهرستی از «۰ مورد» چشم را از کارِ واقعی می‌دزد.
        void Todo(int n, string text, string href, string icon, string tone = "wait")
        {
            if (n > 0) vm.Todo.Add(new TodoItem($"{Fa.N(n)} {text}", href, icon, tone));
        }

        Todo(await db.Documents.CountAsync(d => d.Status == AccountStatus.Pending, ct), "مدرک در انتظار بررسی", "/Admin/Documents", "bi-patch-check");
        Todo(await db.Drivers.CountAsync(d => d.Status == AccountStatus.Pending, ct), "راننده در انتظار تأیید", "/Admin/Drivers/Pending", "bi-person-check");
        Todo(await db.Companies.CountAsync(c => c.Status == AccountStatus.Pending, ct), "درخواست عضویت شرکت", "/Admin/Companies/Requests", "bi-envelope-paper");
        Todo(await db.Shippers.CountAsync(s => s.VerifyStatus == AccountStatus.Pending, ct), "احراز هویت صاحب بار", "/Admin/Users/Verifications", "bi-person-vcard");
        Todo(await db.Vehicles.CountAsync(v => v.VerifyStatus == AccountStatus.Pending, ct), "خودروی در انتظار تأیید", "/Admin/Fleet?verify=pending", "bi-truck");
        Todo(await AdminOps.Problems(db.Trips, now).CountAsync(ct), "سفر مشکل‌دار", "/Admin/Trips/Problems", "bi-exclamation-triangle", "no");
        Todo(await db.Documents.CountAsync(d => d.Status == AccountStatus.Approved && d.ExpiresAt < now, ct), "مدرک تأییدشدهٔ منقضی", "/Admin/Documents/Expired", "bi-calendar-x", "no");
        Todo(await db.PayoutRequests.CountAsync(p => p.Status == PayoutStatus.Pending && p.OwnerKind == OwnerKind.Driver, ct), "درخواست برداشت راننده", "/Admin/Finance/Payouts?owner=driver", "bi-cash-coin");
        Todo(await db.PayoutRequests.CountAsync(p => p.Status == PayoutStatus.Pending && p.OwnerKind == OwnerKind.Company, ct), "درخواست تسویهٔ شرکت", "/Admin/Finance/Payouts?owner=company", "bi-building-check");
        Todo(complaintsOpen, "شکایت باز", "/Admin/Complaints?status=reviewing", "bi-exclamation-octagon", "no");
        Todo(await db.Tickets.CountAsync(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Waiting, ct), "تیکت در انتظار پاسخ", "/Admin/Support", "bi-life-preserver");

        // ---------- سفرهای تازه و نقشه ----------
        vm.RecentTrips = await AdminOps.TripRows(db.Trips.AsNoTracking().OrderByDescending(t => t.CreatedAt)).Take(8).ToListAsync(ct);
        vm.Markers = await AdminOps.LiveMarkersAsync(db, withVehicles: false, ct);
        vm.PlatformBalance = await wallet.PlatformBalanceAsync(ct);

        return View(vm);
    }
}
