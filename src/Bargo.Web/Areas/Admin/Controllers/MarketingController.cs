using System.Text;
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
/// بانک مخاطبان بازاریابی و کمپین پیامکی (آورده‌شده از کارکور).
///
/// عمداً از <see cref="BroadcastController"/> جداست: آن یکی یک متن به حساب‌های
/// تأییدشدهٔ خود بارگو می‌فرستد و در همان درخواست تمام می‌شود. اینجا بانکِ
/// چندده‌هزارتایی است، صف دارد، وضعیت تک‌تک شماره‌ها را نگه می‌دارد و لغو اشتراک
/// را محترم می‌شمارد؛ خودِ ارسال با <see cref="CampaignSenderJob"/> در پس‌زمینه است.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Content)]
public class MarketingController(BargoDbContext db, SettingsService settings, CurrentUser me, AuditService audit) : Controller
{
    private const int PageSize = 50;
    private const string TestPurpose = "campaign-test";

    // ================================================================ مخاطبان

    public async Task<IActionResult> Contacts(string? q, string? cat, string? province, string? state, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بانک مخاطبان";
        var query = Filtered(q, cat, province, state)
            .OrderBy(c => c.Province).ThenBy(c => c.City).ThenBy(c => c.MarketingContactId)
            .AsNoTracking();

        return View(new MarketingContactsVm
        {
            Page = await PageVm<MarketingContact>.FromAsync(query, page, PageLink.For(Request), PageSize, ct),
            Categories = await CategoriesAsync(ct),
            Provinces = await ProvincesAsync(ct),
            Q = q, Cat = cat, Province = province, State = state,
            All = await db.MarketingContacts.CountAsync(ct),
            OptedOut = await db.MarketingContacts.CountAsync(c => c.OptedOut, ct)
        });
    }

    /// <summary>
    /// فیلتر مشترک فهرست و ساخت صف کمپین — عمداً یک متد.
    ///
    /// اگر دو نسخه می‌داشت، روزی عددی که مدیر روی صفحه می‌بیند با تعدادی که
    /// واقعاً پیامک می‌گیرند فرق می‌کرد و کسی هم متوجه نمی‌شد.
    /// </summary>
    private IQueryable<MarketingContact> Filtered(string? q, string? cat, string? province, string? state)
    {
        var query = db.MarketingContacts.AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = Fa.Latin(q.Trim());
            query = query.Where(c => c.Name!.Contains(t) || c.Mobile.Contains(t) || c.City!.Contains(t));
        }
        if (!string.IsNullOrWhiteSpace(cat)) query = query.Where(c => c.Category == cat);
        if (!string.IsNullOrWhiteSpace(province)) query = query.Where(c => c.Province == province);

