using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// سفارش‌ها = سفرهای ساخته‌شده از بارهای من. صفحهٔ جزئیات همهٔ کارهای صاحب بار روی
/// یک سفر را یک‌جا دارد: پرداخت کرایه، کد تحویل، اسناد، لغو، امتیاز و شکایت.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class OrdersController(BargoDbContext db, CurrentUser me, TripFlow flow) : Controller
{
    private static readonly string[] Current = [.. TripStatus.Upcoming, .. TripStatus.Live];

    public static readonly (string Key, string Label)[] Tabs =
    [
        ("current", "سفارش‌های جاری"),
        ("done", "تکمیل‌شده"),
        ("cancelled", "لغوشده"),
    ];

    private static IQueryable<Trip> Filter(IQueryable<Trip> q, string tab) => tab switch
    {
        "done" => q.Where(t => TripStatus.Done.Contains(t.Status)),
        "cancelled" => q.Where(t => t.Status == TripStatus.Cancelled),
        _ => q.Where(t => Current.Contains(t.Status))
    };

    public async Task<IActionResult> Index(string? tab, int page = 1, CancellationToken ct = default)
    {
        tab = Tabs.Any(t => t.Key == tab) ? tab! : "current";
        ViewData["Title"] = Tabs.First(t => t.Key == tab).Label;

        var mine = db.Trips.AsNoTracking().VisibleTo(me.ToActor());
        var counts = new Dictionary<string, int>();
        foreach (var t in Tabs) counts[t.Key] = await Filter(mine, t.Key).CountAsync(ct);

        var ordered = tab switch
        {
            "done" => Filter(mine, tab).OrderByDescending(t => t.DeliveredAt ?? t.CreatedAt),
            "cancelled" => Filter(mine, tab).OrderByDescending(t => t.CancelledAt ?? t.CreatedAt),
            _ => Filter(mine, tab).OrderByDescending(t => t.TripId)
        };
        var vm = await PageVm<ShipperTripRow>.FromAsync(ordered.Select(ShipperTripRow.FromTrip), page, PageLink.For(Request), 20, ct);

        ViewBag.Tab = tab;
        ViewBag.Counts = counts;
        return View(vm);
    }

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var actor = me.ToActor();
        var trip = await flow.FindForAsync(id, actor, ct);
        if (trip is null) return NotFound();
        ViewData["Title"] = $"سفارش {trip.Code}";

        var myRating = await db.Ratings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TripId == id && r.FromKind == Roles.Shipper && r.FromId == me.Id, ct);
        var done = TripStatus.Done.Contains(trip.Status);

        var vm = new ShipperOrderVm
        {
            Trip = trip,
            Events = await db.TripEvents.AsNoTracking().Where(e => e.TripId == id).OrderByDescending(e => e.CreatedAt).ToListAsync(ct),
            Documents = await db.TripDocuments.AsNoTracking().Where(d => d.TripId == id).OrderByDescending(d => d.CreatedAt).ToListAsync(ct),
            Payments = await db.Payments.AsNoTracking()
                .Where(p => p.TripId == id && p.PayerKind == OwnerKind.Shipper && p.PayerId == me.Id)
                .OrderByDescending(p => p.PaymentId).ToListAsync(ct),
            Invoices = await db.Invoices.AsNoTracking().Of(me.Owner).Where(i => i.TripId == id).ToListAsync(ct),
            Refunds = await db.Refunds.AsNoTracking().Of(me.Owner).Where(r => r.TripId == id).ToListAsync(ct),
            Complaints = await db.Complaints.AsNoTracking()
                .Where(c => c.TripId == id && c.FromKind == Roles.Shipper && c.FromId == me.Id)
                .OrderByDescending(c => c.ComplaintId).ToListAsync(ct),
            MyRating = myRating,
            WalletBalance = await db.Shippers.AsNoTracking().Where(s => s.ShipperId == me.Id).Select(s => s.WalletBalance).FirstOrDefaultAsync(ct),
            CanCancel = TripFlow.CanCancel(trip.Status, Roles.Shipper),
            CanRate = done && myRating is null && (trip.DriverId != null || trip.CompanyId != null),
            RateTarget = trip.CarrierKind == CarrierKind.Company && trip.DriverId is null
                ? trip.Company?.Name ?? "شرکت حمل‌کننده"
                : trip.Driver?.FullName ?? "راننده",
            PointCount = await db.TrackingPoints.CountAsync(p => p.TripId == id, ct),
            AssignmentChanges = await db.TripAssignments.CountAsync(a => a.TripId == id && a.PreviousDriverId != null, ct)
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> ResendCode(int id, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();

        if (trip.Status != TripStatus.Unloaded)
        {
            TempData["err"] = "کد تحویل فقط پس از تأیید تخلیهٔ بار و پیش از ثبت تحویل ارسال می‌شود.";
        }
        else if (trip.DeliveryOtpSentAt is DateTime sent && DateTime.UtcNow - sent < TimeSpan.FromMinutes(2))
        {
            TempData["err"] = $"کد تحویل {Fa.Ago(sent)} ارسال شده است؛ پیش از درخواست دوباره دو دقیقه صبر کنید.";
        }
        else
        {
            try
            {
                await flow.IssueDeliveryOtpAsync(trip, ct);
                await db.SaveChangesAsync(ct);
                var mobile = trip.Load!.ReceiverMobile ?? trip.Load.Shipper?.Mobile;
                TempData["ok"] = $"کد تحویل تازه به {ShipperUi.MaskMobile(mobile)} پیامک شد و در اعلان‌های شما هم آمده است. کد قبلی دیگر معتبر نیست.";
            }
            catch (UserError e)
            {
                TempData["err"] = e.Message;
            }
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            var wasPaid = trip.IsPaid;
            await flow.CancelAsync(trip, me.ToActor(), reason?.Trim() ?? "", ct);
            TempData["ok"] = wasPaid
                ? $"سفر {trip.Code} لغو شد و کرایهٔ {Fa.Toman(trip.Fare)} به کیف پول شما برگشت."
                : $"سفر {trip.Code} لغو شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }
}
