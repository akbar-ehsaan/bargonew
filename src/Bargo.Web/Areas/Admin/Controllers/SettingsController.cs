using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Bargo.Web.Areas.AdminPanel.Backoffice;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using AdminEntity = Bargo.Web.Models.Entities.Admin;
// اکشنِ «Roles» (نقش‌ها و دسترسی‌ها) نام کلاس ثابت‌های نقش را می‌پوشاند
using RoleNames = Bargo.Web.Models.Entities.Roles;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// تنظیمات سیستم: نقش‌ها و دسترسی‌ها، پیامک، درگاه، نقشه، بارنامه، مالی، لاگ و نسخه.
///
/// صفحه‌های گروهی (پیامک، درگاه، …) همه یک ویو (Group) دارند: هر کلیدِ
/// <see cref="SettingsService.Defaults"/> با گروهِ مربوطه یک فیلد می‌شود و نوعش از
/// مقدار پیش‌فرض و نام کلید حدس زده می‌شود (true/false → چک‌باکس، «Rial» → تومان،
/// عدد → عددی، «Key»/«Merchant» → رمز که فقط چهار نویسهٔ آخرش دیده می‌شود).
/// افزودن تنظیم تازه فقط یک ردیف در Defaults است؛ اینجا چیزی اضافه نمی‌شود.
/// </summary>
[Area("Admin")]
[Authorize(Roles = RoleNames.Admin)]
[RequireAdminPermission(AdminPermission.Settings)]
public class SettingsController(BargoDbContext db, SettingsService settings, AuditService audit, CurrentUser me, IWebHostEnvironment env) : Controller
{
    private const int MinPassword = 8;

    public IActionResult Index() => RedirectToAction(nameof(Roles));

    // ------------------------------------------------------------------
    //  نقش‌ها و دسترسی‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Roles(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "نقش‌ها و دسترسی‌ها";
        var rows = await db.Admins.AsNoTracking()
            .OrderByDescending(a => a.IsSuper).ThenByDescending(a => a.IsActive).ThenBy(a => a.Name)
            .Select(a => new AdminRowVm
            {
                AdminId = a.AdminId, Name = a.Name, Mobile = a.Mobile, IsSuper = a.IsSuper,
                Permissions = a.Permissions, IsActive = a.IsActive, LastLoginAt = a.LastLoginAt, CreatedAt = a.CreatedAt
            }).ToListAsync(ct);
        return View(new RolesVm
        {
            Rows = rows,
            Edit = edit is int id ? rows.FirstOrDefault(r => r.AdminId == id) : null,
            MeId = me.Id,
            MeSuper = rows.Any(r => r.AdminId == me.Id && r.IsSuper),
            ActiveSupers = rows.Count(r => r.IsSuper && r.IsActive)
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveAdmin(int? id, string? name, string? mobile, string? password, bool isSuper, bool isActive, int[]? perms, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Roles), new { edit = id });
        name = (name ?? "").Trim();
        mobile = Fa.NormMobile(mobile);
        if (name.Length is < 2 or > 100) { TempData["err"] = "نام مدیر ۲ تا ۱۰۰ نویسه باشد."; return back; }
        if (mobile.Length == 0) { TempData["err"] = "شمارهٔ موبایل معتبر نیست (۰۹xxxxxxxxx)."; return back; }
        if (await db.Admins.AnyAsync(a => a.Mobile == mobile && a.AdminId != (id ?? 0), ct)) { TempData["err"] = "این شماره برای مدیر دیگری ثبت شده است."; return back; }

        var meRow = await db.Admins.AsNoTracking().FirstOrDefaultAsync(a => a.AdminId == me.Id, ct);
        var meSuper = meRow?.IsSuper ?? false;
        var permissions = (AdminPermission)(perms ?? []).Where(p => Enum.IsDefined(typeof(AdminPermission), p)).Aggregate(0, (acc, p) => acc | p);

        AdminEntity? row = null;
        if (id is int aid)
        {
            row = await db.Admins.FirstOrDefaultAsync(a => a.AdminId == aid, ct);
            if (row is null) return NotFound();
        }

        // مدیر غیرارشد نه مدیر ارشد می‌سازد و نه اختیار «تنظیمات» را به کسی می‌دهد
        if (!meSuper && (isSuper || (row?.IsSuper ?? false)))
        { TempData["err"] = "فقط مدیر ارشد می‌تواند مدیر ارشد تعریف یا ویرایش کند."; return back; }
        if (!meSuper && permissions.HasFlag(AdminPermission.Settings) && !(row?.Permissions.HasFlag(AdminPermission.Settings) ?? false))
        { TempData["err"] = "فقط مدیر ارشد می‌تواند اختیار «تنظیمات» را تفویض کند."; return back; }

        if (row is not null && row.AdminId == me.Id)
        {
            // خودتان را از دسترسی نیندازید — این دو از فرم خوانده نمی‌شوند
            isSuper = row.IsSuper;
            isActive = row.IsActive;
        }
        if (row is not null && row.IsSuper && row.IsActive && (!isSuper || !isActive))
        {
            var others = await db.Admins.CountAsync(a => a.IsSuper && a.IsActive && a.AdminId != row.AdminId, ct);
            if (others == 0) { TempData["err"] = $"«{row.Name}» تنها مدیر ارشد فعال است؛ پیش از تغییر، مدیر ارشد دیگری تعریف کنید."; return back; }
        }

        if (row is null || !string.IsNullOrEmpty(password))
        {
            if (string.IsNullOrEmpty(password) || password.Length < MinPassword)
            { TempData["err"] = $"گذرواژه دست‌کم {Fa.N(MinPassword)} نویسه باشد."; return back; }
        }

        var before = row is null ? null : new { row.Name, row.Mobile, row.IsSuper, Permissions = row.Permissions.ToString(), row.IsActive };
        row ??= db.Admins.Add(new AdminEntity()).Entity;
        row.Name = name;
        row.Mobile = mobile;
        row.IsSuper = isSuper;
        row.Permissions = isSuper ? AdminPermission.All : permissions;
        row.IsActive = isActive;
        if (!string.IsNullOrEmpty(password)) row.PassHash = PasswordHasher.Hash(password);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Admin", row.AdminId, before is null ? "create" : "update",
            $"مدیر «{name}» ({mobile}): {(isSuper ? "مدیر ارشد" : PermLabels(row.Permissions))}{(isActive ? "" : " — غیرفعال")}{(before is not null && !string.IsNullOrEmpty(password) ? " — گذرواژه عوض شد" : "")}",
            new { before, after = new { name, mobile, isSuper, permissions = row.Permissions.ToString(), isActive } });

