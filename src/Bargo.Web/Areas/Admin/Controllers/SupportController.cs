using Bargo.Web.Areas.AdminPanel.Backoffice;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AdminEntity = Bargo.Web.Models.Entities.Admin;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// پشتیبانی: صف تیکت‌ها (با اولویت‌بندی)، گفتگوی هر تیکت و پاسخ، اپراتورها و سوابق
/// پاسخ‌ها با زمان اولین پاسخ.
///
/// پاسخ مدیر با SenderKind = admin ثبت می‌شود؛ پنل کاربر همین را «پشتیبانی بارگو»
/// نشان می‌دهد. هر پاسخ، تیکت را «پاسخ داده شد» می‌کند و پاسخِ بعدیِ کاربر آن را به
/// «در انتظار پاسخ پشتیبانی» برمی‌گرداند (TicketsControllerBase) — صف باز همین دو
/// وضعیت باز/در انتظار است.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Support)]
public class SupportController(BargoDbContext db, NotificationService notify, AuditService audit, CurrentUser me) : Controller
{
    private const int MinPassword = 8;

    // ------------------------------------------------------------------
    //  صف تیکت‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? status, string? sort, string? category, bool mine = false, int page = 1, CancellationToken ct = default)
    {
        sort = sort == "priority" ? "priority" : "";
        ViewData["Title"] = sort == "priority" ? "اولویت‌بندی تیکت‌ها" : "تیکت‌ها";
        status = status is TicketStatus.Answered or TicketStatus.Closed or "all" ? status : "open";
        category = category is not null && TicketCategory.All.Contains(category) ? category : null;

        var all = db.Tickets.AsNoTracking();
        if (category is not null) all = all.Where(t => t.Category == category);
        if (mine) all = all.Where(t => t.AssignedAdminId == me.Id);

        var counts = await all.GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        counts["open"] = counts.GetValueOrDefault(TicketStatus.Open) + counts.GetValueOrDefault(TicketStatus.Waiting);
        counts["all"] = counts.Where(kv => kv.Key is not ("open" or "all")).Sum(kv => kv.Value);

        var q = status switch
        {
            "open" => all.Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Waiting),
            "all" => all,
            _ => all.Where(t => t.Status == status)
        };

        // اولویت: فوری > زیاد > عادی > کم، و درون هر اولویت قدیمی‌ترین اول
        IOrderedQueryable<Ticket> ordered = sort == "priority"
            ? q.OrderBy(t => t.Priority == TicketPriority.Urgent ? 0 : t.Priority == TicketPriority.High ? 1 : t.Priority == TicketPriority.Low ? 3 : 2)
                .ThenBy(t => t.UpdatedAt)
            : status == "open" ? q.OrderBy(t => t.UpdatedAt) : q.OrderByDescending(t => t.UpdatedAt);

        var rows = ordered.Select(t => new TicketRowVm
        {
            Id = t.TicketId,
            Subject = t.Subject,
            Category = t.Category,
            Priority = t.Priority,
            Status = t.Status,
            OwnerKind = t.OwnerKind,
            OwnerName = t.OwnerName,
            AssignedAdminId = t.AssignedAdminId,
            AssignedName = db.Admins.Where(a => a.AdminId == t.AssignedAdminId).Select(a => a.Name).FirstOrDefault(),
            TripId = t.TripId,
            Messages = t.Messages.Count,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt
        });

