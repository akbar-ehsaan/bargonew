using System.Globalization;
using System.Text.RegularExpressions;
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
/// تعرفه و کمیسیون: نرخ کمیسیون بارگو، تعرفهٔ خدمات، هزینهٔ عضویت، پلن‌های اشتراک و
/// کد تخفیف.
///
/// ورودی‌های مبلغ همه «تومان»‌اند (با جداکنندهٔ هزارگان حین تایپ) و ستون‌ها «ریال» —
/// همان درس رنگیو: برچسبِ ریال در فرمی که بقیهٔ پنل تومان نشان می‌دهد یعنی مدیر
/// عددی ده برابرِ انتظارش ثبت می‌کند و هیچ خطایی هم نمی‌بیند.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Finance)]
public class PricingController(BargoDbContext db, SettingsService settings, AuditService audit) : Controller
{
    private const string MembershipKey = "company_membership";

    /// <summary>کلیدهایی که کد به آن‌ها تکیه دارد — کلیدشان عوض و خودشان حذف نمی‌شوند؛ فقط غیرفعال.</summary>
    private static readonly HashSet<string> SystemKeys = ["cargo_insurance", MembershipKey, "urgent_listing"];

    private static string Inv(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    //  درصد کمیسیون
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "درصد کمیسیون";
        var vm = new PricingVm
        {
            CommissionPercent = await settings.GetDecimalAsync(SettingsService.Keys.CommissionPercent, ct),
            CommissionMinRial = await settings.GetLongAsync(SettingsService.Keys.CommissionMinRial, ct),
            VatPercent = await settings.GetDecimalAsync(SettingsService.Keys.VatPercent, ct),
            Recent = await db.Trips.AsNoTracking().OrderByDescending(t => t.TripId).Take(10)
                .Select(t => new LockedTripVm(t.TripId, t.Code, t.Fare, t.CommissionPercent, t.Commission, t.CreatedAt))
                .ToListAsync(ct)
        };
        return View(vm);
    }

