using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.CompanyPanel.Controllers;

/// <summary>
/// «کاربران شرکت» — تعریف کاربر، تعیین نقش (عنوان و فعال/غیرفعال) و تعیین سطح دسترسی.
///
/// عنوان (dispatcher، accountant، …) فقط برچسب است؛ دسترسی واقعی از پرچم‌های
/// <see cref="CompanyPermission"/> خوانده می‌شود (Filters/AccessFilters.cs). مدیر شرکت
/// (IsOwner) همیشه همهٔ دسترسی‌ها را دارد و نه غیرفعال می‌شود نه دسترسی‌اش کم می‌شود؛
/// وگرنه شرکت می‌توانست بی‌مدیر بماند.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Users)]
public class UsersController(BargoDbContext db, CurrentUser me, AuditService audit) : Controller
{
    private const int MaxUsers = 50;

    // ------------------------------------------------------------------
    //  فهرست و نقش
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "کاربران شرکت";
        var cid = me.CompanyId;
        var rows = await db.CompanyUsers.AsNoTracking().Where(u => u.CompanyId == cid)
            .OrderByDescending(u => u.IsOwner).ThenBy(u => u.Name)
            .Select(u => new CompanyUserRow
            {
                CompanyUserId = u.CompanyUserId, Name = u.Name, Mobile = u.Mobile, Title = u.Title,
                Permissions = u.Permissions, IsOwner = u.IsOwner, IsActive = u.IsActive,
                LastLoginAt = u.LastLoginAt, CreatedAt = u.CreatedAt
            }).ToListAsync(ct);