        var open = db.Tickets.AsNoTracking().Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Waiting);
        var vm = new SupportQueueVm
        {
            Page = await PageVm<TicketRowVm>.FromAsync(rows, page, PageLink.For(Request), ct: ct),
            Status = status, Sort = sort, Category = category, Mine = mine, Counts = counts,
            UrgentOpen = await open.CountAsync(t => t.Priority == TicketPriority.Urgent, ct),
            UnassignedOpen = await open.CountAsync(t => t.AssignedAdminId == null, ct),
            MineOpen = await open.CountAsync(t => t.AssignedAdminId == me.Id, ct),
            MeId = me.Id
        };
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  گفتگو
    // ------------------------------------------------------------------

    public async Task<IActionResult> Thread(int id, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().Include(x => x.Messages).FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        ViewData["Title"] = $"تیکت #{Fa.N(id)} — {t.Subject}";

        var names = await BackofficeLookup.NamesAsync(db, [(t.OwnerKind, t.OwnerId)], ct);
        var vm = new TicketThreadVm
        {
            T = t,
            OwnerMobile = names.Mobile(t.OwnerKind, t.OwnerId),
            AssignedName = t.AssignedAdminId is int aid ? (await BackofficeLookup.AdminNamesAsync(db, [aid], ct)).GetValueOrDefault(aid) : null,
            TripCode = t.TripId is int tid ? await db.Trips.Where(x => x.TripId == tid).Select(x => x.Code).FirstOrDefaultAsync(ct) : null,
            AssignedToMe = t.AssignedAdminId == me.Id,
            OtherTickets = await db.Tickets.AsNoTracking()
                .Where(x => x.OwnerKind == t.OwnerKind && x.OwnerId == t.OwnerId && x.TicketId != id)
                .OrderByDescending(x => x.UpdatedAt).Take(6).ToListAsync(ct)
        };
        ViewBag.OwnerLink = OwnerLink(t.OwnerKind, t.OwnerId);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Reply(int id, string? body, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Thread), new { id });
        body = AdminOps.Note(body, 4000);
        if (body is null || body.Length < 2) { TempData["err"] = "متن پاسخ را بنویسید."; return back; }

        var t = await db.Tickets.FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        if (t.Status == TicketStatus.Closed) { TempData["err"] = "این تیکت بسته شده است؛ برای ادامهٔ گفتگو ابتدا دوباره بازش کنید."; return back; }

        db.TicketMessages.Add(new TicketMessage { TicketId = id, SenderKind = Roles.Admin, SenderId = me.Id, SenderName = me.Name, Body = body });
        t.Status = TicketStatus.Answered;
        t.UpdatedAt = DateTime.UtcNow;
        // پاسخ‌دهنده‌ای که کسی به او تخصیص نداده، خودش مسئول تیکت می‌شود تا صف بی‌صاحب نماند
        var autoAssigned = t.AssignedAdminId is null;
        t.AssignedAdminId ??= me.Id;

        notify.Add(t.OwnerKind, t.OwnerId, $"پاسخ پشتیبانی به تیکت «{t.Subject}»",
            body.Length > 140 ? body[..140] + "…" : body,
            $"/{BackofficeLookup.PanelOf(t.OwnerKind)}/Tickets/Thread/{id}");
        audit.Add("Ticket", id, "reply", $"پاسخ به تیکت «{t.Subject}» ({t.OwnerName})", new { t.OwnerKind, t.OwnerId, length = body.Length, autoAssigned });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "پاسخ ارسال شد و کاربر اعلان گرفت.";
        return back;
    }

    [HttpPost]
    public async Task<IActionResult> Assign(int id, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Thread), new { id });
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        if (t.AssignedAdminId == me.Id) { TempData["err"] = "این تیکت از قبل به شما تخصیص دارد."; return back; }

        var before = t.AssignedAdminId;
        t.AssignedAdminId = me.Id;
        audit.Add("Ticket", id, "assign", $"تخصیص تیکت «{t.Subject}» به {me.Name}", new { from = before, to = me.Id });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "تیکت به شما تخصیص یافت.";
        return back;
    }

    [HttpPost]
    public async Task<IActionResult> SetPriority(int id, string priority, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Thread), new { id });
        if (priority is not (TicketPriority.Low or TicketPriority.Normal or TicketPriority.High or TicketPriority.Urgent))
        {
            TempData["err"] = "اولویت انتخاب‌شده معتبر نیست.";
            return back;
        }
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        if (t.Priority == priority) { TempData["err"] = $"اولویت این تیکت از قبل «{TicketPriority.Label(priority)}» است."; return back; }

        var before = t.Priority;
        t.Priority = priority;
        audit.Add("Ticket", id, "priority", $"اولویت تیکت «{t.Subject}»: {TicketPriority.Label(before)} ← {TicketPriority.Label(priority)}", new { from = before, to = priority });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"اولویت تیکت «{TicketPriority.Label(priority)}» شد.";
        return back;
    }

    [HttpPost]
    public async Task<IActionResult> Close(int id, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Thread), new { id });
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        if (t.Status == TicketStatus.Closed) { TempData["err"] = "این تیکت پیش‌تر بسته شده است."; return back; }

        t.Status = TicketStatus.Closed;
        t.UpdatedAt = DateTime.UtcNow;
        notify.Add(t.OwnerKind, t.OwnerId, $"تیکت «{t.Subject}» بسته شد",
            "اگر موضوع هنوز حل نشده، تیکت تازه‌ای ثبت کنید.",
            $"/{BackofficeLookup.PanelOf(t.OwnerKind)}/Tickets/Thread/{id}");
        audit.Add("Ticket", id, "close", $"بستن تیکت «{t.Subject}» ({t.OwnerName})", new { t.OwnerKind, t.OwnerId });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "تیکت بسته شد.";
        return back;
    }

    /// <summary>بازکردن دوبارهٔ تیکتِ بسته — وقتی کاربر تلفنی پیگیری می‌کند و بحث ادامه دارد.</summary>
    [HttpPost]
    public async Task<IActionResult> Reopen(int id, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Thread), new { id });
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.TicketId == id, ct);
        if (t is null) return NotFound();
        if (t.Status != TicketStatus.Closed) { TempData["err"] = "این تیکت باز است."; return back; }

        t.Status = TicketStatus.Waiting;
        t.UpdatedAt = DateTime.UtcNow;
        audit.Add("Ticket", id, "reopen", $"بازکردن دوبارهٔ تیکت «{t.Subject}»");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "تیکت دوباره باز شد و در صف قرار گرفت.";
        return back;
    }

    // ------------------------------------------------------------------
    //  اپراتورها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Operators(CancellationToken ct)
    {
        ViewData["Title"] = "اپراتورهای پشتیبانی";
        var since = DateTime.UtcNow.AddDays(-7);
        var rows = await db.Admins.AsNoTracking()
            .Where(a => a.IsSuper || a.Permissions.HasFlag(AdminPermission.Support))
            .OrderByDescending(a => a.IsActive).ThenBy(a => a.Name)
            .Select(a => new OperatorRowVm
            {
                AdminId = a.AdminId,
                Name = a.Name,
                Mobile = a.Mobile,
                IsSuper = a.IsSuper,
                IsActive = a.IsActive,
                LastLoginAt = a.LastLoginAt,
                OpenAssigned = db.Tickets.Count(t => t.AssignedAdminId == a.AdminId && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Waiting)),
                Answered7d = db.TicketMessages.Count(m => m.SenderKind == Roles.Admin && m.SenderId == a.AdminId && m.CreatedAt >= since)
            })
            .ToListAsync(ct);

        var vm = new OperatorsVm
        {
            Rows = rows,
            CanAdd = await CanManageOperatorsAsync(ct),
            UnassignedOpen = await db.Tickets.CountAsync(t => t.AssignedAdminId == null && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Waiting), ct)
        };
        return View(vm);
    }

    /// <summary>
    /// تعریف اپراتور — فقط با اختیار «پشتیبانی». اختیارات دیگر از «نقش‌ها و دسترسی‌ها»
    /// و فقط توسط مدیر ارشد داده می‌شود.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AddOperator(string? name, string? mobile, string? password, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Operators));
        if (!await CanManageOperatorsAsync(ct))
        {
            TempData["err"] = "تعریف اپراتور فقط برای مدیر ارشد یا مدیرِ دارای اختیار «تنظیمات» ممکن است.";
            return back;
        }

        name = (name ?? "").Trim();
        var mob = Fa.NormMobile(mobile);
        password ??= "";
        if (name.Length < 3) { TempData["err"] = "نام اپراتور (دست‌کم ۳ نویسه) را وارد کنید."; return back; }
        if (mob.Length == 0) { TempData["err"] = "شمارهٔ موبایل معتبر (۰۹xxxxxxxxx) وارد کنید."; return back; }
        if (password.Length < MinPassword) { TempData["err"] = $"گذرواژه دست‌کم {Fa.N(MinPassword)} نویسه باشد."; return back; }
        if (await db.Admins.AnyAsync(a => a.Mobile == mob, ct)) { TempData["err"] = "با این شماره پیش‌تر یک مدیر تعریف شده است."; return back; }

        var admin = new AdminEntity
        {
            Name = name,
            Mobile = mob,
            PassHash = PasswordHasher.Hash(password),
            IsSuper = false,
            Permissions = AdminPermission.Support,
            IsActive = true
        };
        db.Admins.Add(admin);
        await db.SaveChangesAsync(ct);

        audit.Add("Admin", admin.AdminId, "create", $"تعریف اپراتور پشتیبانی «{name}» ({mob})", new { permissions = AdminPermission.Support.ToString() });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"اپراتور «{name}» تعریف شد و می‌تواند با موبایل {Fa.Digits(mob)} وارد پنل مدیر شود.";
        return back;
    }

    private async Task<bool> CanManageOperatorsAsync(CancellationToken ct)
    {
        var a = await db.Admins.AsNoTracking().Where(x => x.AdminId == me.Id && x.IsActive)
            .Select(x => new { x.IsSuper, x.Permissions }).FirstOrDefaultAsync(ct);
        return a is not null && (a.IsSuper || a.Permissions.HasFlag(AdminPermission.Settings));
    }

    // ------------------------------------------------------------------
    //  سوابق پاسخ‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> History(string? q, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "سوابق پاسخ‌ها";
        var term = AdminOps.Term(q);
        var all = db.Tickets.AsNoTracking().Where(t => t.Status == TicketStatus.Answered || t.Status == TicketStatus.Closed);
        if (term.Length > 0) all = all.Where(t => t.Subject.Contains(term) || t.OwnerName.Contains(term));

        var rows = all.OrderByDescending(t => t.UpdatedAt).Select(t => new HistoryRowVm
        {
            Id = t.TicketId,
            Subject = t.Subject,
            Category = t.Category,
            Status = t.Status,
            OwnerKind = t.OwnerKind,
            OwnerName = t.OwnerName,
            AssignedName = db.Admins.Where(a => a.AdminId == t.AssignedAdminId).Select(a => a.Name).FirstOrDefault(),
            Replies = t.Messages.Count(m => m.SenderKind == Roles.Admin),
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            FirstReplyAt = t.Messages.Where(m => m.SenderKind == Roles.Admin).Min(m => (DateTime?)m.CreatedAt)
        });

        // ۳۰ روز اخیر: میانگین زمان اولین پاسخ روی تیکت‌هایی که پاسخ گرفته‌اند
        var since = DateTime.UtcNow.AddDays(-30);
        var recent = await db.Tickets.AsNoTracking().Where(t => t.CreatedAt >= since)
            .Select(t => new { t.CreatedAt, First = t.Messages.Where(m => m.SenderKind == Roles.Admin).Min(m => (DateTime?)m.CreatedAt) })
            .ToListAsync(ct);
        var answered = recent.Where(r => r.First is not null).Select(r => (r.First!.Value - r.CreatedAt).TotalMinutes).ToList();

        var vm = new SupportHistoryVm
        {
            Page = await PageVm<HistoryRowVm>.FromAsync(rows, page, PageLink.For(Request), ct: ct),
            Q = term,
            Tickets30d = recent.Count,
            Unanswered30d = recent.Count - answered.Count,
            AvgFirstReplyMinutes30d = answered.Count == 0 ? null : answered.Average()
        };
        return View(vm);
    }

    private static string OwnerLink(string kind, int id) => kind switch
    {
        OwnerKind.Driver => $"/Admin/Users/Driver/{id}",
        OwnerKind.Shipper => $"/Admin/Users/Shipper/{id}",
        OwnerKind.Company => $"/Admin/Users/Company/{id}",
        _ => "#"
    };
}