    /// <summary>
    /// نرخ تازه فقط روی سفرهایی می‌نشیند که از این پس ساخته می‌شوند: Trip هنگام ساخت
    /// درصد و مبلغ کمیسیون را قفل می‌کند (TripFlow.AcceptOfferAsync) تا تغییر تعرفه
    /// سهمِ حمل‌کننده‌ای را که روی عدد مشخصی توافق کرده عوض نکند.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save(string? commissionPercent, string? commissionMinToman, string? vatPercent, CancellationToken ct)
    {
        var pct = BackofficeLookup.ParseDecimal(commissionPercent);
        var vat = BackofficeLookup.ParseDecimal(vatPercent);
        var min = Fa.ParseToman(commissionMinToman);

        if (pct is null || pct.Value < 0m || pct.Value > 50m)
        { TempData["err"] = "درصد کمیسیون باید عددی بین ۰ و ۵۰ باشد."; return RedirectToAction(nameof(Index)); }
        if (vat is null || vat.Value < 0m || vat.Value > 50m)
        { TempData["err"] = "درصد مالیات بر ارزش افزوده باید عددی بین ۰ و ۵۰ باشد."; return RedirectToAction(nameof(Index)); }
        if (min is null || min.Value < 0)
        { TempData["err"] = "حداقل کمیسیون را به تومان وارد کنید (صفر یعنی بدون حداقل)."; return RedirectToAction(nameof(Index)); }

        var before = new
        {
            percent = await settings.GetAsync(SettingsService.Keys.CommissionPercent, ct),
            minRial = await settings.GetAsync(SettingsService.Keys.CommissionMinRial, ct),
            vat = await settings.GetAsync(SettingsService.Keys.VatPercent, ct)
        };
        var after = new { percent = Inv(pct.Value), minRial = min.Value.ToString(CultureInfo.InvariantCulture), vat = Inv(vat.Value) };

        if (before.percent == after.percent && before.minRial == after.minRial && before.vat == after.vat)
        { TempData["ok"] = "چیزی تغییر نکرد."; return RedirectToAction(nameof(Index)); }

        if (before.percent != after.percent) await settings.SetAsync(SettingsService.Keys.CommissionPercent, after.percent, ct);
        if (before.minRial != after.minRial) await settings.SetAsync(SettingsService.Keys.CommissionMinRial, after.minRial, ct);
        if (before.vat != after.vat) await settings.SetAsync(SettingsService.Keys.VatPercent, after.vat, ct);

        await audit.LogAsync("Setting", 0, "pricing",
            $"کمیسیون {Fa.N(pct.Value, 2)}٪، حداقل {Fa.Toman(min.Value)}، مالیات {Fa.N(vat.Value, 2)}٪",
            new { before, after });

        TempData["ok"] = "نرخ‌ها ذخیره شد. سفرهای ساخته‌شده پیش از این لحظه با نرخ قبلی تسویه می‌شوند.";
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    //  تعرفهٔ خدمات
    // ------------------------------------------------------------------

    public async Task<IActionResult> Tariffs(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "تعرفه خدمات";
        var items = await db.Tariffs.AsNoTracking().OrderBy(t => t.Key).ToListAsync(ct);
        return View(new TariffsVm
        {
            Items = items,
            Edit = edit is int id ? items.FirstOrDefault(t => t.TariffId == id) : null,
            SystemKeys = SystemKeys
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveTariff(int? id, string? key, string? title, string mode, string? amountToman, string? percent, bool isActive, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Tariffs), new { edit = id });
        key = (key ?? "").Trim().ToLowerInvariant();
        title = (title ?? "").Trim();

        Tariff? row = null;
        if (id is int tid)
        {
            row = await db.Tariffs.FirstOrDefaultAsync(t => t.TariffId == tid, ct);
            if (row is null) return NotFound();
            if (SystemKeys.Contains(row.Key)) key = row.Key;
        }

        if (!Regex.IsMatch(key, "^[a-z][a-z0-9_]{1,39}$"))
        { TempData["err"] = "کلید فقط حروف کوچک لاتین، رقم و _ (۲ تا ۴۰ نویسه، شروع با حرف)."; return back; }
        if (title.Length is < 2 or > 100)
        { TempData["err"] = "عنوان تعرفه ۲ تا ۱۰۰ نویسه باشد."; return back; }
        if (await db.Tariffs.AnyAsync(t => t.Key == key && t.TariffId != (id ?? 0), ct))
        { TempData["err"] = $"تعرفه‌ای با کلید «{key}» از قبل وجود دارد."; return back; }

        long? amount = null;
        decimal? pct = null;
        if (mode == "percent")
        {
            pct = BackofficeLookup.ParseDecimal(percent);
            if (pct is null || pct.Value < 0m || pct.Value > 100m) { TempData["err"] = "درصد تعرفه باید بین ۰ و ۱۰۰ باشد."; return back; }
        }
        else
        {
            amount = Fa.ParseToman(amountToman);
            if (amount is null || amount.Value < 0) { TempData["err"] = "مبلغ تعرفه را به تومان وارد کنید."; return back; }
        }

        var before = row is null ? null : new { row.Key, row.Title, row.Amount, row.Percent, row.IsActive };
        row ??= db.Tariffs.Add(new Tariff()).Entity;
        row.Key = key;
        row.Title = title;
        row.Amount = amount;
        row.Percent = pct;
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Tariff", row.TariffId, before is null ? "create" : "update",
            $"تعرفهٔ «{title}» ({key}): {(pct is decimal p ? Fa.N(p, 3) + "٪" : Fa.Toman(amount))}",
            new { before, after = new { key, title, amount, percent = pct, isActive } });

        TempData["ok"] = $"تعرفهٔ «{title}» ذخیره شد.";
        return RedirectToAction(nameof(Tariffs));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteTariff(int id, CancellationToken ct)
    {
        var row = await db.Tariffs.FirstOrDefaultAsync(t => t.TariffId == id, ct);
        if (row is null) return NotFound();
        if (SystemKeys.Contains(row.Key))
        {
            TempData["err"] = $"«{row.Title}» تعرفهٔ سامانه است و حذف نمی‌شود؛ برای توقف، غیرفعالش کنید.";
            return RedirectToAction(nameof(Tariffs));
        }
        db.Tariffs.Remove(row);
        audit.Add("Tariff", id, "delete", $"حذف تعرفهٔ «{row.Title}» ({row.Key})", new { row.Key, row.Title, row.Amount, row.Percent });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "تعرفه حذف شد.";
        return RedirectToAction(nameof(Tariffs));
    }

    // ------------------------------------------------------------------
    //  هزینهٔ عضویت و اشتراک‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Membership(string tab = "active", int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "هزینه عضویت";
        tab = tab == "expired" ? "expired" : "active";
        var now = DateTime.UtcNow;
        var all = db.Subscriptions.AsNoTracking();
        var q = tab == "expired"
            ? all.Include(s => s.Plan).Where(s => s.EndsAt < now).OrderByDescending(s => s.EndsAt)
            : all.Include(s => s.Plan).Where(s => s.EndsAt >= now).OrderBy(s => s.EndsAt);

        var pageVm = await PageVm<Subscription>.FromAsync(q, page, PageLink.For(Request), ct: ct);
        var month = Fa.MonthStartUtc;
        return View(new MembershipVm
        {
            Fee = await db.Tariffs.AsNoTracking().FirstOrDefaultAsync(t => t.Key == MembershipKey, ct),
            Page = pageVm,
            Names = await BackofficeLookup.NamesAsync(db, pageVm.Rows.Select(s => (s.OwnerKind, s.OwnerId)), ct),
            Tab = tab,
            ActiveCount = await all.CountAsync(s => s.EndsAt >= now, ct),
            ExpiredCount = await all.CountAsync(s => s.EndsAt < now, ct),
            MonthRevenue = await all.Where(s => s.CreatedAt >= month).SumAsync(s => s.PaidAmount, ct)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveMembership(string? amountToman, bool isActive, CancellationToken ct)
    {
        var amount = Fa.ParseToman(amountToman);
        if (amount is null || amount.Value < 0)
        { TempData["err"] = "هزینهٔ عضویت را به تومان وارد کنید (صفر یعنی رایگان)."; return RedirectToAction(nameof(Membership)); }

        var row = await db.Tariffs.FirstOrDefaultAsync(t => t.Key == MembershipKey, ct);
        var before = row is null ? null : new { row.Amount, row.IsActive };
        row ??= db.Tariffs.Add(new Tariff { Key = MembershipKey, Title = "هزینه عضویت سالانهٔ شرکت" }).Entity;
        row.Amount = amount;
        row.Percent = null;
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Tariff", row.TariffId, "membership", $"هزینهٔ عضویت شرکت: {Fa.Toman(amount)}{(isActive ? "" : " (غیرفعال)")}",
            new { before, after = new { amount, isActive } });
        TempData["ok"] = "هزینهٔ عضویت ذخیره شد.";
        return RedirectToAction(nameof(Membership));
    }

    // ------------------------------------------------------------------
    //  پلن‌های اشتراک
    // ------------------------------------------------------------------

    public async Task<IActionResult> Plans(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "پلن‌های اشتراک";
        var now = DateTime.UtcNow;
        var items = await db.SubscriptionPlans.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.SubscriptionPlanId).ToListAsync(ct);
        var subs = await db.Subscriptions.AsNoTracking().Where(s => s.EndsAt >= now)
            .GroupBy(s => s.SubscriptionPlanId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return View(new PlansVm
        {
            Items = items,
            Subscribers = subs,
            Edit = edit is int id ? items.FirstOrDefault(p => p.SubscriptionPlanId == id) : null
        });
    }

    [HttpPost]
    public async Task<IActionResult> SavePlan(int? id, string? title, string audience, string? durationDays, string? priceToman,
        string? maxDrivers, string? maxVehicles, string? features, bool isActive, string? sortOrder, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Plans), new { edit = id });
        title = (title ?? "").Trim();
        var duration = BackofficeLookup.ParseInt(durationDays);
        var price = Fa.ParseToman(priceToman);
        var md = BackofficeLookup.ParseInt(maxDrivers);
        var mv = BackofficeLookup.ParseInt(maxVehicles);

        if (title.Length is < 2 or > 80) { TempData["err"] = "عنوان پلن ۲ تا ۸۰ نویسه باشد."; return back; }
        if (audience is not ("company" or "driver" or "shipper")) { TempData["err"] = "مخاطب پلن را انتخاب کنید."; return back; }
        if (duration is null || duration.Value < 1 || duration.Value > 3650) { TempData["err"] = "مدت پلن باید بین ۱ و ۳۶۵۰ روز باشد."; return back; }
        if (price is null || price.Value < 0) { TempData["err"] = "قیمت پلن را به تومان وارد کنید."; return back; }
        if (md is < 1 || mv is < 1) { TempData["err"] = "سقف راننده/خودرو دست‌کم ۱ است؛ خالی یعنی نامحدود."; return back; }

        SubscriptionPlan? row = null;
        if (id is int pid)
        {
            row = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.SubscriptionPlanId == pid, ct);
            if (row is null) return NotFound();
        }
        var before = row is null ? null : new { row.Title, row.Audience, row.DurationDays, row.Price, row.MaxDrivers, row.MaxVehicles, row.IsActive };
        row ??= db.SubscriptionPlans.Add(new SubscriptionPlan()).Entity;
        row.Title = title;
        row.Audience = audience;
        row.DurationDays = duration.Value;
        row.Price = price.Value;
        row.MaxDrivers = md;
        row.MaxVehicles = mv;
        row.Features = string.IsNullOrWhiteSpace(features) ? null : features.Trim();
        row.IsActive = isActive;
        row.SortOrder = BackofficeLookup.ParseInt(sortOrder) ?? 0;
        await db.SaveChangesAsync(ct);

        // قیمت پلنِ فروخته‌شده روی Subscription.PaidAmount ثبت است؛ تغییر اینجا اشتراک‌های جاری را عوض نمی‌کند
        await audit.LogAsync("SubscriptionPlan", row.SubscriptionPlanId, before is null ? "create" : "update",
            $"پلن «{title}»: {Fa.N(duration.Value)} روز، {Fa.Toman(price.Value)}",
            new { before, after = new { title, audience, duration, price, md, mv, isActive } });

        TempData["ok"] = $"پلن «{title}» ذخیره شد.";
        return RedirectToAction(nameof(Plans));
    }

