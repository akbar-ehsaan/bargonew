using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers.Shared;

/// <summary>
/// «پشتیبانی» — همهٔ رفتار ماژول یک‌جا، و هر Area فقط یک زیرکلاس چندخطی دارد
/// (الگوی TicketsControllerBase رنگیو). منطق در هر چهار پنل یکی است و تنها تفاوت
/// صاحب تیکت است که از CurrentUser.Owner می‌آید.
///
/// ویوها مشترک‌اند (~/Views/Shared/Panel/Tickets) و پیوندهای داخلشان با asp-action
/// ساخته می‌شوند تا Area جاری خودکار در نشانی بنشیند.
/// </summary>
[AllowUnapproved]
public abstract class TicketsControllerBase(BargoDbContext db, CurrentUser me, SettingsService settings) : Controller
{
    private const string V = "~/Views/Shared/Panel/Tickets/";

    public async Task<IActionResult> Index(string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تیکت‌های پشتیبانی";
        var q = db.Tickets.AsNoTracking().Of(me.Owner);
        if (status == "open") q = q.Where(t => t.Status != TicketStatus.Closed);
        else if (status == "closed") q = q.Where(t => t.Status == TicketStatus.Closed);
        var vm = await PageVm<Ticket>.FromAsync(q.OrderByDescending(t => t.UpdatedAt), page, PageLink.For(Request), ct: ct);
        ViewBag.Status = status;
        return View(V + "Index.cshtml", vm);
    }

    [HttpGet]
    public IActionResult Create(string? category, int? tripId)
    {
        ViewData["Title"] = category == TicketCategory.CargoIssue ? "گزارش مشکل بار" : "ثبت تیکت";
        ViewBag.Category = TicketCategory.All.Contains(category) ? category : TicketCategory.Support;
        ViewBag.TripId = tripId;
        return View(V + "Create.cshtml");
    }

    [HttpPost]
    public async Task<IActionResult> Create(string category, string subject, string body, string? tripCode, string priority = TicketPriority.Normal, CancellationToken ct = default)
    {
        subject = (subject ?? "").Trim();
        body = (body ?? "").Trim();
        if (subject.Length is < 3 or > 200 || body.Length < 5)
        {
            TempData["err"] = "موضوع (۳ تا ۲۰۰ نویسه) و متن تیکت را کامل بنویسید.";
            return RedirectToAction(nameof(Create), new { category });
        }

        int? tripId = null;
        if (!string.IsNullOrWhiteSpace(tripCode))
        {
            var code = Fa.Latin(tripCode).ToUpperInvariant();
            tripId = await db.Trips.VisibleTo(me.ToActor()).Where(t => t.Code == code).Select(t => (int?)t.TripId).FirstOrDefaultAsync(ct);
            if (tripId is null)
            {
                TempData["err"] = "سفری با این کد در سفرهای شما پیدا نشد.";
                return RedirectToAction(nameof(Create), new { category });
            }
        }

        var (kind, id) = me.Owner;
        var t = new Ticket
        {
            OwnerKind = kind, OwnerId = id, OwnerName = me.Name,
            Category = TicketCategory.All.Contains(category) ? category : TicketCategory.Support,
            Priority = priority is TicketPriority.Low or TicketPriority.High or TicketPriority.Urgent ? priority : TicketPriority.Normal,
            Subject = subject, TripId = tripId
        };
        t.Messages.Add(new TicketMessage { SenderKind = me.Role, SenderId = me.Id, SenderName = me.Name, Body = body });
        db.Tickets.Add(t);
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "تیکت ثبت شد. پاسخ پشتیبانی در همین صفحه و در اعلان‌ها نمایش داده می‌شود.";
        return RedirectToAction(nameof(Thread), new { id = t.TicketId });
    }

    public async Task<IActionResult> Thread(int id, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().Of(me.Owner).Include(x => x.Messages)
            .FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        ViewData["Title"] = $"تیکت #{Fa.N(t.TicketId)}";
        return View(V + "Thread.cshtml", t);
    }

    [HttpPost]
    public async Task<IActionResult> Reply(int id, string body, CancellationToken ct)
    {
        var t = await db.Tickets.Of(me.Owner).FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        if (string.IsNullOrWhiteSpace(body)) return RedirectToAction(nameof(Thread), new { id });
        if (t.Status == TicketStatus.Closed)
        {
            TempData["err"] = "این تیکت بسته شده است؛ تیکت تازه ثبت کنید.";
            return RedirectToAction(nameof(Thread), new { id });
        }
        db.TicketMessages.Add(new TicketMessage { TicketId = id, SenderKind = me.Role, SenderId = me.Id, SenderName = me.Name, Body = body.Trim() });
        t.Status = TicketStatus.Waiting;
        t.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Thread), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Close(int id, CancellationToken ct)
    {
        var n = await db.Tickets.Of(me.Owner).Where(x => x.TicketId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TicketStatus.Closed).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
        if (n == 0) return NotFound();
        TempData["ok"] = "تیکت بسته شد.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Contact(CancellationToken ct)
    {
        ViewData["Title"] = "تماس با پشتیبانی";
        ViewBag.Phone = await settings.GetAsync(SettingsService.Keys.SupportPhone, ct);
        var faq = await db.ContentItems.AsNoTracking().Where(c => c.Kind == ContentKind.Faq && c.IsPublished)
            .OrderBy(c => c.SortOrder).ToListAsync(ct);
        return View(V + "Contact.cshtml", faq);
    }
}