        ViewBag.ActiveCount = rows.Count(r => r.IsActive);
        ViewBag.MaxUsers = MaxUsers;
        return View(rows);
    }

    [HttpPost]
    public async Task<IActionResult> ToggleActive(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var u = await db.CompanyUsers.FirstOrDefaultAsync(x => x.CompanyUserId == id && x.CompanyId == cid, ct);
        if (u is null) return NotFound();

        if (u.IsActive)
        {
            if (u.CompanyUserId == me.Id)
            {
                TempData["err"] = "نمی‌توانید حساب خودتان را غیرفعال کنید.";
                return RedirectToAction(nameof(Index));
            }
            if (u.IsOwner)
            {
                TempData["err"] = "حساب مدیر شرکت غیرفعال نمی‌شود.";
                return RedirectToAction(nameof(Index));
            }
        }

        u.IsActive = !u.IsActive;
        audit.Add("CompanyUser", u.CompanyUserId, u.IsActive ? "activate" : "deactivate",
            $"{(u.IsActive ? "فعال‌سازی" : "غیرفعال‌سازی")} کاربر «{u.Name}»", new { u.CompanyId, u.Mobile, u.Title });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = u.IsActive
            ? $"حساب «{u.Name}» فعال شد و می‌تواند وارد پنل شود."
            : $"حساب «{u.Name}» غیرفعال شد؛ در اولین درخواست بعدی از پنل بیرون می‌افتد.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetTitle(int id, string? title, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var u = await db.CompanyUsers.FirstOrDefaultAsync(x => x.CompanyUserId == id && x.CompanyId == cid, ct);
        if (u is null) return NotFound();
        if (u.IsOwner)
        {
            TempData["err"] = "عنوان مدیر شرکت تغییر نمی‌کند.";
            return RedirectToAction(nameof(Index));
        }
        if (title is null || !CompanyLabels.AssignableTitles.Contains(title))
        {
            TempData["err"] = "عنوان انتخاب‌شده معتبر نیست.";
            return RedirectToAction(nameof(Index));
        }
        if (u.Title == title) return RedirectToAction(nameof(Index));

        audit.Add("CompanyUser", u.CompanyUserId, "title",
            $"عنوان «{u.Name}»: {CompanyLabels.UserTitle(u.Title)} ← {CompanyLabels.UserTitle(title)}", new { from = u.Title, to = title });
        u.Title = title;
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"عنوان «{u.Name}» به «{CompanyLabels.UserTitle(title)}» تغییر کرد. دسترسی‌ها تغییری نکرد؛ از «تعیین سطح دسترسی» تنظیمشان کنید.";
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    //  تعریف کاربر
    // ------------------------------------------------------------------

    [HttpGet]
    public IActionResult Add()
    {
        ViewData["Title"] = "تعریف کاربر";
        var title = "dispatcher";
        return View(new CompanyUserForm { Title = title, Perms = FlagNames(CompanyLabels.DefaultFor(title)) });
    }

    [HttpPost]
    public async Task<IActionResult> Add(CompanyUserForm vm, CancellationToken ct)
    {
        ViewData["Title"] = "تعریف کاربر";
        var cid = me.CompanyId;

        var name = (vm.Name ?? "").Trim();
        var mobile = Fa.NormMobile(vm.Mobile);
        var password = vm.Password ?? "";
        var title = CompanyLabels.AssignableTitles.Contains(vm.Title) ? vm.Title : null;
        var perms = ParsePerms(vm.Perms);

        var errors = new List<string>();
        if (name.Length is < 2 or > 100) errors.Add("نام و نام خانوادگی کاربر را (۲ تا ۱۰۰ نویسه) بنویسید");
        if (mobile.Length == 0) errors.Add("شمارهٔ موبایل معتبر نیست (۰۹xxxxxxxxx)");
        else if (await db.CompanyUsers.AnyAsync(u => u.Mobile == mobile, ct)) errors.Add("این شمارهٔ موبایل قبلاً برای یک کاربر پنل شرکت ثبت شده است");
        if (password.Length < 6) errors.Add("گذرواژه دست‌کم ۶ نویسه باشد");
        if (title is null) errors.Add("عنوان کاربر را انتخاب کنید");
        if (perms == CompanyPermission.None) errors.Add("دست‌کم یک بخش را برای دسترسی انتخاب کنید");
        if (await db.CompanyUsers.CountAsync(u => u.CompanyId == cid, ct) >= MaxUsers)
            errors.Add($"حداکثر {Fa.N(MaxUsers)} کاربر برای هر شرکت مجاز است");

        if (errors.Count > 0)
        {
            ViewBag.Errors = errors;
            vm.Password = null;
            return View(vm);
        }

        var u = new CompanyUser
        {
            CompanyId = cid, Name = name, Mobile = mobile, PassHash = PasswordHasher.Hash(password),
            Title = title!, Permissions = perms, IsOwner = false, IsActive = true
        };
        db.CompanyUsers.Add(u);
        await db.SaveChangesAsync(ct);
        audit.Add("CompanyUser", u.CompanyUserId, "create", $"تعریف کاربر «{u.Name}» ({CompanyLabels.UserTitle(u.Title)})",
            new { u.CompanyId, u.Mobile, u.Title, Permissions = FlagNames(u.Permissions) });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"کاربر «{u.Name}» تعریف شد و می‌تواند با موبایل {Fa.Digits(u.Mobile)} و گذرواژه‌ای که تعیین کردید وارد پنل شرکت شود.";
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    //  تعیین سطح دسترسی
    // ------------------------------------------------------------------

    public async Task<IActionResult> Permissions(CancellationToken ct)
    {
        ViewData["Title"] = "تعیین سطح دسترسی";
        var cid = me.CompanyId;
        var rows = await db.CompanyUsers.AsNoTracking().Where(u => u.CompanyId == cid)
            .OrderByDescending(u => u.IsOwner).ThenBy(u => u.Name)
            .Select(u => new CompanyUserRow
            {
                CompanyUserId = u.CompanyUserId, Name = u.Name, Mobile = u.Mobile, Title = u.Title,
                Permissions = u.Permissions, IsOwner = u.IsOwner, IsActive = u.IsActive,
                LastLoginAt = u.LastLoginAt, CreatedAt = u.CreatedAt
            }).ToListAsync(ct);
        return View(rows);
    }

    [HttpPost]
    public async Task<IActionResult> SavePermissions(CancellationToken ct)
    {
        var cid = me.CompanyId;
        var users = await db.CompanyUsers.Where(u => u.CompanyId == cid).ToListAsync(ct);
        var changed = 0;
        var warnings = new List<string>();

        foreach (var u in users)
        {
            // مدیر شرکت همیشه همه‌چیز — حتی اگر فرم چیز دیگری بفرستد
            var next = u.IsOwner ? CompanyPermission.All : ParsePerms(Request.Form[$"p_{u.CompanyUserId}"].ToArray());

            if (!u.IsOwner && u.CompanyUserId == me.Id && !next.HasFlag(CompanyPermission.Users))
            {
                next |= CompanyPermission.Users;
                warnings.Add("دسترسی «کاربران» از حساب خودتان برداشته نشد؛ وگرنه دیگر نمی‌توانستید به همین صفحه بیایید");
            }

            if (next == u.Permissions) continue;
            audit.Add("CompanyUser", u.CompanyUserId, "permissions", $"تغییر دسترسی «{u.Name}»",
                new { from = FlagNames(u.Permissions), to = FlagNames(next) });
            u.Permissions = next;
            changed++;
        }

        if (changed > 0) await db.SaveChangesAsync(ct);

        TempData["ok"] = changed == 0 ? "تغییری در دسترسی‌ها داده نشد." : $"دسترسی {Fa.N(changed)} کاربر به‌روز شد و از همین لحظه اعمال می‌شود.";
        if (warnings.Count > 0) TempData["err"] = string.Join("؛ ", warnings) + ".";
        return RedirectToAction(nameof(Permissions));
    }

    // ------------------------------------------------------------------

    /// <summary>نام پرچم‌ها («Loads»، «Fleet») → پرچم ترکیبی؛ نام ناشناخته و All نادیده گرفته می‌شوند.</summary>
    private static CompanyPermission ParsePerms(string?[]? names)
    {
        var p = CompanyPermission.None;
        foreach (var n in names ?? [])
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (!Enum.TryParse<CompanyPermission>(n.Trim(), true, out var f)) continue;
            if (CompanyLabels.Permissions.Contains(f)) p |= f;
        }
        return p;
    }

    private static string[] FlagNames(CompanyPermission p) =>
        CompanyLabels.Permissions.Where(f => p.HasFlag(f)).Select(f => f.ToString()).ToArray();
}
