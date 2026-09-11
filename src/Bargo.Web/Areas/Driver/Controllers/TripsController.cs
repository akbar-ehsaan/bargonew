using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// سفرهای راننده و همهٔ گام‌های اجرای سفر. هر POST سفر را از
/// <see cref="TripFlow.FindForAsync"/> می‌خواند (شرط مالکیت داخل کوئری) و گذار را به
/// TripFlow می‌سپارد؛ این کنترلر هیچ‌وقت Status را نمی‌نویسد.
///
/// صفحهٔ «عملیات سفر» و «مشاهده بارنامه» هم به همین POSTها می‌فرستند و با back=ops یا
/// back=waybill به همان صفحه برمی‌گردند.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class TripsController(
    BargoDbContext db,
    CurrentUser me,
    TripFlow flow,
    SettingsService settings,
    DocumentStorage storage,
    NotificationService notify) : Controller
{
    private static readonly string[] DriverDocKinds =
        [TripDocumentKind.LoadingReceipt, TripDocumentKind.DeliveryReceipt, TripDocumentKind.Photo];

    public async Task<IActionResult> Index(string? tab, int page = 1, CancellationToken ct = default)
    {
        tab = tab is "live" or "done" or "cancelled" ? tab : "upcoming";
        ViewData["Title"] = tab switch
        {
            "live" => "سفرهای در حال انجام",
            "done" => "سفرهای تکمیل‌شده",
            "cancelled" => "سفرهای لغوشده",
            _ => "سفرهای آینده"
        };

        var mine = db.Trips.AsNoTracking().VisibleTo(me.ToActor());
        var counts = await mine.GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);

        IQueryable<Trip> q = tab switch
        {
            "live" => mine.Where(t => TripStatus.Live.Contains(t.Status)).OrderByDescending(t => t.StartedAt),
            "done" => mine.Where(t => TripStatus.Done.Contains(t.Status)).OrderByDescending(t => t.DeliveredAt),
            "cancelled" => mine.Where(t => t.Status == TripStatus.Cancelled).OrderByDescending(t => t.CancelledAt),
            _ => mine.Where(t => TripStatus.Upcoming.Contains(t.Status)).OrderBy(t => t.ScheduledDepartureAt)
        };

        var vm = new TripsVm
        {
            Tab = tab,
            Page = await PageVm<TripRowVm>.FromAsync(q.ToTripRows(), page, PageLink.For(Request), ct: ct),
            CountUpcoming = counts.Where(c => TripStatus.Upcoming.Contains(c.Key)).Sum(c => c.N),
            CountLive = counts.Where(c => TripStatus.Live.Contains(c.Key)).Sum(c => c.N),
            CountDone = counts.Where(c => TripStatus.Done.Contains(c.Key)).Sum(c => c.N),
            CountCancelled = counts.Where(c => c.Key == TripStatus.Cancelled).Sum(c => c.N)
        };
        return View(vm);
    }

    /// <summary>سفر در حال انجام → مستقیم به صفحهٔ همان سفر؛ اگر نیست، صفحهٔ خالی با راه بعدی.</summary>
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        ViewData["Title"] = "سفر در حال انجام";
        if (await db.CurrentTripIdAsync(me.Id, ct) is int id) return RedirectToAction(nameof(Detail), new { id });

        ViewBag.LastDone = await db.Trips.AsNoTracking().VisibleTo(me.ToActor())
            .Where(t => TripStatus.Done.Contains(t.Status)).OrderByDescending(t => t.DeliveredAt)
            .ToTripRows().FirstOrDefaultAsync(ct);
        ViewBag.PendingOffers = await db.Offers.CountAsync(o => o.DriverId == me.Id && o.Status == OfferStatus.Pending, ct);
        return View();
    }

    public async Task<IActionResult> History(DateTime? from, DateTime? to, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تاریخچه سفرها";
        var q = db.Trips.AsNoTracking().VisibleTo(me.ToActor());
        // «تاریخ سفر» = زمان حرکتِ برنامه‌ریزی‌شده، و برای سفرِ بدون برنامه زمان ساخت
        if (from is DateTime f)
        {
            var start = Fa.ToUtc(Fa.ToTehran(f).Date);
            q = q.Where(t => (t.ScheduledDepartureAt ?? t.CreatedAt) >= start);
        }
        if (to is DateTime t0)
        {
            var end = Fa.ToUtc(Fa.ToTehran(t0).Date.AddDays(1));
            q = q.Where(t => (t.ScheduledDepartureAt ?? t.CreatedAt) < end);
        }

        var done = q.Where(t => t.Status == TripStatus.Delivered || t.Status == TripStatus.Settled);
        var vm = new TripHistoryVm
        {
            From = from,
            To = to,
            Page = await PageVm<TripRowVm>.FromAsync(q.OrderByDescending(t => t.ScheduledDepartureAt ?? t.CreatedAt).ToTripRows(),
                page, PageLink.For(Request), ct: ct),
            TotalTrips = await q.CountAsync(ct),
            DoneTrips = await done.CountAsync(ct),
            TotalKm = await q.SumAsync(t => t.TravelledKm, ct),
            // سهم خودِ راننده: کل سهم حمل‌کننده در سفر مستقل، سهم راننده در سفر شرکتی
            TotalIncome = (await done.Where(t => t.CarrierKind != CarrierKind.Company).SumAsync(t => (long?)t.CarrierShare, ct) ?? 0)
                        + (await done.Where(t => t.CarrierKind == CarrierKind.Company).SumAsync(t => t.DriverShare, ct) ?? 0)
        };
        return View(vm);
    }

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        ViewData["Title"] = $"سفر {trip.Code}";
        return View(await db.TripDetailAsync(trip, me.Id, settings, Request, ct));
    }

    // ------------------------------------------------------------------
    //  گام‌های سفر
    // ------------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> Move(int id, string to, string? note, double? lat, double? lng, string? back, string? step, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        // لغو فرم و علتِ اجباری خودش را دارد
        if (string.IsNullOrEmpty(to) || to == TripStatus.Cancelled) return BadRequest();

        try
        {
            await flow.MoveAsync(trip, to, me.ToActor(), Clean(note, 500), lat, lng, ct);
            if (to == TripStatus.AwaitingAssignment)
            {
                TempData["ok"] = $"مأموریت سفر {trip.Code} را نپذیرفتید؛ سفر برای تخصیص دوباره به شرکت برگشت.";
                return RedirectToAction(nameof(Index));
            }
            TempData["ok"] = to switch
            {
                TripStatus.Accepted => $"مأموریت سفر {trip.Code} را پذیرفتید. در زمان بارگیری «حرکت به سمت مبدا» را بزنید.",
                TripStatus.Unloaded when trip.DeliveryOtpHash is not null =>
                    "تخلیه ثبت شد و کد تحویل برای گیرنده پیامک شد. پس از تحویل کامل، کد را از گیرنده بگیرید و ثبت کنید.",
                _ => $"«{TripFlow.ActionLabel(to)}» برای سفر {trip.Code} ثبت شد."
            };
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return Back(back, id, step);
    }

    /// <summary>تحویل بار با کد یکبارمصرفی که فقط گیرنده دارد.</summary>
    [HttpPost]
    public async Task<IActionResult> Deliver(int id, string? code, double? lat, double? lng, string? back, string? step, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();

        try
        {
            if (string.IsNullOrWhiteSpace(code)) throw new UserError("کد تحویل را از گیرنده بگیرید و وارد کنید.");
            await flow.ConfirmDeliveryAsync(trip, code, me.ToActor(), lat, lng, ct);
            TempData["ok"] = trip.Status == TripStatus.Settled
                ? trip.CarrierKind == CarrierKind.Company
                    ? $"تحویل بار ثبت و سفر {trip.Code} تسویه شد. سهم شما را شرکت پرداخت می‌کند."
                    : $"تحویل بار ثبت شد و {Fa.Toman(trip.CarrierShare)} به کیف پول شما واریز شد."
                : $"تحویل بار سفر {trip.Code} ثبت شد. کرایه پس از پرداخت صاحب بار تسویه می‌شود.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return Back(back, id, step);
    }

    [HttpPost]
    public async Task<IActionResult> ResendOtp(int id, string? back, string? step, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();

        if (!TripFlow.NextSteps(trip.Status, Roles.Driver).Contains(TripStatus.Delivered))
            TempData["err"] = "ارسال کد تحویل فقط پس از ثبت تخلیه ممکن است.";
        // جلوی پیامک‌بارانِ گیرنده با کلیک‌های پشت‌سرهم
        else if (trip.DeliveryOtpSentAt is DateTime sent && sent > DateTime.UtcNow.AddMinutes(-2))
            TempData["err"] = $"کد تحویل {Fa.Ago(sent)} ارسال شده است؛ دو دقیقه بعد دوباره تلاش کنید.";
        else
        {
            await flow.IssueDeliveryOtpAsync(trip, ct);
            await db.SaveChangesAsync(ct);
            TempData["ok"] = "کد تحویل تازه برای گیرنده پیامک شد. کد قبلی دیگر معتبر نیست.";
        }
        return Back(back, id, step);
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            await flow.CancelAsync(trip, me.ToActor(), Clean(reason, 500) ?? "", ct);
            TempData["ok"] = $"سفر {trip.Code} لغو شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Waybill(int id, string? number, IFormFile? file, string? back, string? step, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            number = Fa.Latin(number);
            if (number.Length == 0) throw new UserError("شمارهٔ بارنامه را وارد کنید.");
            if (number.Length > 40) throw new UserError("شمارهٔ بارنامه حداکثر ۴۰ نویسه است.");
            if (trip.Waybill?.Status == "verified") throw new UserError("بارنامهٔ این سفر تأیید شده و قابل تغییر نیست.");
            var path = await storage.SaveAsync(file, "trips", ct);
            await flow.RegisterWaybillAsync(trip, number, path, me.ToActor(), ct);
            TempData["ok"] = $"بارنامهٔ شمارهٔ {number} برای سفر {trip.Code} ثبت شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return Back(back, id, step);
    }

    [HttpPost]
    public async Task<IActionResult> Document(int id, string? kind, string? title, IFormFile? file, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(id, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            if (kind is null || !DriverDocKinds.Contains(kind)) throw new UserError("نوع سند معتبر نیست.");
            if (trip.Status is TripStatus.AwaitingAssignment or TripStatus.Assigned or TripStatus.Cancelled)
                throw new UserError("در این مرحلهٔ سفر نمی‌توان سند بارگذاری کرد.");
            if (file is null || file.Length == 0) throw new UserError("فایل سند را انتخاب کنید.");

            var path = await storage.SaveAsync(file, "trips", ct);
            var t = Clean(title, 150) ?? TripDocumentKind.Label(kind);
            db.TripDocuments.Add(new TripDocument
            {
                TripId = trip.TripId, Kind = kind, Title = t, FilePath = path,
                UploadedByKind = Roles.Driver, UploadedById = me.Id
            });
            notify.ToLoadOwner(trip.Load!, $"سند تازه برای سفر {trip.Code}", $"{TripDocumentKind.Label(kind)}: {t}",
                trip.Load!.ShipperId != null ? $"/Shipper/Tracking?tripId={trip.TripId}" : null);
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"«{t}» بارگذاری شد و صاحب بار آن را می‌بیند.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>امتیاز راننده به صاحب بار، پس از تحویل.</summary>
    [HttpPost]
    public async Task<IActionResult> Rate(int id, int score, string? comment, CancellationToken ct)
    {
        if (!await db.Trips.AnyAsync(t => t.TripId == id && t.DriverId == me.Id, ct)) return NotFound();
        try
        {
            await flow.RateAsync(id, me.ToActor(), score, Clean(comment, 1000), ct);
            TempData["ok"] = "امتیاز شما به صاحب بار ثبت شد. سپاس از بازخوردتان.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    private IActionResult Back(string? back, int id, string? step) => back switch
    {
        "ops" => RedirectToAction("Index", "Operations", new { step }),
        "waybill" => RedirectToAction("Current", "Waybills"),
        _ => RedirectToAction(nameof(Detail), new { id })
    };

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
