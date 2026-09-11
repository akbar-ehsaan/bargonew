using Bargo.Web.Data;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers;

/// <summary>
/// «دیگر برایم پیام نفرست» — همان لینکی که ته پیامک تبلیغاتی می‌رود
/// (آورده‌شده از کارکور).
///
/// بدون ورود باز می‌شود و باید هم همین‌طور باشد: گیرندهٔ پیامک لزوماً عضو بارگو
/// نیست و اگر برای قطع پیام مجبور به ساختن حساب شود، عملاً راهی ندارد.
///
/// مسیر کوتاه (/u/…) عمدی است: هر نویسه‌اش در همهٔ پیامک‌های کمپین تکرار می‌شود
/// و فارسی هر ۷۰ نویسه یک قطعهٔ پیامک است. بارگو MapControllers دارد، پس مسیر
/// با [Route] مستقیم روی اکشن‌ها می‌نشیند.
/// </summary>
public class UnsubscribeController(BargoDbContext db, OptOutLinks links) : Controller
{
    [HttpGet("u/{token}")]
    public async Task<IActionResult> Index(string token)
    {
        var parsed = OptOutLinks.Split(token);
        if (parsed is not { } p || !links.Verify(p.Id, p.Sig)) return NotFound();

        var contact = await db.MarketingContacts.AsNoTracking()
            .Where(c => c.MarketingContactId == p.Id)
            .Select(c => new { c.Mobile, c.OptedOut })
            .FirstOrDefaultAsync();
        if (contact == null) return NotFound();

        // شمارهٔ خودش را نشان می‌دهیم تا مطمئن شود لینک درستی را باز کرده،
        // ولی وسطش پوشیده است — این صفحه بی‌ورود باز می‌شود.
        ViewBag.Masked = Mask(contact.Mobile);
        ViewBag.Already = contact.OptedOut;
        ViewBag.Token = token;
        return View();
    }

    /// <summary>
    /// ⚠️ تغییر وضعیت با POST و نه GET: بعضی پیام‌رسان‌ها و مرورگرها لینک‌ها را
    /// پیش‌بارگیری می‌کنند و با GETی که می‌نویسد، کاربر بی‌آنکه کلیک کند از
    /// فهرست می‌افتاد — یا بدتر، صفحه را باز می‌کرد و می‌دید کارِ انجام‌نشده
    /// انجام شده است.
    /// </summary>
    [HttpPost("u/{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string token)
    {
        var parsed = OptOutLinks.Split(token);
        if (parsed is not { } p || !links.Verify(p.Id, p.Sig)) return NotFound();

        var contact = await db.MarketingContacts.FirstOrDefaultAsync(c => c.MarketingContactId == p.Id);
        if (contact == null) return NotFound();

        if (!contact.OptedOut)
        {
            contact.OptedOut = true;
            contact.OptedOutAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        ViewBag.Masked = Mask(contact.Mobile);
        ViewBag.Done = true;
        return View("Index");
    }

    /// <summary>«۰۹۱۲۱۲۳۴۵۶۷» → «۰۹۱۲***۴۵۶۷»</summary>
    private static string Mask(string mobile) =>
        mobile.Length < 8 ? Fa.Digits(mobile) : Fa.Digits(mobile[..4]) + "***" + Fa.Digits(mobile[^4..]);
}