        TempData["ok"] = $"مدیر «{name}» ذخیره شد.";
        return RedirectToAction(nameof(Roles));
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(int id, string? password, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Roles), new { edit = id });
        if (string.IsNullOrEmpty(password) || password.Length < MinPassword) { TempData["err"] = $"گذرواژهٔ تازه دست‌کم {Fa.N(MinPassword)} نویسه باشد."; return back; }
        var row = await db.Admins.FirstOrDefaultAsync(a => a.AdminId == id, ct);
        if (row is null) return NotFound();
        var meSuper = await db.Admins.AnyAsync(a => a.AdminId == me.Id && a.IsSuper, ct);
        if (row.IsSuper && !meSuper && row.AdminId != me.Id) { TempData["err"] = "گذرواژهٔ مدیر ارشد را فقط خودش یا مدیر ارشد دیگری عوض می‌کند."; return back; }

        row.PassHash = PasswordHasher.Hash(password);
        audit.Add("Admin", id, "reset_password", $"بازنشانی گذرواژهٔ مدیر «{row.Name}»");
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"گذرواژهٔ «{row.Name}» عوض شد.";
        return RedirectToAction(nameof(Roles));
    }

    [HttpPost]
    public async Task<IActionResult> ToggleAdmin(int id, CancellationToken ct)
    {
        var row = await db.Admins.FirstOrDefaultAsync(a => a.AdminId == id, ct);
        if (row is null) return NotFound();
        if (row.AdminId == me.Id) { TempData["err"] = "حساب خودتان را نمی‌توانید غیرفعال کنید."; return RedirectToAction(nameof(Roles)); }
        var meSuper = await db.Admins.AnyAsync(a => a.AdminId == me.Id && a.IsSuper, ct);
        if (row.IsSuper && !meSuper) { TempData["err"] = "فقط مدیر ارشد می‌تواند وضعیت مدیر ارشد را تغییر دهد."; return RedirectToAction(nameof(Roles)); }
        if (row.IsSuper && row.IsActive && !await db.Admins.AnyAsync(a => a.IsSuper && a.IsActive && a.AdminId != id, ct))
        { TempData["err"] = $"«{row.Name}» تنها مدیر ارشد فعال است و غیرفعال نمی‌شود."; return RedirectToAction(nameof(Roles)); }

        row.IsActive = !row.IsActive;
        audit.Add("Admin", id, row.IsActive ? "activate" : "deactivate", $"{(row.IsActive ? "فعال‌سازی" : "غیرفعال‌سازی")} مدیر «{row.Name}»");
        await db.SaveChangesAsync(ct);
        TempData["ok"] = row.IsActive ? $"«{row.Name}» فعال شد." : $"«{row.Name}» غیرفعال شد و از این لحظه وارد پنل نمی‌شود.";
        return RedirectToAction(nameof(Roles));
    }

    private static string PermLabels(AdminPermission p)
    {
        var list = AdminPermissionLabels.All.Where(x => p.HasFlag(x.Flag)).Select(x => x.Label).ToList();
        return list.Count == 0 ? "بدون دسترسی" : string.Join("، ", list);
    }

    // ------------------------------------------------------------------
    //  گروه‌های تنظیمات
    // ------------------------------------------------------------------

    private sealed record GroupDef(string Group, string Title, string Icon, string? Hint, string Back);

    private static readonly GroupDef[] GroupDefs =
    [
        new("sms", "تنظیمات پیامک", "bi-chat-square-text", "کد یکبارمصرف، اعلان‌ها و پیامک گروهی از این سرویس فرستاده می‌شود. مقدار «log» یعنی فقط در لاگ ثبت شود و چیزی ارسال نشود.", "Sms"),
        new("gateway", "درگاه پرداخت", "bi-credit-card", "شارژ کیف پول و پرداخت کرایه از این درگاه می‌گذرد. تا وقتی «حالت آزمایشی» روشن است هیچ پولی جابه‌جا نمی‌شود.", "Gateway"),
        new("map", "نقشه و GPS", "bi-geo", "کاشی نقشه، کلید سرویس نشان و بازهٔ ارسال موقعیت راننده.", "Map"),
        new("waybill", "تنظیمات بارنامه و سفر", "bi-file-earmark-text", "الزام بارنامه، تحویل با کد و مهلت پیشنهادها.", "Waybill"),
        new("documents", "مدارک و انقضا", "bi-patch-check", "چند روز پیش از انقضا هشدار بدهیم و آیا مدرک منقضی پذیرش بار را ببندد.", "Waybill"),
        new("finance", "تنظیمات مالی", "bi-cash", "کمیسیون و مالیات را از «تعرفه و کمیسیون» هم می‌توان تغییر داد؛ اینجا همان کلیدهاست.", "Finance"),
        new("version", "نسخهٔ اپ و تماس", "bi-git", "اپ موبایل با «حداقل نسخهٔ مجاز» مقایسه می‌شود و نسخه‌های قدیمی‌تر باید به‌روزرسانی کنند.", "Version"),
    ];

    private static GroupDef? Def(string group) => GroupDefs.FirstOrDefault(g => g.Group == group);

    private static bool IsSecret(string key) => key.Contains("Key", StringComparison.Ordinal) || key.Contains("Merchant", StringComparison.Ordinal);

    private static string TypeOf(string key, string @default)
    {
        if (@default is "true" or "false") return "bool";
        if (key.EndsWith("Rial", StringComparison.Ordinal)) return "money";
        if (decimal.TryParse(@default, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return "number";
        return "text";
    }

    private async Task<SettingsGroupVm> GroupVmAsync(string group, CancellationToken ct)
    {
        var def = Def(group)!;
        var fields = new List<SettingFieldVm>();
        foreach (var (key, label, value) in await settings.GroupAsync(group, ct))
        {
            var type = TypeOf(key, SettingsService.Defaults[key].Default);
            var secret = IsSecret(key);
            var shown = type switch
            {
                "money" => long.TryParse(Fa.Latin(value), out var rial) ? (rial / 10).ToString(CultureInfo.InvariantCulture) : "0",
                _ => secret ? BackofficeFormat.Mask(value) : value
            };
            fields.Add(new SettingFieldVm { Key = key, Label = label, Value = shown, Type = type, Secret = secret });
        }
        return new SettingsGroupVm { Group = group, Title = def.Title, Icon = def.Icon, Hint = def.Hint, Back = def.Back, Fields = fields };
    }

    private async Task<IActionResult> GroupPageAsync(string title, string icon, string? subtitle, string[] groups, CancellationToken ct)
    {
        ViewData["Title"] = title;
        var vm = new SettingsPageVm { Title = title, Icon = icon, Subtitle = subtitle };
        foreach (var g in groups) vm.Groups.Add(await GroupVmAsync(g, ct));
        return View("Group", vm);
    }

    public Task<IActionResult> Sms(CancellationToken ct) =>
        GroupPageAsync("تنظیمات پیامک", "bi-chat-square-text", "سرویس ارسال پیامک، کلید و شماره خط.", ["sms"], ct);

    public Task<IActionResult> Gateway(CancellationToken ct) =>
        GroupPageAsync("درگاه پرداخت", "bi-credit-card", "درگاه بانکی، شناسهٔ پذیرنده و حالت آزمایشی.", ["gateway"], ct);

    public Task<IActionResult> Map(CancellationToken ct) =>
        GroupPageAsync("نقشه و GPS", "bi-geo", "کاشی نقشه، کلید نشان و بازهٔ ارسال موقعیت.", ["map"], ct);

    public Task<IActionResult> Waybill(CancellationToken ct) =>
        GroupPageAsync("تنظیمات بارنامه", "bi-file-earmark-text", "قواعد بارنامه و تحویل، و هشدار انقضای مدارک.", ["waybill", "documents"], ct);

    public Task<IActionResult> Finance(CancellationToken ct) =>
        GroupPageAsync("تنظیمات مالی", "bi-cash", "کمیسیون، حداقل برداشت و مالیات بر ارزش افزوده.", ["finance"], ct);

    /// <summary>
    /// ذخیرهٔ یک گروه: فقط کلیدهای همان گروه از فرم خوانده می‌شوند (نه هر چیزی که
    /// فرستاده شده) و فقط مقدارهای تغییرکرده نوشته و در حسابرسی ثبت می‌شوند.
    /// رمزها اگر با همان ماسکِ نمایش‌داده‌شده برگردند دست نمی‌خورند.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save(string group, CancellationToken ct)
    {
        var def = Def(group ?? "");
        if (def is null) return NotFound();
        var back = RedirectToAction(def.Back);
        var form = Request.Form;

        var changes = new List<(string Key, string Label, string Before, string After)>();
        foreach (var (key, label, current) in await settings.GroupAsync(group!, ct))
        {
            var type = TypeOf(key, SettingsService.Defaults[key].Default);
            var raw = form.TryGetValue(key, out var vals) ? vals : StringValues.Empty;
            string next;
            switch (type)
            {
                case "bool":
                    // چک‌باکس + فیلد پنهانِ false: «true,false» یعنی روشن
                    next = raw.Any(v => v == "true") ? "true" : "false";
                    break;
                case "money":
                {
                    var rial = Fa.ParseToman(raw.ToString().Split(',')[0]);
                    if (rial is null || rial.Value < 0) { TempData["err"] = $"«{label}» را به تومان و بدون علامت وارد کنید."; return back; }
                    next = rial.Value.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                case "number":
                {
                    var n = Fa.Latin(raw.ToString()).Replace('٫', '.');
                    if (!decimal.TryParse(n, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d < 0)
                    { TempData["err"] = $"«{label}» باید عددی نامنفی باشد."; return back; }
                    next = d.ToString("0.###", CultureInfo.InvariantCulture);
                    break;
                }
                default:
                {
                    next = raw.ToString().Trim();
                    if (IsSecret(key) && (next.StartsWith("••••", StringComparison.Ordinal) || (next.Length == 0 && current.Length > 0 && !form.ContainsKey(key + "__clear"))))
                        next = current;
                    if (next.Length > 500) { TempData["err"] = $"«{label}» بیش از حد بلند است."; return back; }
                    break;
                }
            }
            if (next != current) changes.Add((key, label, current, next));
        }

        if (changes.Count == 0) { TempData["ok"] = "چیزی تغییر نکرد."; return back; }

        foreach (var c in changes) await settings.SetAsync(c.Key, c.After, ct);

        // مقدار رمزها در حسابرسی هم ماسک می‌شود — لاگ نباید کلید API را نگه دارد
        await audit.LogAsync("Setting", 0, "group:" + group, $"{def.Title}: {string.Join("، ", changes.Select(c => c.Label))}",
            new
            {
                group,
                changes = changes.Select(c => new
                {
                    c.Key,
                    before = IsSecret(c.Key) ? BackofficeFormat.Mask(c.Before) : c.Before,
                    after = IsSecret(c.Key) ? BackofficeFormat.Mask(c.After) : c.After
                })
            });

        TempData["ok"] = $"{Fa.N(changes.Count)} تنظیم ذخیره شد.";
        return back;
    }

    // ------------------------------------------------------------------
    //  لاگ سیستم
    // ------------------------------------------------------------------

    public async Task<IActionResult> Logs(string tab = "audit", string? entity = null, string? action = null, string? actor = null,
        string? mobile = null, string? purpose = null, DateTime? from = null, DateTime? to = null, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "لاگ سیستم";
        tab = tab == "sms" ? "sms" : "audit";
        DateTime? fromUtc = from is DateTime f ? BackofficeLookup.DayStartUtc(f) : null;
        DateTime? toUtc = to is DateTime t ? BackofficeLookup.DayStartUtc(t).AddDays(1) : null;

        if (tab == "sms")
        {
            mobile = Fa.Latin(mobile);
            purpose = AdminOps.Term(purpose);
            var q = db.SmsLogs.AsNoTracking();
            if (mobile.Length > 0) q = q.Where(s => s.Mobile.Contains(mobile));
            if (purpose.Length > 0) q = q.Where(s => s.Purpose == purpose);
            if (fromUtc is DateTime sf) q = q.Where(s => s.CreatedAt >= sf);
            if (toUtc is DateTime st) q = q.Where(s => s.CreatedAt < st);
            return View(new LogsVm
            {
                Tab = tab,
                Sms = await PageVm<SmsLog>.FromAsync(q.OrderByDescending(s => s.SmsLogId), page, PageLink.For(Request), ct: ct),
                Mobile = mobile.Length == 0 ? null : mobile,
                Purpose = purpose.Length == 0 ? null : purpose,
                From = from, To = to
            });
        }

        entity = AdminOps.Term(entity);
        action = AdminOps.Term(action);
        actor = AdminOps.Term(actor);
        var a = db.AuditLogs.AsNoTracking();
        if (entity.Length > 0) a = a.Where(x => x.Entity == entity);
        if (action.Length > 0) a = a.Where(x => x.Action.Contains(action));
        if (actor.Length > 0) a = a.Where(x => x.ActorName != null && x.ActorName.Contains(actor));
        if (fromUtc is DateTime af) a = a.Where(x => x.CreatedAt >= af);
        if (toUtc is DateTime at) a = a.Where(x => x.CreatedAt < at);

        return View(new LogsVm
        {
            Tab = tab,
            Audit = await PageVm<AuditLog>.FromAsync(a.OrderByDescending(x => x.AuditLogId), page, PageLink.For(Request), ct: ct),
            Entities = await db.AuditLogs.AsNoTracking().Select(x => x.Entity).Distinct().OrderBy(x => x).ToListAsync(ct),
            Entity = entity.Length == 0 ? null : entity,
            ActionName = action.Length == 0 ? null : action,
            Actor = actor.Length == 0 ? null : actor,
            From = from, To = to
        });
    }

    // ------------------------------------------------------------------
    //  نسخه و به‌روزرسانی
    // ------------------------------------------------------------------

    public async Task<IActionResult> Version(CancellationToken ct)
    {
        ViewData["Title"] = "نسخه و به‌روزرسانی";
        var asm = typeof(BargoDbContext).Assembly;
        List<string> applied = [], pending = [];
        string? dbName = null;
        try
        {
            dbName = db.Database.GetDbConnection().Database;
            applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TempData["err"] = "اتصال به پایگاه‌داده برای خواندن فهرست مهاجرت‌ها برقرار نشد.";
        }

        return View(new VersionVm
        {
            AppVersion = asm.GetName().Version?.ToString(3) ?? "—",
            InformationalVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            EnvironmentName = env.EnvironmentName,
            Runtime = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            DatabaseName = dbName,
            Applied = applied,
            Pending = pending,
            Form = await GroupVmAsync("version", ct)
        });
    }
}
