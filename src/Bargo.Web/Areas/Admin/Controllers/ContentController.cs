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
/// مدیریت محتوا: بنر، خبر، سوالات متداول، قوانین و صفحات ثابت — همه در یک جدول
/// (ContentItem) با ستون Kind؛ یک صفحه با پنج تب، نه پنج کنترلر.
///
/// Slug فقط برای «قوانین» و «صفحهٔ ثابت» معنا دارد (نشانی عمومی /p/{slug})؛ تصویر
/// فقط برای بنر و خبر. فرم یکی است و فیلدهای بی‌ربط به هر نوع پنهان می‌شوند.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Content)]
public class ContentController(BargoDbContext db, DocumentStorage storage, AuditService audit) : Controller
{
    public static readonly string[] Kinds = [ContentKind.Banner, ContentKind.News, ContentKind.Faq, ContentKind.Rule, ContentKind.Page];
    public static readonly string[] Audiences = ["all", OwnerKind.Driver, OwnerKind.Shipper, OwnerKind.Company];

    private static string NormKind(string? kind) => Kinds.Contains(kind ?? "") ? kind! : ContentKind.Banner;

    public static bool HasSlug(string kind) => kind is ContentKind.Rule or ContentKind.Page;
    public static bool HasImage(string kind) => kind is ContentKind.Banner or ContentKind.News;
    public static bool HasLink(string kind) => kind is ContentKind.Banner or ContentKind.News;
    public static bool HasAudience(string kind) => kind is ContentKind.Banner or ContentKind.News or ContentKind.Faq;

    public async Task<IActionResult> Index(string? kind, int? edit, bool @new = false, CancellationToken ct = default)
    {
        kind = NormKind(kind);
        ViewData["Title"] = kind switch
        {
            ContentKind.Banner => "بنرها",
            ContentKind.News => "اخبار",
            ContentKind.Faq => "سوالات متداول",
            ContentKind.Rule => "قوانین",
            _ => "صفحات ثابت"
        };

        var items = await db.ContentItems.AsNoTracking().Where(c => c.Kind == kind)
            .OrderBy(c => c.SortOrder).ThenByDescending(c => c.ContentItemId).ToListAsync(ct);
        var counts = await db.ContentItems.AsNoTracking().GroupBy(c => c.Kind)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var editItem = edit is int id ? items.FirstOrDefault(c => c.ContentItemId == id) : null;
        return View(new ContentVm
        {
            Kind = kind,
            Items = items,
            Counts = counts,
            Edit = editItem,
            ShowForm = @new || editItem is not null
        });
    }

