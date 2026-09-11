namespace Bargo.Web.Areas.CompanyPanel.Controllers;

using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// داشبورد مدیریتی شرکت — «امروز چه کاری منتظر من است»: صف تخصیص، سفرهای در راه،
/// درخواست‌های مستقیم و مدارک رو به انقضا. برای شرکتِ هنوز تأییدنشده هم باز است
/// (دروازهٔ تأیید به همین‌جا برمی‌گرداند) و پیام وضعیت و پیوند مدارک را نشان می‌دهد.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[AllowUnapproved]
public class DashboardController(BargoDbContext db, CurrentUser me, SettingsService settings) : Controller
{
    public async Task<IActionResult> Index(string? gate, CancellationToken ct)
    {
        ViewData["Title"] = "داشبورد مدیریتی";
        var cid = me.CompanyId;

        var co = await db.Companies.AsNoTracking().Where(c => c.CompanyId == cid)
            .Select(c => new { c.Name, c.Status, c.StatusReason, c.WalletBalance })
            .FirstOrDefaultAsync(ct);
        if (co is null) return NotFound();

        var perms = await db.CompanyUsers.AsNoTracking().Where(u => u.CompanyUserId == me.Id)
            .Select(u => u.Permissions).FirstOrDefaultAsync(ct);

        var vm = new OpsDashboardVm
        {
            CompanyName = co.Name, CompanyStatus = co.Status, StatusReason = co.StatusReason, Gate = gate,
            CanLoads = perms.HasFlag(CompanyPermission.Loads),
            CanDispatch = perms.HasFlag(CompanyPermission.Dispatch),
            CanDrivers = perms.HasFlag(CompanyPermission.Drivers),
            CanFleet = perms.HasFlag(CompanyPermission.Fleet),
            CanFinance = perms.HasFlag(CompanyPermission.Finance)
        };

        var trips = db.Trips.AsNoTracking().Where(t => t.CompanyId == cid);

        // ---- صف تخصیص ----
        var queue = trips.Where(t => t.Status == TripStatus.AwaitingAssignment || t.Status == TripStatus.Assigned);
        vm.QueueTotal = await queue.CountAsync(ct);
        vm.Queue = await queue
            .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Driver)
            .OrderBy(t => t.Status == TripStatus.AwaitingAssignment ? 0 : 1).ThenBy(t => t.ScheduledDepartureAt)
            .Take(8).ToListAsync(ct);

        // ---- سفرهای در راه ----
        var live = trips.Where(t => TripStatus.Live.Contains(t.Status));
        vm.LiveTotal = await live.CountAsync(ct);
        vm.Live = await live
            .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Driver).Include(t => t.Vehicle)
            .OrderByDescending(t => t.LastPointAt)
            .Take(8).ToListAsync(ct);

        // ---- رانندگان ----
        var onlineSince = DateTime.UtcNow.AddMinutes(-CompanyOps.OnlineMinutes);
        var driverCount = await db.Drivers.CountAsync(d => d.CompanyId == cid && d.Status == AccountStatus.Approved, ct);
        var online = await db.Drivers.CountAsync(d => d.CompanyId == cid && d.Status == AccountStatus.Approved && d.LastSeenAt > onlineSince, ct);
        vm.PendingDrivers = await db.Drivers.CountAsync(d => d.CompanyId == cid && d.Status == AccountStatus.Pending, ct);

        // ---- هشدار مدارک ----
        var warnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        vm.Alerts = await CompanyOps.ExpiryAlertsAsync(db, cid, warnDays, ct);
        var vehiclesAlerted = vm.Alerts.Where(a => a.SubjectKind == OwnerKind.Vehicle).Select(a => a.SubjectId).Distinct().Count();

        // ---- کار ورودی ----
        vm.DirectRequests = await db.Loads.Market(cid).CountAsync(l => l.TargetCompanyId == cid, ct);
        vm.PendingOffers = await db.Offers.CountAsync(o => o.CompanyId == cid && o.Status == OfferStatus.Pending, ct);
        vm.MarketLoads = await db.Loads.Market(cid).CountAsync(l => l.TargetCompanyId == null && l.CompanyId != cid, ct);

        // ---- شمارنده‌ها ----
        vm.Stats.Add(new Stat
        {
            Label = "در انتظار تخصیص", Value = Fa.N(vm.QueueTotal), Unit = "سفر", Icon = "bi-hourglass-split",
            Kind = vm.QueueTotal > 0 ? "w" : "i", Href = "/Company/Dispatch"
        });
        vm.Stats.Add(new Stat
        {
            Label = "سفرهای جاری", Value = Fa.N(vm.LiveTotal), Unit = "سفر", Icon = "bi-truck", Kind = "p", Href = "/Company/Trips?tab=current"
        });
        vm.Stats.Add(new Stat
        {
            Label = "رانندگان آنلاین", Value = Fa.N(online), Unit = "از " + Fa.N(driverCount), Icon = "bi-broadcast", Kind = "s",
            Href = "/Company/Drivers/Online"
        });
        vm.Stats.Add(new Stat
        {
            Label = "خودروهای با مدرک رو به انقضا", Value = Fa.N(vehiclesAlerted), Unit = "خودرو", Icon = "bi-alarm",
            Kind = vehiclesAlerted > 0 ? "r" : "i", Href = "/Company/Fleet/Alerts"
        });

        if (vm.CanFinance)
        {
            var monthStart = Fa.MonthStartUtc;
            var income = await db.WalletTransactions.AsNoTracking().Of(me.Owner)
                .Where(x => x.Kind == WalletTxnKind.FareIncome && x.CreatedAt >= monthStart)
                .SumAsync(x => (long?)x.Amount, ct) ?? 0;
            vm.Stats.Add(new Stat
            {
                Label = "موجودی کیف پول", Value = Fa.N(co.WalletBalance / 10), Unit = "تومان", Icon = "bi-wallet2", Kind = "t", Href = "/Company/Finance"
            });
            vm.Stats.Add(new Stat
            {
                Label = "درآمد این ماه", Value = Fa.N(income / 10), Unit = "تومان", Icon = "bi-graph-up-arrow", Kind = "s", Href = "/Company/Finance/Income"
            });
        }
        else
        {
            vm.Stats.Add(new Stat
            {
                Label = "درخواست‌های مستقیم", Value = Fa.N(vm.DirectRequests), Unit = "بار", Icon = "bi-inbox",
                Kind = vm.DirectRequests > 0 ? "w" : "i", Href = "/Company/Loads/Incoming"
            });
            vm.Stats.Add(new Stat
            {
                Label = "پیشنهادهای در انتظار", Value = Fa.N(vm.PendingOffers), Unit = "پیشنهاد", Icon = "bi-tags", Kind = "i", Href = "/Company/Loads/Incoming"
            });
        }

        return View(vm);
    }
}
