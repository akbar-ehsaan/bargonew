using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers.Shared;

/// <summary>اعلان‌ها — مشترک چهار پنل. برای شرکت، صندوق مال خودِ شرکت است نه کاربر.</summary>
[AllowUnapproved]
public abstract class NotificationsControllerBase(BargoDbContext db, CurrentUser me) : Controller
{
    public async Task<IActionResult> Index(bool unread = false, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "اعلان‌ها";
        var q = db.Notifications.AsNoTracking().Of(me.Owner);
        if (unread) q = q.Where(n => n.ReadAt == null);
        var vm = await PageVm<Notification>.FromAsync(q.OrderByDescending(n => n.NotificationId), page, PageLink.For(Request), ct: ct);
        ViewBag.Unread = unread;
        return View("~/Views/Shared/Panel/Notifications/Index.cshtml", vm);
    }

    /// <summary>باز کردن اعلان: خوانده‌شده و رفتن به پیوندش (فقط پیوند داخلی).</summary>
    public async Task<IActionResult> Open(long id, CancellationToken ct)
    {
        var n = await db.Notifications.Of(me.Owner).FirstOrDefaultAsync(x => x.NotificationId == id, ct);
        if (n is null) return NotFound();
        n.ReadAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return !string.IsNullOrEmpty(n.Link) && Url.IsLocalUrl(n.Link) ? Redirect(n.Link) : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        await db.Notifications.Of(me.Owner).Where(n => n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
        return RedirectToAction(nameof(Index));
    }
}
