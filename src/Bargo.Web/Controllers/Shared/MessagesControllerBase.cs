using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers.Shared;

/// <summary>
/// پیام‌های درون‌برنامه‌ای — مشترک پنل‌ها. دو نوع گفتگو:
///   «offer:{offerId}» میان صاحب بار و پیشنهاددهنده، پیش از قطعی شدن
///   «trip:{tripId}»   میان صاحب بار و حمل‌کننده، پس از ساخت سفر
/// شرط عضویت در گفتگو هر بار از روی خودِ پیشنهاد/سفر سنجیده می‌شود.
/// </summary>
[AllowUnapproved]
public abstract class MessagesControllerBase(BargoDbContext db, CurrentUser me, NotificationService notify) : Controller
{
    public sealed record ThreadRow(string Key, string Title, string LastBody, string LastFrom, DateTime LastAt, int Unread);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "پیام‌ها";
        var (k, id) = me.Owner;
        var mine = await db.Messages.AsNoTracking()
            .Where(m => (m.FromKind == k && m.FromId == id) || (m.ToKind == k && m.ToId == id))
            .OrderByDescending(m => m.MessageId).Take(500).ToListAsync(ct);

        // نام فرستنده در گفتگوهای تأییدنشده مخفی است (Privacy): سفرِ پرداخت‌شده آشکار است
        var tripIds = mine.Where(m => m.TripId != null).Select(m => m.TripId!.Value).Distinct().ToList();
        var paidTrips = tripIds.Count == 0 ? [] :
            await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.TripId) && (t.IsPaid || t.PayMethod == PayMethods.Cash)).Select(t => t.TripId).ToListAsync(ct);

        var rows = mine.GroupBy(m => m.ThreadKey).Select(g =>
        {
            var last = g.First();
            var fromMe = last.FromKind == k && last.FromId == id;
            var revealed = fromMe || (last.TripId is int tid && paidTrips.Contains(tid));
            return new ThreadRow(g.Key, TitleOf(g.Key), last.Body, revealed ? last.FromName : Privacy.HiddenName, last.CreatedAt,
                g.Count(m => m.ToKind == k && m.ToId == id && m.ReadAt == null));
        }).OrderByDescending(r => r.LastAt).ToList();

        return View("~/Views/Shared/Panel/Messages/Index.cshtml", rows);
    }

    public async Task<IActionResult> Thread(string key, CancellationToken ct)
    {
        var party = await PartiesAsync(key, ct);
        if (party is null) return NotFound();

        var (k, id) = me.Owner;
        var msgs = await db.Messages.Where(m => m.ThreadKey == key && ((m.FromKind == k && m.FromId == id) || (m.ToKind == k && m.ToId == id)))
            .OrderBy(m => m.MessageId).ToListAsync(ct);
        foreach (var m in msgs.Where(m => m.ToKind == k && m.ToId == id && m.ReadAt == null)) m.ReadAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // تا تأیید نهایی، نام طرف مقابل روی پیام‌هایش هم مخفی می‌شود — پیش از دستکاری،
        // ردیف‌ها از ردیاب EF جدا می‌شوند تا نام ماسک‌شده هرگز ذخیره نشود
        if (!party.Value.Revealed)
        {
            foreach (var m in msgs) db.Entry(m).State = EntityState.Detached;
            foreach (var m in msgs.Where(m => !(m.FromKind == k && m.FromId == id)))
                m.FromName = Privacy.HiddenName;
        }

        ViewData["Title"] = party.Value.Title;
        ViewBag.Key = key;
        ViewBag.Counterpart = party.Value.Revealed ? party.Value.OtherName : Privacy.HiddenName;
        return View("~/Views/Shared/Panel/Messages/Thread.cshtml", msgs);
    }

    [HttpPost]
    public async Task<IActionResult> Send(string key, string body, CancellationToken ct)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return RedirectToAction(nameof(Thread), new { key });
        if (body.Length > 2000) body = body[..2000];

        var party = await PartiesAsync(key, ct);
        if (party is null) return NotFound();
        var (k, id) = me.Owner;

        // پاسخ به آخرین کسی که به من پیام داده؛ وگرنه طرفِ پیش‌فرضِ گفتگو
        var lastIncoming = await db.Messages.AsNoTracking()
            .Where(m => m.ThreadKey == key && m.ToKind == k && m.ToId == id)
            .OrderByDescending(m => m.MessageId).Select(m => new { m.FromKind, m.FromId }).FirstOrDefaultAsync(ct);
        var to = lastIncoming is not null ? (lastIncoming.FromKind, lastIncoming.FromId) : party.Value.Other;

        db.Messages.Add(new Message
        {
            ThreadKey = key, TripId = party.Value.TripId, LoadId = party.Value.LoadId,
            FromKind = k, FromId = id, FromName = me.Name, ToKind = to.Item1, ToId = to.Item2, Body = body
        });
        var area = to.Item1 switch { OwnerKind.Driver => "Driver", OwnerKind.Shipper => "Shipper", OwnerKind.Company => "Company", _ => "" };
        var senderLabel = party.Value.Revealed ? me.Name : "طرف گفتگو";
        notify.Add(to.Item1, to.Item2, $"پیام تازه از {senderLabel}", body.Length > 80 ? body[..80] + "…" : body, $"/{area}/Messages/Thread?key={Uri.EscapeDataString(key)}", "info");
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Thread), new { key });
    }

    private static string TitleOf(string key) =>
        key.StartsWith("trip:") ? $"گفتگوی سفر #{key[5..]}" : key.StartsWith("offer:") ? $"گفتگوی پیشنهاد #{key[6..]}" : key;

    /// <summary>
    /// طرف مقابل و عنوان گفتگو؛ null اگر کاربر جاری عضو این گفتگو نیست.
    /// Revealed: هویت طرفین آشکار است (کرایهٔ سفر پرداخت شده)؛ در گفتگوی پیشنهاد همیشه مخفی.
    /// </summary>
    private async Task<(string Title, (string, int) Other, string OtherName, bool Revealed, int? TripId, int? LoadId)?> PartiesAsync(string key, CancellationToken ct)
    {
        var actor = me.ToActor();
        var (k, id) = me.Owner;

        if (key.StartsWith("trip:") && int.TryParse(key[5..], out var tripId))
        {
            var t = await db.Trips.AsNoTracking().VisibleTo(actor).Where(x => x.TripId == tripId)
                .Select(x => new
                {
                    x.Code, x.LoadId, x.Load!.ShipperId, LoadCompanyId = x.Load.CompanyId, x.CarrierKind, x.DriverId, x.CompanyId, Revealed = x.IsPaid || x.PayMethod == PayMethods.Cash,
                    Shipper = x.Load.Shipper!.FullName, Driver = x.Driver!.FirstName + " " + x.Driver.LastName, Company = x.Company!.Name
                }).FirstOrDefaultAsync(ct);
            if (t is null) return null;

            (string, int) owner = t.ShipperId is int s ? (OwnerKind.Shipper, s) : (OwnerKind.Company, t.LoadCompanyId ?? 0);
            (string, int) carrier = t.CarrierKind == CarrierKind.Company ? (OwnerKind.Company, t.CompanyId ?? 0) : (OwnerKind.Driver, t.DriverId ?? 0);
            var iAmOwner = owner == (k, id);
            var other = iAmOwner ? carrier : owner;
            var otherName = iAmOwner ? (t.CarrierKind == CarrierKind.Company ? t.Company : t.Driver) : (t.Shipper ?? "صاحب بار");
            return ($"گفتگوی سفر {t.Code}", other, otherName ?? "", t.Revealed, tripId, t.LoadId);
        }

        if (key.StartsWith("offer:") && int.TryParse(key[6..], out var offerId))
        {
            var o = await db.Offers.AsNoTracking().Where(x => x.OfferId == offerId)
                .Select(x => new
                {
                    x.LoadId, x.Load!.Code, x.Load.ShipperId, LoadCompanyId = x.Load.CompanyId, x.DriverId, x.CompanyId,
                    Shipper = x.Load.Shipper!.FullName, Driver = x.Driver!.FirstName + " " + x.Driver.LastName, Company = x.Company!.Name
                }).FirstOrDefaultAsync(ct);
            if (o is null) return null;

            (string, int) owner = o.ShipperId is int s ? (OwnerKind.Shipper, s) : (OwnerKind.Company, o.LoadCompanyId ?? 0);
            (string, int) carrier = o.CompanyId is int c ? (OwnerKind.Company, c) : (OwnerKind.Driver, o.DriverId ?? 0);
            if (owner != (k, id) && carrier != (k, id)) return null;
            var iAmOwner = owner == (k, id);
            return ($"گفتگوی پیشنهاد بار {o.Code}", iAmOwner ? carrier : owner,
                (iAmOwner ? (o.Company ?? o.Driver) : o.Shipper) ?? "", false, null, o.LoadId);
        }
        return null;
    }
}
