using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers.Shared;

/// <summary>
/// ثبت شکایت و گزارش مشکل — مشترک پنل‌ها. شکایتِ گره‌خورده به سفر، طرف مقابل را خودکار
/// تعیین می‌کند (صاحب بار ← حمل‌کننده، حمل‌کننده ← صاحب بار) تا مدیر در «پروندهٔ سفر»
/// همهٔ اسناد را کنار شکایت ببیند.
/// </summary>
[AllowUnapproved]
public abstract class ComplaintsControllerBase(BargoDbContext db, CurrentUser me) : Controller
{
    private const string V = "~/Views/Shared/Panel/Complaints/";

    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "شکایت‌های من";
        var (k, id) = me.Owner;
        var q = db.Complaints.AsNoTracking().Where(c => c.FromKind == k && c.FromId == id).OrderByDescending(c => c.ComplaintId);
        return View(V + "Index.cshtml", await PageVm<Complaint>.FromAsync(q, page, PageLink.For(Request), ct: ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? tripId, CancellationToken ct)
    {
        ViewData["Title"] = "ثبت شکایت و گزارش مشکل";
        ViewBag.Trips = await db.Trips.AsNoTracking().VisibleTo(me.ToActor())
            .OrderByDescending(t => t.TripId).Take(50)
            .Select(t => new { t.TripId, t.Code, From = t.Load!.OriginCity!.Name, To = t.Load.DestCity!.Name })
            .ToListAsync(ct);
        ViewBag.TripId = tripId;
        return View(V + "Create.cshtml");
    }

    [HttpPost]
    public async Task<IActionResult> Create(int? tripId, string kind, string title, string body, CancellationToken ct)
    {
        title = (title ?? "").Trim();
        body = (body ?? "").Trim();
        if (title.Length < 3 || body.Length < 10)
        {
            TempData["err"] = "عنوان و شرح شکایت را کامل بنویسید (شرح دست‌کم ۱۰ نویسه).";
            return RedirectToAction(nameof(Create), new { tripId });
        }

        var (k, id) = me.Owner;
        var c = new Complaint
        {
            FromKind = k, FromId = id, FromName = me.Name, Title = title, Body = body,
            Kind = ComplaintKind.All.Contains(kind) ? kind : ComplaintKind.Other
        };

        if (tripId is int tid)
        {
            var t = await db.Trips.AsNoTracking().VisibleTo(me.ToActor()).Include(x => x.Load)
                .FirstOrDefaultAsync(x => x.TripId == tid, ct);
            if (t is null) return NotFound();
            c.TripId = tid;
            var ownerSide = (t.Load!.ShipperId is int s ? (OwnerKind.Shipper, s) : (OwnerKind.Company, t.Load.CompanyId ?? 0));
            (string, int) carrier = t.CarrierKind == CarrierKind.Company ? (OwnerKind.Company, t.CompanyId ?? 0) : (OwnerKind.Driver, t.DriverId ?? 0);
            var against = ownerSide == (k, id) ? carrier : ownerSide;
            c.AgainstKind = against.Item1;
            c.AgainstId = against.Item2;
        }

        db.Complaints.Add(c);
        await db.SaveChangesAsync(ct);
        c.Code = $"CP-{Fa.Stamp6(c.CreatedAt)}-{c.ComplaintId:0000}";
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"شکایت با کد {c.Code} ثبت شد و نتیجهٔ رسیدگی به شما اعلان می‌شود.";
        return RedirectToAction(nameof(Detail), new { id = c.ComplaintId });
    }

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var (k, oid) = me.Owner;
        var c = await db.Complaints.AsNoTracking().Include(x => x.Trip)
            .FirstOrDefaultAsync(x => x.ComplaintId == id && x.FromKind == k && x.FromId == oid, ct);
        if (c is null) return NotFound();
        ViewData["Title"] = $"شکایت {c.Code}";
        return View(V + "Detail.cshtml", c);
    }
}
