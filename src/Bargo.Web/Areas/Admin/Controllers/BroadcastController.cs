using Bargo.Web.Areas.AdminPanel.Backoffice;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// پیام‌ها و اعلان‌ها: اعلان درون‌برنامه‌ای گروهی (به همه یا یک نقش) و پیامک گروهی.
///
/// پیامک دو مرحله‌ای است: نخست شمار گیرندگان نشان داده می‌شود و فقط با تأیید دوباره
/// ارسال می‌شود — یک کلیک اشتباه نباید به هزاران نفر پیامک بفرستد. هر ارسال یک ردیف
/// Broadcast با شمار گیرندگان می‌سازد تا بعداً معلوم باشد چه چیزی به چه کسانی رفت.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Content)]
public class BroadcastController(BargoDbContext db, NotificationService notify, ISmsSender sms, AuditService audit, CurrentUser me) : Controller
{
    private static readonly string[] Audiences = ["all", "drivers", "shippers", "companies"];
    private const int MaxSmsSegments = 5;
    private const string SmsPurpose = "broadcast";

    // ------------------------------------------------------------------
    //  اعلان عمومی
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? audience, int page = 1, CancellationToken ct = default)
    {
        audience = Norm(audience);
        ViewData["Title"] = audience switch
        {
            "drivers" => "پیام به رانندگان",
            "shippers" => "پیام به صاحبان بار",
            "companies" => "پیام به شرکت‌ها",
            _ => "ارسال اعلان عمومی"
        };

        var vm = new BroadcastVm
        {
            Audience = audience,
            Reach = await ReachAsync(ct),
            History = await PageVm<BroadcastRowVm>.FromAsync(HistoryRows(db.Broadcasts.AsNoTracking()), page, PageLink.For(Request), pageSize: 20, ct: ct)
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Send(string? audience, string? title, string? body, string? link, CancellationToken ct)
    {
        audience = Norm(audience);
        var back = RedirectToAction(nameof(Index), new { audience });
        title = (title ?? "").Trim();
        body = AdminOps.Note(body, 2000);
        link = (link ?? "").Trim();

        if (title.Length is < 3 or > 150) { TempData["err"] = "عنوان اعلان بین ۳ تا ۱۵۰ نویسه باشد."; return back; }
        if (body is null || body.Length < 5) { TempData["err"] = "متن اعلان (دست‌کم ۵ نویسه) را بنویسید."; return back; }
        if (link.Length > 0 && !Url.IsLocalUrl(link)) { TempData["err"] = "پیوند باید نشانی داخلی بارگو باشد (مثلاً /Driver/Loads)."; return back; }

        var n = await notify.BroadcastAsync(audience, title, body, link.Length == 0 ? null : link, ct);
        if (n == 0) { TempData["err"] = $"هیچ حساب تأییدشده‌ای در «{BackofficeFormat.AudienceLabel(audience)}» نیست؛ اعلانی ارسال نشد."; return back; }

        var b = new Broadcast { Audience = audience, Channel = "notification", Title = title, Body = body, SentCount = n, CreatedByAdminId = me.Id };
        db.Broadcasts.Add(b);
        await db.SaveChangesAsync(ct);
        audit.Add("Broadcast", b.BroadcastId, "send:notification", $"اعلان «{title}» به {BackofficeFormat.AudienceLabel(audience)} — {Fa.N(n)} گیرنده", new { audience, title, link, sent = n });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"اعلان «{title}» برای {Fa.N(n)} کاربر ({BackofficeFormat.AudienceLabel(audience)}) ارسال شد.";
        return back;
    }

    // ------------------------------------------------------------------
    //  پیامک گروهی
    // ------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Sms(string? audience, CancellationToken ct)
    {
        ViewData["Title"] = "پیامک گروهی";
        return View(await SmsVmAsync(Norm(audience), "", false, 0, ct));
    }

    /// <summary>
    /// مرحلهٔ اول (confirm=false): فقط شمار گیرندگان. مرحلهٔ دوم (confirm=true): ارسال
    /// به تک‌تک شماره‌ها؛ لاگ هر پیامک را خودِ ISmsSender می‌نویسد و اینجا با ردیف
    /// Broadcast یک‌جا ذخیره می‌شود.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Sms(string? audience, string? text, bool confirm = false, CancellationToken ct = default)
    {
        ViewData["Title"] = "پیامک گروهی";
        audience = Norm(audience);
        text = (text ?? "").Trim();
        var segments = BackofficeFormat.SmsSegments(text);

        if (text.Length < 5)
        {
            TempData["err"] = "متن پیامک (دست‌کم ۵ نویسه) را بنویسید.";
            return View(await SmsVmAsync(audience, text, false, 0, ct));
        }
        if (segments > MaxSmsSegments)
        {
            TempData["err"] = $"متن پیامک {Fa.N(segments)} بخش است؛ بیشینه {Fa.N(MaxSmsSegments)} بخش مجاز است. متن را کوتاه کنید.";
            return View(await SmsVmAsync(audience, text, false, 0, ct));
        }

        var mobiles = await RecipientsAsync(audience, ct);
        if (mobiles.Count == 0)
        {
            TempData["err"] = $"هیچ گیرنده‌ای در «{BackofficeFormat.AudienceLabel(audience)}» نیست (حساب تأییدشده با موبایل معتبر و پیامک روشن).";
            return View(await SmsVmAsync(audience, text, false, 0, ct));
        }

        if (!confirm)
            return View(await SmsVmAsync(audience, text, true, mobiles.Count, ct));

        var ok = 0;
        foreach (var m in mobiles)
            if (await sms.SendAsync(m, text, SmsPurpose, ct)) ok++;

        var b = new Broadcast
        {
            Audience = audience, Channel = "sms",
            Title = text.Length > 60 ? text[..60] + "…" : text,
            Body = text, SentCount = ok, CreatedByAdminId = me.Id
        };
        db.Broadcasts.Add(b);
        await db.SaveChangesAsync(ct);
        audit.Add("Broadcast", b.BroadcastId, "send:sms",
            $"پیامک به {BackofficeFormat.AudienceLabel(audience)} — {Fa.N(ok)} از {Fa.N(mobiles.Count)} موفق، {Fa.N(segments)} بخش",
            new { audience, recipients = mobiles.Count, sent = ok, segments, text });
        await db.SaveChangesAsync(ct);

        if (ok == mobiles.Count)
            TempData["ok"] = $"پیامک برای {Fa.N(ok)} شماره ({BackofficeFormat.AudienceLabel(audience)}) ارسال شد.";
        else
            TempData["err"] = $"پیامک برای {Fa.N(ok)} شماره ارسال شد و {Fa.N(mobiles.Count - ok)} ارسال ناموفق بود؛ جزئیات در فهرست زیر.";
        return RedirectToAction(nameof(Sms), new { audience });
    }

    // ------------------------------------------------------------------
    //  کمکی
    // ------------------------------------------------------------------

    private static string Norm(string? audience) => audience is not null && Audiences.Contains(audience) ? audience : "all";

    private async Task<Dictionary<string, int>> ReachAsync(CancellationToken ct)
    {
        var reach = new Dictionary<string, int>
        {
            ["drivers"] = await db.Drivers.CountAsync(d => d.Status == AccountStatus.Approved, ct),
            ["shippers"] = await db.Shippers.CountAsync(s => s.Status == AccountStatus.Approved, ct),
            ["companies"] = await db.Companies.CountAsync(c => c.Status == AccountStatus.Approved, ct)
        };
        reach["all"] = reach["drivers"] + reach["shippers"] + reach["companies"];
        return reach;
    }

    /// <summary>شماره‌های یکتا؛ صاحب بارِ که پیامک را خاموش کرده کنار گذاشته می‌شود.</summary>
    private async Task<List<string>> RecipientsAsync(string audience, CancellationToken ct)
    {
        var set = new HashSet<string>();
        if (audience is "all" or "drivers")
            set.UnionWith(await db.Drivers.Where(d => d.Status == AccountStatus.Approved && d.Mobile != "").Select(d => d.Mobile).ToListAsync(ct));
        if (audience is "all" or "shippers")
            set.UnionWith(await db.Shippers.Where(s => s.Status == AccountStatus.Approved && s.NotifySms && s.Mobile != "").Select(s => s.Mobile).ToListAsync(ct));
        if (audience is "all" or "companies")
            set.UnionWith(await db.Companies.Where(c => c.Status == AccountStatus.Approved && c.Mobile != "").Select(c => c.Mobile).ToListAsync(ct));
        return set.Where(m => Fa.NormMobile(m).Length == 11).Select(Fa.NormMobile).Distinct().ToList();
    }

    private IQueryable<BroadcastRowVm> HistoryRows(IQueryable<Broadcast> q) => q.OrderByDescending(b => b.BroadcastId).Select(b => new BroadcastRowVm
    {
        Id = b.BroadcastId,
        Audience = b.Audience,
        Channel = b.Channel,
        Title = b.Title,
        Body = b.Body,
        SentCount = b.SentCount,
        CreatedBy = db.Admins.Where(a => a.AdminId == b.CreatedByAdminId).Select(a => a.Name).FirstOrDefault(),
        CreatedAt = b.CreatedAt
    });

    private async Task<SmsBroadcastVm> SmsVmAsync(string audience, string text, bool confirming, int recipients, CancellationToken ct) => new()
    {
        Audience = audience,
        Text = text,
        Confirming = confirming,
        Recipients = recipients,
        Reach = await ReachAsync(ct),
        Logs = await db.SmsLogs.AsNoTracking().OrderByDescending(l => l.SmsLogId).Take(50).ToListAsync(ct),
        History = await HistoryRows(db.Broadcasts.AsNoTracking().Where(b => b.Channel == "sms")).Take(20).ToListAsync(ct)
    };
}