        query = state switch
        {
            "optedout" => query.Where(c => c.OptedOut),
            "sent" => query.Where(c => c.SentCount > 0),
            "untouched" => query.Where(c => !c.OptedOut && c.SentCount == 0),
            _ => query
        };
        return query;
    }

    private Task<List<string>> CategoriesAsync(CancellationToken ct = default) =>
        db.MarketingContacts.Where(c => c.Category != null && c.Category != "")
            .Select(c => c.Category!).Distinct().OrderBy(x => x).ToListAsync(ct);

    private Task<List<string>> ProvincesAsync(CancellationToken ct = default) =>
        db.MarketingContacts.Where(c => c.Province != null && c.Province != "")
            .Select(c => c.Province!).Distinct().OrderBy(x => x).ToListAsync(ct);

    private Task<List<string>> CitiesAsync(CancellationToken ct = default) =>
        db.MarketingContacts.Where(c => c.City != null && c.City != "")
            .Select(c => c.City!).Distinct().OrderBy(x => x).Take(400).ToListAsync(ct);

    /// <summary>«دیگر پیام نده» — از این به بعد هیچ کمپینی این شماره را نمی‌گیرد.</summary>
    [HttpPost]
    public async Task<IActionResult> ToggleOptOut(int id, string? back, CancellationToken ct)
    {
        var c = await db.MarketingContacts.FirstOrDefaultAsync(x => x.MarketingContactId == id, ct);
        if (c == null) return NotFound();

        c.OptedOut = !c.OptedOut;
        c.OptedOutAt = c.OptedOut ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(ct);

        TempData["ok"] = c.OptedOut
            ? $"شمارهٔ {Fa.Digits(c.Mobile)} از این پس پیامکی نمی‌گیرد."
            : $"شمارهٔ {Fa.Digits(c.Mobile)} دوباره در فهرست ارسال است.";
        return AdminOps.Back(this, back, Url.Action(nameof(Contacts))!);
    }

    // ================================================================ داشبورد

    /// <summary>
    /// داشبورد پیامک — مدیر *پیش از هر کاری* یک تصویر کامل می‌بیند: چند نفر در
    /// بانک‌اند، چند نفرشان واقعاً پیامک‌پذیرند (و چرا بقیه نه)، سقف امروز چقدر
    /// مانده و کمپین‌های قبلی چه کردند.
    /// </summary>
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        ViewData["Title"] = "داشبورد پیامک";

        var total = await db.MarketingContacts.CountAsync(ct);
        var optedOut = await db.MarketingContacts.CountAsync(c => c.OptedOut, ct);
        var touched = await db.MarketingContacts.CountAsync(c => !c.OptedOut && c.SentCount > 0, ct);

        // گروه‌بندی روی پایگاه‌داده، نگاشت به رکورد در حافظه (سازندهٔ رکورد در
        // GroupBy ترجمه نمی‌شود).
        var cats = await db.MarketingContacts
            .GroupBy(c => c.Category)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return View(new MarketingDashboardVm
        {
            Total = total,
            OptedOut = optedOut,
            Touched = touched,
            Untouched = total - optedOut - touched,
            Sendable = total - optedOut,
            Categories = [.. cats.Select(x => new ContactCategoryCount(x.Name, x.Count)).OrderByDescending(x => x.Count)],
            // اعضای خودِ بارگو — تا معلوم باشد کمپین‌ها دارند به کجا می‌رسند.
            Drivers = await db.Drivers.CountAsync(d => d.Status == AccountStatus.Approved, ct),
            Shippers = await db.Shippers.CountAsync(s => s.Status == AccountStatus.Approved, ct),
            Companies = await db.Companies.CountAsync(c => c.Status == AccountStatus.Approved, ct),
            Budget = await CampaignGuards.BudgetAsync(db, settings, ct),
            PriceRial = await settings.GetLongAsync(SettingsService.Keys.SmsCampaignPriceRial, ct),
            SmsReal = await SmsRealAsync(ct),
            CampaignOn = await settings.GetBoolAsync(SettingsService.Keys.SmsCampaignEnabled, ct),
            Campaigns = await db.SmsCampaigns.OrderByDescending(c => c.SmsCampaignId).Take(8).AsNoTracking().ToListAsync(ct)
        });
    }

    /// <summary>سرویس پیامک واقعاً می‌فرستد یا فقط در لاگ ثبت می‌کند؟</summary>
    private async Task<bool> SmsRealAsync(CancellationToken ct) =>
        (await settings.GetAsync(SettingsService.Keys.SmsProvider, ct)).Trim().ToLowerInvariant() == "ictx";

    // ================================================================ گروه‌ها

    /// <summary>
    /// فهرست همهٔ گروه‌ها با تعدادشان — و راهی برای درست‌کردنشان.
    ///
    /// تا وقتی مدیر نتواند حتی *ببیند* چه گروه‌هایی در بانک هست، فهرست کشویی
    /// صفحهٔ مخاطبان همه را پشت هم می‌ریزد بی‌آنکه بگوید کدام یکی است و کدام
    /// پنجاه‌هزار. چیزی که دیده نشود، تمیز هم نمی‌شود.
    /// </summary>
    public async Task<IActionResult> Categories(CancellationToken ct)
    {
        ViewData["Title"] = "گروه‌های بانک مخاطبان";

        // ⚠️ اول به نوع بی‌نام و بعد به رکورد — GroupBy با سازندهٔ رکورد در
        // Select ترجمه نمی‌شود.
        var grouped = await db.MarketingContacts
            .GroupBy(c => c.Category)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return View(new MarketingCategoriesVm
        {
            Rows = [.. grouped.Select(x => new ContactCategoryCount(x.Name, x.Count)).OrderByDescending(x => x.Count)],
            Total = await db.MarketingContacts.CountAsync(ct)
        });
    }

    /// <summary>
    /// تغییر نام یک گروه — و چون روی نام یکسان می‌نشیند، همان هم «ادغام» است.
    ///
    /// مقصد خالی یعنی «گروهش را بردار»، نه «حذفش کن»: مخاطب سرِ جایش می‌ماند و
    /// فقط برچسب اشتباهش پاک می‌شود. حذف خودِ مخاطب کار این صفحه نیست.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RenameCategory(string? fromCategory, string? toCategory, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(toCategory) ? null : toCategory.Trim();

        // == fromCategory وقتی تهی است به IS NULL ترجمه نمی‌شود؛ دو مسیر جدا.
        var q = string.IsNullOrWhiteSpace(fromCategory)
            ? db.MarketingContacts.Where(c => c.Category == null || c.Category == "")
            : db.MarketingContacts.Where(c => c.Category == fromCategory);

        var n = await q.ExecuteUpdateAsync(s => s.SetProperty(c => c.Category, target), ct);

        TempData["ok"] = target == null
            ? $"گروهِ {Fa.N(n)} مخاطب برداشته شد."
            : $"{Fa.N(n)} مخاطب به گروه «{target}» منتقل شد.";
        return RedirectToAction(nameof(Categories));
    }

    // ================================================================ ورود

    public IActionResult Import()
    {
        ViewData["Title"] = "ورود مخاطبان";
        return View();
    }

    /// <summary>
    /// ورود از کاربران خود بارگو — بانک را از حساب‌های تأییدشده پر می‌کند.
    ///
    /// چرا کپی و نه خواندن مستقیم از جدول حساب‌ها: کمپین «لغو اشتراک» و «سابقهٔ
    /// ارسال» لازم دارد و آن‌ها روی حساب‌ها جایی ندارند. ادغام روی شماره است و
    /// OptedOut هرگز بازنویسی نمی‌شود — ورود دوباره، لغو اشتراک‌ها را پاک نمی‌کند.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ImportUsers(string? audience, CancellationToken ct)
    {
        List<(string Mobile, string? Name, int? CityId)> rows;
        string category;
        switch (audience)
        {
            case "drivers":
                category = CampaignTemplates.CategoryDrivers;
                rows = (await db.Drivers.AsNoTracking()
                        .Where(d => d.Status == AccountStatus.Approved && d.Mobile != "")
                        .Select(d => new { d.Mobile, Name = d.FirstName + " " + d.LastName, d.CityId })
                        .ToListAsync(ct))
                    .Select(x => (x.Mobile, (string?)x.Name.Trim(), x.CityId)).ToList();
                break;
            case "shippers":
                category = CampaignTemplates.CategoryShippers;
                rows = (await db.Shippers.AsNoTracking()
                        .Where(s => s.Status == AccountStatus.Approved && s.Mobile != "")
                        .Select(s => new { s.Mobile, Name = s.FullName, s.CityId })
                        .ToListAsync(ct))
                    .Select(x => (x.Mobile, (string?)x.Name, x.CityId)).ToList();
                break;
            case "companies":
                category = CampaignTemplates.CategoryCompanies;
                rows = (await db.Companies.AsNoTracking()
                        .Where(c => c.Status == AccountStatus.Approved && c.Mobile != "")
                        .Select(c => new { c.Mobile, c.Name, c.CityId })
                        .ToListAsync(ct))
                    .Select(x => (x.Mobile, (string?)x.Name, x.CityId)).ToList();
                break;
            default:
                TempData["err"] = "مخاطب واردات را انتخاب کنید (رانندگان، صاحبان بار یا شرکت‌ها).";
                return RedirectToAction(nameof(Import));
        }

        // نام شهر و استان یک‌بار خوانده می‌شود؛ نه یک پرس‌وجو برای هر ردیف.
        var cityIds = rows.Where(r => r.CityId != null).Select(r => r.CityId!.Value).Distinct().ToList();
        var cities = await db.Cities.AsNoTracking().Include(c => c.Province)
            .Where(c => cityIds.Contains(c.CityId))
            .ToDictionaryAsync(c => c.CityId, ct);

        var added = 0; var updated = 0; var skipped = 0;
        var seen = new HashSet<string>();
        var mobiles = rows.Select(r => Fa.NormMobile(r.Mobile)).Where(m => m.Length == 11).ToList();
        var existing = await db.MarketingContacts.Where(c => mobiles.Contains(c.Mobile)).ToDictionaryAsync(c => c.Mobile, ct);

        foreach (var r in rows)
        {
            var mobile = Fa.NormMobile(r.Mobile);
            if (mobile.Length != 11 || !seen.Add(mobile)) { skipped++; continue; }

            var city = r.CityId != null && cities.TryGetValue(r.CityId.Value, out var c) ? c : null;
            if (existing.TryGetValue(mobile, out var old))
            {
                // فقط جاهای خالی پر می‌شوند؛ دست چیزی که قبلاً درست بوده نمی‌خورد.
                old.Name ??= r.Name;
                old.Category ??= category;
                old.Province ??= city?.Province?.Name;
                old.City ??= city?.Name;
                updated++;
            }
            else
            {
                db.MarketingContacts.Add(new MarketingContact
                {
                    Mobile = mobile,
                    Name = r.Name,
                    Category = category,
                    Province = city?.Province?.Name,
                    City = city?.Name,
                    Source = "bargo:" + audience
                });
                added++;
            }
        }

        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"{Fa.N(added)} مخاطب تازه از «{category}» وارد شد، {Fa.N(updated)} به‌روزرسانی"
                       + (skipped > 0 ? $"، {Fa.N(skipped)} ردیف نامعتبر یا تکراری رد شد." : ".");
        return RedirectToAction(nameof(Contacts), new { cat = category });
    }

    /// <summary>
    /// ورود CSV — ستون‌ها: mobile,name,category,province,city,address,lat,lng,source
    ///
    /// ادغام روی شماره است: ردیف تکراری به‌روزرسانی می‌شود نه اضافه — و
    /// <see cref="MarketingContact.OptedOut"/> هرگز از فایل بازنویسی نمی‌شود،
    /// وگرنه هر بارگذاری تازه، «دیگر پیام نده»ها را پاک می‌کرد.
    ///
    /// ⚠️ دسته‌ای خوانده و دسته‌ای نوشته می‌شود: روی فایل بزرگ، یک FirstOrDefault
    /// جدا برای هر ردیف یعنی ده‌ها هزار رفت‌وبرگشت به پایگاه‌داده در یک درخواست
    /// وب — و تایم‌اوت پیش از پایان.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(60_000_000)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            TempData["err"] = "فایلی انتخاب نشده است.";
            return RedirectToAction(nameof(Import));
        }

        var added = 0; var updated = 0; var skipped = 0; var line = 0;
        var seen = new HashSet<string>();

        const int batchSize = 1000;
        var buffer = new List<MarketingContact>(batchSize);

        async Task FlushAsync()
        {
            if (buffer.Count == 0) return;

            var mobiles = buffer.Select(b => b.Mobile).ToList();
            var existing = await db.MarketingContacts
                .Where(c => mobiles.Contains(c.Mobile))
                .ToDictionaryAsync(c => c.Mobile, ct);

            foreach (var row in buffer)
            {
                if (existing.TryGetValue(row.Mobile, out var old))
                {
                    // فقط جاهای خالی پر می‌شوند. OptedOut هم عمداً اینجا نیست:
                    // هر بارگذاری وگرنه «دیگر پیام نده»ها را پاک می‌کرد.
                    old.Name ??= row.Name;
                    old.Category ??= row.Category;
                    old.Province ??= row.Province;
                    old.City ??= row.City;
                    old.Address ??= row.Address;
                    old.Lat ??= row.Lat;
                    old.Lng ??= row.Lng;
                    updated++;
                }
                else
                {
                    db.MarketingContacts.Add(row);
                    added++;
                }
            }

            await db.SaveChangesAsync(ct);
            // بدون این، ردیف‌های دسته‌های قبلی در حافظه می‌مانند و هر SaveChanges
            // کندتر و کندتر می‌شود.
            db.ChangeTracker.Clear();
            buffer.Clear();
        }

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string?[] header = [];
        while (await reader.ReadLineAsync(ct) is { } raw)
        {
            line++;
            if (raw.Trim().Length == 0) continue;

            var cells = SplitCsv(raw);
            if (line == 1) { header = [.. cells.Select(c => c.Trim().ToLowerInvariant())]; continue; }

            string? Col(string name)
            {
                var i = Array.IndexOf(header, name);
                return i >= 0 && i < cells.Length && cells[i].Trim().Length > 0 ? cells[i].Trim() : null;
            }

            var mobile = Fa.NormMobile(Col("mobile") ?? "");
            if (mobile.Length != 11 || !seen.Add(mobile)) { skipped++; continue; }

            buffer.Add(new MarketingContact
            {
                Mobile = mobile,
                Name = Col("name"),
                Category = Col("category"),
                Province = Col("province"),
                City = Col("city"),
                Address = Col("address"),
                Lat = double.TryParse(Col("lat"), out var la) ? la : null,
                Lng = double.TryParse(Col("lng"), out var ln) ? ln : null,
                Source = Col("source") ?? file.FileName
            });

            if (buffer.Count >= batchSize) await FlushAsync();
        }

        await FlushAsync();
        TempData["ok"] = $"{Fa.N(added)} مخاطب تازه، {Fa.N(updated)} به‌روزرسانی" +
                         (skipped > 0 ? $"، {Fa.N(skipped)} ردیف نامعتبر یا تکراری رد شد." : ".");
        return RedirectToAction(nameof(Contacts));
    }

    /// <summary>جداکنندهٔ CSV با پشتیبانی از گیومه — آدرس‌ها ویرگول دارند.</summary>
    private static string[] SplitCsv(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { outp.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        outp.Add(sb.ToString());
        return [.. outp];
    }

    // ================================================================ ارسال (زنجیرهٔ چهارگام)
    //
    // Sms (فیلتر و شمار) ← Compose (متن، آزمایشی) ← SmsConfirm (فهرست دقیق و
    // تأیید) ← Send (ثبت و شروع). هیچ گامی جز آخری چیزی نمی‌فرستد، و آخری هم
    // فقط صف را می‌سازد و روشن می‌کند — خودِ ارسال با CampaignSenderJob است تا
    // هزاران گیرنده درخواست وب را نکشد.

    private IQueryable<MarketingContact> Scope(string? cat, string? province, string? city)
    {
        var q = db.MarketingContacts.AsQueryable();
        if (!string.IsNullOrWhiteSpace(cat)) q = q.Where(c => c.Category == cat);
        if (!string.IsNullOrWhiteSpace(province)) q = q.Where(c => c.Province == province);
        if (!string.IsNullOrWhiteSpace(city)) q = q.Where(c => c.City == city);
        return q;
    }

    private IQueryable<MarketingContact> Sendable(string? cat, string? province, string? city, bool excludeSent)
    {
        var q = Scope(cat, province, city).Where(c => !c.OptedOut);
        if (excludeSent) q = q.Where(c => c.SentCount == 0);
        return q;
    }

    private async Task<CampaignFilterVm> FilterVmAsync(string? cat, string? province, string? city, bool excludeSent, CancellationToken ct) => new()
    {
        Cat = cat, Province = province, City = city, ExcludeSent = excludeSent,
        Categories = await CategoriesAsync(ct),
        Provinces = await ProvincesAsync(ct),
        Cities = await CitiesAsync(ct),
        Budget = await CampaignGuards.BudgetAsync(db, settings, ct),
        Cap = SmsCampaign.DefaultCap,
        PriceRial = await settings.GetLongAsync(SettingsService.Keys.SmsCampaignPriceRial, ct),
        SmsReal = await SmsRealAsync(ct),
        CampaignOn = await settings.GetBoolAsync(SettingsService.Keys.SmsCampaignEnabled, ct)
    };

    /// <summary>گام ۱ — فیلتر و «چند نفر واقعاً پیامک می‌گیرند».</summary>
    public async Task<IActionResult> Sms(string? cat, string? province, string? city, bool excludeSent = true, CancellationToken ct = default)
    {
        ViewData["Title"] = "کمپین پیامکی جدید";

        // «چه گروه‌هایی پیام گرفته‌اند» — کمپین‌هایی که واقعاً ارسالی داشته‌اند،
        // به تفکیک همان فیلتر ساخته‌شده. تا مدیر پیش از انتخاب ببیند به کدام
        // گروه قبلاً چه رفته و ناخواسته یک گروه را دو بار بمباران نکند.
        var groupsRaw = await db.SmsCampaigns.AsNoTracking()
            .Where(c => c.SentCount > 0 || c.Status == CampaignStatus.Running)
            .GroupBy(c => new { c.FilterCategory, c.FilterProvince, c.FilterCity })
            .Select(g => new
            {
                g.Key.FilterCategory, g.Key.FilterProvince, g.Key.FilterCity,
                Campaigns = g.Count(), TotalSent = g.Sum(x => x.SentCount),
                LastAt = g.Max(x => x.StartedAt ?? x.CreatedAt)
            })
            .ToListAsync(ct);
        var groups = groupsRaw
            .Select(x => new CampaignGroupHistory(x.FilterCategory, x.FilterProvince, x.FilterCity,
                x.Campaigns, x.TotalSent, x.LastAt))
            .OrderByDescending(x => x.LastAt).ToList();

        // ⚠️ هشدار «بار دوم/سوم»: همین گروهِ انتخاب‌شده قبلاً چند بار کمپین گرفته؟
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        return View(new CampaignSmsVm
        {
            F = await FilterVmAsync(cat, province, city, excludeSent, ct),
            // شکستن عدد عمدی است: «۵۰ هزار ردیف داریم ولی ۰ گیرنده» بدون توضیح
            // شبیه خرابی نرم‌افزار است، در حالی که واقعیت داده است.
            ScopeTotal = await Scope(cat, province, city).CountAsync(ct),
            ScopeOptOut = await Scope(cat, province, city).CountAsync(c => c.OptedOut, ct),
            ScopeAlreadySent = await Scope(cat, province, city).CountAsync(c => !c.OptedOut && c.SentCount > 0, ct),
            Count = await Sendable(cat, province, city, excludeSent).CountAsync(ct),
            SentGroups = groups,
            PriorRounds = groups
                .Where(g => N(g.Category) == N(cat) && N(g.Province) == N(province) && N(g.City) == N(city))
                .Sum(g => g.Campaigns)
        });
    }

    /// <summary>گام ۲ — نوشتن متن. چیزی ارسال نمی‌کند.</summary>
    [HttpPost]
    public async Task<IActionResult> Compose(string? cat, string? province, string? city,
        bool excludeSent = true, string? text = null, CancellationToken ct = default)
    {
        ViewData["Title"] = "نوشتن پیام برای گیرندگان";

        return View("Compose", new CampaignComposeVm
        {
            F = await FilterVmAsync(cat, province, city, excludeSent, ct),
            Count = await Sendable(cat, province, city, excludeSent).CountAsync(ct),
            // متن: یا از «ارسال دوباره» آمده، یا پیش‌نویس دفعهٔ قبل.
            Text = string.IsNullOrWhiteSpace(text)
                ? await settings.GetAsync(SettingsService.Keys.SmsCampaignDraft, ct)
                : text,
            Templates = [.. CampaignTemplates.For(cat)],
            Reserve = OptOutLinks.ReserveChars(SmsCampaign.Host),
            // نمونهٔ واقعی برای پیش‌نمایش — نخستین گیرندهٔ همین فیلتر.
            Sample = await Sendable(cat, province, city, excludeSent)
                .OrderBy(c => c.SentCount).ThenBy(c => c.MarketingContactId)
                .Select(c => new CampaignTarget(c.MarketingContactId, c.Mobile, c.Name, c.Category, c.Province, c.City))
                .FirstOrDefaultAsync(ct)
        });
    }

    /// <summary>ارسال آزمایشی به یک شماره — یک پیامک واقعی با همان متن و بند لغو.</summary>
    [HttpPost]
    public async Task<IActionResult> TestSms(string? testMobile, string? text,
        string? cat, string? province, string? city, bool excludeSent,
        [FromServices] ISmsSender sms, [FromServices] OptOutLinks links, CancellationToken ct = default)
    {
        var mobile = Fa.NormMobile(testMobile);
        if (mobile.Length != 11)
            TempData["err"] = "برای ارسال آزمایشی یک شمارهٔ موبایل معتبر بنویسید.";
        else if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 10)
            TempData["err"] = "متن کوتاه‌تر از ۱۰ نویسه ارسال نمی‌شود.";
        else
        {
            // با بند لغوِ نمونه، تا دقیقاً همان چیزی برسد که گیرندهٔ واقعی می‌گیرد.
            var sample = await Sendable(cat, province, city, excludeSent).FirstOrDefaultAsync(ct);
            var body = CampaignMessage.Render(text, sample?.Name, sample?.City, sample?.Province)
                       + links.Suffix(sample?.MarketingContactId ?? 1, SmsCampaign.Host);
            var ok = await sms.SendAsync(mobile, body, TestPurpose, ct);
            await db.SaveChangesAsync(ct); // ردیف SmsLog ارسال‌کننده
            TempData[ok ? "ok" : "err"] = ok
                ? $"پیامک آزمایشی به {Fa.Digits(mobile)} رفت — همین متن، با بند لغو."
                  + (await SmsRealAsync(ct) ? "" : " (سرویس روی «log» است؛ فقط در لاگ ثبت شد.)")
                : "درگاه پیامک ارسال را نپذیرفت؛ لاگ پیامک را ببینید.";
        }
        // برگشت به همان صفحهٔ نوشتن، با همان متن و فیلتر.
        return await Compose(cat, province, city, excludeSent, text, ct);
    }

    /// <summary>گام ۳ — فهرست دقیق گیرندگان و تأیید.</summary>
    [HttpPost]
    public async Task<IActionResult> SmsConfirm(string? cat, string? province, string? city,
        bool excludeSent = true, string? text = null, CancellationToken ct = default)
    {
        ViewData["Title"] = "تأیید ارسال پیامک";

        var t = (text ?? "").Trim();
        if (t.Length < 10)
        {
            TempData["err"] = "متن پیامک دست‌کم ۱۰ نویسه لازم دارد.";
            return await Compose(cat, province, city, excludeSent, text, ct);
        }

        // متن برای دفعهٔ بعد ذخیره می‌شود — حتی اگر آخرش ارسال نشود.
        await settings.SetAsync(SettingsService.Keys.SmsCampaignDraft, t, ct);

        var targets = await Sendable(cat, province, city, excludeSent)
            .OrderBy(c => c.SentCount).ThenBy(c => c.MarketingContactId)
            .Select(c => new CampaignTarget(c.MarketingContactId, c.Mobile, c.Name, c.Category, c.Province, c.City))
            .ToListAsync(ct);

        var cap = SmsCampaign.DefaultCap;
        var withReserve = SmsCampaign.WithOptOutAllowance(t);
        var m = SmsSegments.Measure(withReserve);
        var hasTokens = CampaignMessage.HasTokens(t);
        // بدترین حالت با جای‌نشان — روی ۳۰۰ ردیف نمونه، نه کل بانک.
        var maxParts = hasTokens
            ? targets.Take(300).Select(r => SmsSegments.Count(
                  SmsCampaign.WithOptOutAllowance(CampaignMessage.Render(t, r.Name, r.City, r.Province))))
              .DefaultIfEmpty(m.Parts).Max()
            : m.Parts;

        return View("SmsConfirm", new CampaignConfirmVm
        {
            F = await FilterVmAsync(cat, province, city, excludeSent, ct),
            Text = t,
            Count = targets.Count,
            WillSend = Math.Min(targets.Count, cap),
            // فهرست تا ۳۰۰ ردیف نشان داده می‌شود؛ بانک ممکن است ده‌ها هزارتایی
            // باشد و مرورگر را بکشد.
            Recipients = [.. targets.Take(300)],
            More = Math.Max(0, targets.Count - 300),
            Parts = m.Parts,
            Unicode = m.Unicode,
            HasTokens = hasTokens,
            MaxParts = maxParts,
            TotalParts = (long)Math.Min(targets.Count, cap) * maxParts,
            // «همین متن قبلاً رفته» — پیش از تأیید، نه بعدش.
            Duplicates = await CampaignGuards.DuplicatesAsync(db, t, exceptCampaignId: 0)
        });
    }

    /// <summary>گام ۴ — ثبت کمپین و شروع ارسال. تنها گامی که اثر واقعی دارد.</summary>
    [HttpPost]
    public async Task<IActionResult> Send(string? cat, string? province, string? city,
        bool excludeSent, string? text, int expected, CancellationToken ct)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 10) return RedirectToAction(nameof(Sms));

        var targets = await Sendable(cat, province, city, excludeSent)
            .OrderBy(c => c.SentCount).ThenBy(c => c.MarketingContactId)
            .Take(SmsCampaign.DefaultCap)
            .Select(c => new { c.MarketingContactId, c.Mobile })
            .ToListAsync(ct);

        var full = await Sendable(cat, province, city, excludeSent).CountAsync(ct);

        // ⚠️ عددی که به مدیر نشان داده شد. اگر تا لحظهٔ کلیک عوض شده باشد
        // (مثلاً واردات تازه)، هیچ‌چیز ارسال نمی‌شود — تأیید «۱۲ گیرنده» نباید
        // بی‌صدا به «۲۶۵ گیرنده» تبدیل شود.
        if (full != expected)
        {
            TempData["err"] = $"شمار گیرندگان از {Fa.N(expected)} به {Fa.N(full)} تغییر کرده — "
                            + "دوباره بررسی و تأیید کنید؛ چیزی ارسال نشد.";
            return await SmsConfirm(cat, province, city, excludeSent, t, ct);
        }
        if (targets.Count == 0)
        {
            TempData["err"] = "گیرنده‌ای نمانده؛ چیزی ارسال نشد.";
            return RedirectToAction(nameof(Sms), new { cat, province, city, excludeSent });
        }

        var campaign = new SmsCampaign
        {
            Title = $"کمپین {Fa.Date(DateTime.UtcNow)} — {(string.IsNullOrWhiteSpace(cat) ? "همهٔ گروه‌ها" : cat)}",
            Body = t,
            FilterCategory = cat,
            FilterProvince = province,
            FilterCity = string.IsNullOrWhiteSpace(city) ? null : city,
            Cap = SmsCampaign.DefaultCap,
            CreatedByAdminId = me.Id,
            // ⚠️ مستقیم «در حال ارسال» — گام تأیید همین حالا طی شد و «پیش‌نویس و
            // بعداً شروع» یعنی دوباره همان تصمیم از مدیر خواسته شود.
            Status = CampaignStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        db.SmsCampaigns.Add(campaign);
        await db.SaveChangesAsync(ct);

        db.SmsCampaignRecipients.AddRange(targets.Select(x => new SmsCampaignRecipient
        {
            CampaignId = campaign.SmsCampaignId, ContactId = x.MarketingContactId, Mobile = x.Mobile
        }));
        campaign.Total = targets.Count;
        audit.Add("SmsCampaign", campaign.SmsCampaignId, "start",
            $"کمپین «{campaign.Title}» با {Fa.N(targets.Count)} گیرنده ثبت و شروع شد",
            new { cat, province, city, excludeSent, recipients = targets.Count });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"کمپین با {Fa.N(targets.Count)} گیرنده ثبت و شروع شد — پیشرفتش را همین‌جا می‌بینید.";
        return RedirectToAction(nameof(Index));
    }

    // ================================================================ کمپین‌ها

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "کمپین‌های پیامکی بانک مخاطبان";

        var rows = await db.SmsCampaigns.OrderByDescending(c => c.SmsCampaignId)
            .Take(50).AsNoTracking().ToListAsync(ct);

        // «به کجا رفت» در یک خط — سه‌تای اول، بقیه شمرده.
        var ids = rows.Select(r => r.SmsCampaignId).ToList();
        var grouped = await db.SmsCampaignRecipients
            .Where(r => ids.Contains(r.CampaignId) && r.Contact != null)
            .GroupBy(r => new { r.CampaignId, r.Contact!.Province, r.Contact.City })
            .Select(g => new { g.Key.CampaignId, g.Key.Province, g.Key.City, Count = g.Count() })
            .ToListAsync(ct);

        var byIds = rows.Select(r => r.CreatedByAdminId).Distinct().ToList();

        return View(new CampaignListVm
        {
            Rows = rows,
            SmsReal = await SmsRealAsync(ct),
            CampaignOn = await settings.GetBoolAsync(SettingsService.Keys.SmsCampaignEnabled, ct),
            OptOutCount = await db.MarketingContacts.CountAsync(c => c.OptedOut, ct),
            By = await db.Admins.Where(a => byIds.Contains(a.AdminId))
                .ToDictionaryAsync(a => a.AdminId, a => a.Name, ct),
            Where = grouped
                .GroupBy(x => x.CampaignId)
                .ToDictionary(g => g.Key,
                    g => g.OrderByDescending(x => x.Count)
                          .Select(x => new CampaignPlaceCount(x.Province, x.City, x.Count)).ToList())
        });
    }

    /// <param name="cat">
    /// گروه ازپیش‌انتخاب‌شده — مثلاً وقتی از «ورود از کاربران» می‌آییم. بدون این،
    /// آن مسیر گروه را در نشانی می‌فرستاد و فرم نادیده‌اش می‌گرفت.
    /// </param>
    public async Task<IActionResult> Create(string? cat, CancellationToken ct)
    {
        ViewData["Title"] = "کمپین تازه";
        return View(new CampaignCreateVm
        {
            Categories = await CategoriesAsync(ct),
            Provinces = await ProvincesAsync(ct),
            Cities = await CitiesAsync(ct),
            Templates = [.. CampaignTemplates.For(cat)],
            All = await db.MarketingContacts.CountAsync(c => !c.OptedOut, ct),
            PriceRial = await settings.GetLongAsync(SettingsService.Keys.SmsCampaignPriceRial, ct),
            Reserve = OptOutLinks.ReserveChars(SmsCampaign.Host),
            Cat = cat
        });
    }

    /// <summary>
    /// شمار زندهٔ گیرنده‌ها برای فیلتر فعلی — صفحهٔ ساخت با هر تغییر فیلتر این
    /// را صدا می‌زند، تا عدد *پیش از* ساختن دیده شود نه بعدش.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> CountTargets(string? cat, string? province, string? city, string? state, CancellationToken ct)
    {
        var q = Filtered(null, cat, province, state).Where(c => !c.OptedOut);
        if (!string.IsNullOrWhiteSpace(city)) q = q.Where(c => c.City == city);

        return Json(new
        {
            count = await q.CountAsync(ct),
            bank = await db.MarketingContacts.CountAsync(c => !c.OptedOut, ct)
        });
    }

    /// <summary>
    /// ساخت کمپین و صفش. کمپین در حالت پیش‌نویس می‌ماند — هیچ پیامکی تا زدن
    /// «شروع» نمی‌رود.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(string? title, string? body, string? cat, string? province,
        string? city, string? state, int cap = SmsCampaign.DefaultCap, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
        {
            TempData["err"] = "عنوان و متن پیامک لازم است.";
            return RedirectToAction(nameof(Create));
        }
        if (body.Length > 500)
        {
            TempData["err"] = "متن پیامک حداکثر ۵۰۰ نویسه است.";
            return RedirectToAction(nameof(Create));
        }
        if (cap < 1) cap = 1;

        // ⚠️ گیرنده‌ها *پیش از* ساختن کمپین شمرده می‌شوند: اگر فیلتر به کسی
        // نمی‌خورد، نباید کمپین صفرگیرنده‌ای در آرشیو بنشیند که پاک هم نمی‌شود.
        //
        // صف: فقط کسانی که لغو نکرده‌اند، به ترتیب «کمترین پیامکِ گرفته» تا اگر
        // سقف نگذارد همه بروند، بار بعد سراغ همان‌ها که قبلاً گرفته‌اند نرویم.
        var query = Filtered(null, cat, province, state).Where(c => !c.OptedOut);
        if (!string.IsNullOrWhiteSpace(city)) query = query.Where(c => c.City == city);

        var targets = await query
            .OrderBy(c => c.SentCount).ThenBy(c => c.MarketingContactId)
            .Take(cap)
            .Select(c => new { c.MarketingContactId, c.Mobile })
            .ToListAsync(ct);

        if (targets.Count == 0)
        {
            // چرا کسی پیدا نشد، مهم‌تر از اینکه پیدا نشد: بانک خالی و فیلتر تنگ
            // دو مشکل کاملاً متفاوت‌اند با دو راه حل متفاوت.
            var bank = await db.MarketingContacts.CountAsync(c => !c.OptedOut, ct);
            TempData["err"] = bank == 0
                ? "بانک مخاطبان خالی است، پس کمپین ساخته نشد. از «ورود مخاطبان» کاربران بارگو یا فایل CSV وارد کنید."
                : "با این فیلترها کسی پیدا نشد، پس کمپین ساخته نشد — "
                  + $"بانک {Fa.N(bank)} مخاطب فعال دارد. فیلتر «وضعیت» را روی «هر وضعیتی» بگذارید یا گروه/استان را بازتر کنید.";
            return RedirectToAction(nameof(Create), new { cat });
        }

        var campaign = new SmsCampaign
        {
            Title = title.Trim(),
            Body = body.Trim(),
            FilterCategory = cat,
            FilterProvince = province,
            FilterCity = string.IsNullOrWhiteSpace(city) ? null : city,
            Cap = cap,
            CreatedByAdminId = me.Id
        };
        db.SmsCampaigns.Add(campaign);
        await db.SaveChangesAsync(ct);

        db.SmsCampaignRecipients.AddRange(targets.Select(t => new SmsCampaignRecipient
        {
            CampaignId = campaign.SmsCampaignId,
            ContactId = t.MarketingContactId,
            Mobile = t.Mobile
        }));
        campaign.Total = targets.Count;
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"کمپین ساخته شد با {Fa.N(targets.Count)} گیرنده — هنوز چیزی ارسال نشده.";
        return RedirectToAction(nameof(Details), new { id = campaign.SmsCampaignId });
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var campaign = await db.SmsCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.SmsCampaignId == id, ct);
        if (campaign == null) return NotFound();
        ViewData["Title"] = campaign.Title;

        // پروندهٔ کمپین: «به کجا رفت»، اجراکننده و فهرست گیرندگان با وضعیت.
        var places = (await db.SmsCampaignRecipients
            .Where(r => r.CampaignId == id && r.Contact != null)
            .GroupBy(r => new { r.Contact!.Province, r.Contact.City })
            .Select(g => new { g.Key.Province, g.Key.City, Count = g.Count() })
            .OrderByDescending(x => x.Count).Take(30).ToListAsync(ct))
            .Select(x => new CampaignPlaceCount(x.Province, x.City, x.Count)).ToList();

        var recRows = await db.SmsCampaignRecipients
            .Where(r => r.CampaignId == id)
            .OrderBy(r => r.SmsCampaignRecipientId).Take(300)
            .Select(r => new
            {
                r.Mobile, r.Status, r.Error,
                Name = r.Contact != null ? r.Contact.Name : null,
                Province = r.Contact != null ? r.Contact.Province : null,
                City = r.Contact != null ? r.Contact.City : null
            })
            .ToListAsync(ct);

        return View(new CampaignDetailsVm
        {
            C = campaign,
            PriceRial = await settings.GetLongAsync(SettingsService.Keys.SmsCampaignPriceRial, ct),
            Budget = await CampaignGuards.BudgetAsync(db, settings, ct),
            Duplicates = await CampaignGuards.DuplicatesAsync(db, campaign.Body, campaign.SmsCampaignId),
            Places = places,
            By = await db.Admins.Where(a => a.AdminId == campaign.CreatedByAdminId)
                .Select(a => a.Name).FirstOrDefaultAsync(ct),
            Recipients = [.. recRows.Select(r => new CampaignRecipientRow(r.Mobile, r.Status, r.Error, r.Name, r.Province, r.City))],
            Pending = await db.SmsCampaignRecipients
                .CountAsync(r => r.CampaignId == id && r.Status == CampaignRecipientStatus.Pending, ct),
            Skipped = await db.SmsCampaignRecipients
                .CountAsync(r => r.CampaignId == id && r.Status == CampaignRecipientStatus.Skipped, ct),
            Failures = await db.SmsCampaignRecipients
                .Where(r => r.CampaignId == id && r.Status == CampaignRecipientStatus.Failed)
                .OrderByDescending(r => r.SmsCampaignRecipientId).Take(20).AsNoTracking().ToListAsync(ct),
            SmsReal = await SmsRealAsync(ct),
            CampaignOn = await settings.GetBoolAsync(SettingsService.Keys.SmsCampaignEnabled, ct)
        });
    }

    /// <summary>
    /// شروع ارسال. confirm باید دقیقاً برابر تعداد گیرنده تایپ شود.
    ///
    /// این «سختگیری الکی» نیست: تفاوت ۵۰۰ و ۵۰۰۰ گیرنده در یک دکمهٔ تأیید دیده
    /// نمی‌شود ولی در صورتحساب پیامک چند میلیون تومان است. تایپ عدد یعنی مدیر
    /// واقعاً نگاهش کرده.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Start(int id, int confirm, CancellationToken ct)
    {
        var campaign = await db.SmsCampaigns.FirstOrDefaultAsync(c => c.SmsCampaignId == id, ct);
        if (campaign == null) return NotFound();

        if (campaign.Status is not (CampaignStatus.Draft or CampaignStatus.Paused))
        {
            TempData["err"] = "این کمپین در وضعیتی نیست که بشود شروعش کرد.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (confirm != campaign.Total)
        {
            TempData["err"] = $"برای شروع باید عدد {Fa.N(campaign.Total)} را دقیقاً تایپ کنید.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (!await settings.GetBoolAsync(SettingsService.Keys.SmsCampaignEnabled, ct))
        {
            TempData["err"] = "کلید «کمپین پیامکی» در «تنظیمات پیامک» بسته است — پیش‌فرضش خاموش است و باید خودتان بازش کنید.";
            return RedirectToAction(nameof(Details), new { id });
        }

        campaign.Status = CampaignStatus.Running;
        campaign.StartedAt ??= DateTime.UtcNow;
        audit.Add("SmsCampaign", id, "start", $"کمپین «{campaign.Title}» با {Fa.N(campaign.Total)} گیرنده شروع شد");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "کمپین شروع شد — ارسال در پس‌زمینه انجام می‌شود و می‌توانید همین‌جا پیشرفتش را ببینید.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public Task<IActionResult> Pause(int id, CancellationToken ct) => SetStatus(id, CampaignStatus.Paused,
        "کمپین متوقف شد؛ ارسالِ همین لحظه تمام می‌شود و بقیه در صف می‌مانند.", ct);

    [HttpPost]
    public Task<IActionResult> Cancel(int id, CancellationToken ct) => SetStatus(id, CampaignStatus.Cancelled,
        "کمپین لغو شد. آنچه رفته رفته است و بقیه هرگز ارسال نمی‌شوند.", ct);

    private async Task<IActionResult> SetStatus(int id, string status, string message, CancellationToken ct)
    {
        var campaign = await db.SmsCampaigns.FirstOrDefaultAsync(c => c.SmsCampaignId == id, ct);
        if (campaign == null) return NotFound();

        if (campaign.Status is CampaignStatus.Done or CampaignStatus.Cancelled)
        {
            TempData["err"] = "این کمپین تمام شده است.";
            return RedirectToAction(nameof(Details), new { id });
        }

        campaign.Status = status;
        if (status == CampaignStatus.Cancelled) campaign.FinishedAt = DateTime.UtcNow;
        audit.Add("SmsCampaign", id, status, $"کمپین «{campaign.Title}» {CampaignStatus.Label(status)}");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = message;
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// یک پیامک آزمایشی به شمارهٔ خودِ مدیر — پیش از سوزاندن اعتبار روی هزاران
    /// نفر. از صف کمپین نمی‌گذرد و در آمار هم نمی‌آید.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Test(int id, string? mobile, [FromServices] ISmsSender sms, CancellationToken ct)
    {
        var campaign = await db.SmsCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.SmsCampaignId == id, ct);
        if (campaign == null) return NotFound();

        var to = Fa.NormMobile(mobile);
        if (to.Length != 11)
        {
            TempData["err"] = "شمارهٔ آزمایشی معتبر نیست.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var ok = await sms.SendAsync(to, campaign.Body, TestPurpose, ct);
        await db.SaveChangesAsync(ct); // ردیف SmsLog ارسال‌کننده
        TempData[ok ? "ok" : "err"] = ok
            ? $"پیامک آزمایشی به {Fa.Digits(to)} ارسال شد."
            : "ارسال آزمایشی ناموفق بود — پیش از شروع کمپین علتش را پیدا کنید.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