    [HttpPost]
    [RequestSizeLimit(DocumentStorage.MaxBytes + 1024 * 1024)]
    public async Task<IActionResult> Save(int? id, string? kind, string? title, string? slug, string? body, IFormFile? image, bool removeImage,
        string? link, string? audience, string? sortOrder, bool isPublished, CancellationToken ct)
    {
        kind = NormKind(kind);
        var back = RedirectToAction(nameof(Index), new { kind, edit = id, @new = id is null ? "true" : null });
        title = (title ?? "").Trim();
        body = (body ?? "").Trim();
        link = (link ?? "").Trim();
        slug = Fa.Latin(slug).Trim().ToLowerInvariant();
        audience = HasAudience(kind) && Audiences.Contains(audience ?? "") ? audience! : "all";

        if (title.Length is < 2 or > 200) { TempData["err"] = "عنوان ۲ تا ۲۰۰ نویسه باشد."; return back; }
        if (body.Length == 0 && kind is not ContentKind.Banner) { TempData["err"] = "متن محتوا خالی است."; return back; }
        if (HasSlug(kind))
        {
            if (slug.Length == 0) slug = Slugify(title);
            if (!Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{1,99}$"))
            { TempData["err"] = "نشانی (slug) فقط حروف کوچک لاتین، رقم و خط تیره — ۲ تا ۱۰۰ نویسه."; return back; }
            if (await db.ContentItems.AnyAsync(c => c.Slug == slug && c.ContentItemId != (id ?? 0), ct))
            { TempData["err"] = $"نشانی «{slug}» قبلاً به محتوای دیگری داده شده است."; return back; }
        }
        else slug = "";
        if (HasLink(kind) && link.Length > 0 && !(link.StartsWith('/') || Uri.IsWellFormedUriString(link, UriKind.Absolute)))
        { TempData["err"] = "پیوند باید با / شروع شود یا یک نشانی کامل (https://…) باشد."; return back; }
        if (!HasLink(kind)) link = "";

        ContentItem? row = null;
        if (id is int cid)
        {
            row = await db.ContentItems.FirstOrDefaultAsync(c => c.ContentItemId == cid, ct);
            if (row is null) return NotFound();
        }

        string? imagePath = row?.ImagePath;
        try
        {
            if (HasImage(kind))
            {
                var saved = await storage.SaveAsync(image, "content", ct);
                if (saved is not null) imagePath = saved;
                else if (removeImage) imagePath = null;
            }
            else imagePath = null;
        }
        catch (UserError e) { TempData["err"] = e.Message; return back; }

        var before = row is null ? null : new { row.Title, row.Slug, row.Audience, row.SortOrder, row.IsPublished, row.Link, row.ImagePath };
        row ??= db.ContentItems.Add(new ContentItem { Kind = kind }).Entity;
        row.Kind = kind;
        row.Title = title;
        row.Slug = slug.Length == 0 ? null : slug;
        row.Body = body;
        row.ImagePath = imagePath;
        row.Link = link.Length == 0 ? null : link;
        row.Audience = audience;
        row.SortOrder = BackofficeLookup.ParseInt(sortOrder) ?? 0;
        row.IsPublished = isPublished;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("ContentItem", row.ContentItemId, before is null ? "create" : "update",
            $"{ContentKind.Label(kind)} «{title}»{(isPublished ? "" : " (پیش‌نویس)")}",
            new { before, after = new { title, slug, audience, sortOrder = row.SortOrder, isPublished, link, imagePath } });

        TempData["ok"] = $"{ContentKind.Label(kind)} «{title}» ذخیره شد.";
        return RedirectToAction(nameof(Index), new { kind });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var row = await db.ContentItems.FirstOrDefaultAsync(c => c.ContentItemId == id, ct);
        if (row is null) return NotFound();
        db.ContentItems.Remove(row);
        audit.Add("ContentItem", id, "delete", $"حذف {ContentKind.Label(row.Kind)} «{row.Title}»", new { row.Kind, row.Title, row.Slug });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"«{row.Title}» حذف شد.";
        return RedirectToAction(nameof(Index), new { kind = row.Kind });
    }

    [HttpPost]
    public async Task<IActionResult> TogglePublish(int id, CancellationToken ct)
    {
        var row = await db.ContentItems.FirstOrDefaultAsync(c => c.ContentItemId == id, ct);
        if (row is null) return NotFound();
        row.IsPublished = !row.IsPublished;
        row.UpdatedAt = DateTime.UtcNow;
        audit.Add("ContentItem", id, row.IsPublished ? "publish" : "unpublish", $"{(row.IsPublished ? "انتشار" : "خروج از انتشار")} {ContentKind.Label(row.Kind)} «{row.Title}»");
        await db.SaveChangesAsync(ct);
        TempData["ok"] = row.IsPublished ? $"«{row.Title}» منتشر شد." : $"«{row.Title}» از انتشار خارج شد.";
        return RedirectToAction(nameof(Index), new { kind = row.Kind });
    }

    /// <summary>عنوان لاتین → slug؛ عنوان فارسی چیزی برنمی‌گرداند و کاربر باید خودش بنویسد.</summary>
    private static string Slugify(string title)
    {
        var s = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length > 100 ? s[..100].TrimEnd('-') : s;
    }
}