    [HttpPost]
    public async Task<IActionResult> DeletePlan(int id, CancellationToken ct)
    {
        var row = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.SubscriptionPlanId == id, ct);
        if (row is null) return NotFound();
        if (await db.Subscriptions.AnyAsync(s => s.SubscriptionPlanId == id, ct))
        {
            TempData["err"] = $"پلن «{row.Title}» اشتراکِ ثبت‌شده دارد و حذف نمی‌شود؛ غیرفعالش کنید تا دیگر فروخته نشود.";
            return RedirectToAction(nameof(Plans));
        }
        db.SubscriptionPlans.Remove(row);
        audit.Add("SubscriptionPlan", id, "delete", $"حذف پلن «{row.Title}»", new { row.Title, row.Price, row.DurationDays });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "پلن حذف شد.";
        return RedirectToAction(nameof(Plans));
    }

    // ------------------------------------------------------------------
    //  کد تخفیف
    // ------------------------------------------------------------------

    public async Task<IActionResult> Discounts(int? edit, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "کد تخفیف";
        var now = DateTime.UtcNow;
        var all = db.DiscountCodes.AsNoTracking();
        return View(new DiscountsVm
        {
            Page = await PageVm<DiscountCode>.FromAsync(all.OrderByDescending(d => d.DiscountCodeId), page, PageLink.For(Request), ct: ct),
            Edit = edit is int id ? await all.FirstOrDefaultAsync(d => d.DiscountCodeId == id, ct) : null,
            ActiveCount = await all.CountAsync(d => d.IsActive && d.Used < d.MaxUses && (d.ValidTo == null || d.ValidTo >= now), ct)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveDiscount(int? id, string? code, string mode, string? percent, string? amountToman, string? maxUses,
        DateTime? validFrom, DateTime? validTo, bool isActive, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Discounts), new { edit = id });
        if (!ModelState.IsValid) { TempData["err"] = "تاریخ اعتبار معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱"; return back; }

        code = Fa.Latin(code).ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[A-Z0-9_-]{3,30}$"))
        { TempData["err"] = "کد تخفیف ۳ تا ۳۰ نویسه از حروف لاتین، رقم، - و _ باشد."; return back; }

        int? pct = null;
        long? amount = null;
        if (mode == "amount")
        {
            amount = Fa.ParseToman(amountToman);
            if (amount is null || amount.Value <= 0) { TempData["err"] = "مبلغ تخفیف را به تومان وارد کنید."; return back; }
        }
        else
        {
            pct = BackofficeLookup.ParseInt(percent);
            if (pct is null || pct.Value < 1 || pct.Value > 100) { TempData["err"] = "درصد تخفیف باید بین ۱ و ۱۰۰ باشد."; return back; }
        }

        var uses = BackofficeLookup.ParseInt(maxUses) ?? 1;
        if (uses < 1) { TempData["err"] = "سقف استفاده دست‌کم ۱ است."; return back; }

        // اعتبار از ابتدای روزِ شروع تا پایان روزِ آخر به وقت تهران — نه ظهرِ آن روز
        DateTime? from = validFrom is DateTime f ? BackofficeLookup.DayStartUtc(f) : null;
        DateTime? to = validTo is DateTime t ? BackofficeLookup.DayStartUtc(t).AddDays(1).AddSeconds(-1) : null;
        if (from is not null && to is not null && from > to) { TempData["err"] = "تاریخ شروع اعتبار پس از تاریخ پایان است."; return back; }

        if (await db.DiscountCodes.AnyAsync(d => d.Code == code && d.DiscountCodeId != (id ?? 0), ct))
        { TempData["err"] = $"کد «{code}» از قبل وجود دارد."; return back; }

        DiscountCode? row = null;
        if (id is int did)
        {
            row = await db.DiscountCodes.FirstOrDefaultAsync(d => d.DiscountCodeId == did, ct);
            if (row is null) return NotFound();
            if (uses < row.Used) { TempData["err"] = $"این کد {Fa.N(row.Used)} بار استفاده شده؛ سقف نمی‌تواند کمتر باشد."; return back; }
        }
        var before = row is null ? null : new { row.Code, row.Percent, row.Amount, row.MaxUses, row.ValidFrom, row.ValidTo, row.IsActive };
        row ??= db.DiscountCodes.Add(new DiscountCode()).Entity;
        row.Code = code;
        row.Percent = pct;
        row.Amount = amount;
        row.MaxUses = uses;
        row.ValidFrom = from;
        row.ValidTo = to;
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("DiscountCode", row.DiscountCodeId, before is null ? "create" : "update",
            $"کد تخفیف {code}: {(pct is int p ? Fa.N(p) + "٪" : Fa.Toman(amount))}، سقف {Fa.N(uses)} بار",
            new { before, after = new { code, pct, amount, uses, from, to, isActive } });

        TempData["ok"] = $"کد تخفیف {code} ذخیره شد.";
        return RedirectToAction(nameof(Discounts));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteDiscount(int id, CancellationToken ct)
    {
        var row = await db.DiscountCodes.FirstOrDefaultAsync(d => d.DiscountCodeId == id, ct);
        if (row is null) return NotFound();
        if (row.Used > 0)
        {
            TempData["err"] = $"کد {row.Code} استفاده شده و برای سوابق فاکتورها می‌ماند؛ غیرفعالش کنید.";
            return RedirectToAction(nameof(Discounts));
        }
        db.DiscountCodes.Remove(row);
        audit.Add("DiscountCode", id, "delete", $"حذف کد تخفیف {row.Code}", new { row.Code, row.Percent, row.Amount });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "کد تخفیف حذف شد.";
        return RedirectToAction(nameof(Discounts));
    }
}
