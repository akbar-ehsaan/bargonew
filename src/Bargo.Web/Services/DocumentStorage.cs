namespace Bargo.Web.Services;

/// <summary>
/// ذخیرهٔ فایل‌های بارگذاری‌شده (مدارک، تصاویر بار، رسیدها) بیرون از wwwroot.
/// فایل‌ها فقط از مسیر احرازشدهٔ <c>/files/…</c> (FilesController) خوانده می‌شوند؛
/// اگر در wwwroot بودند، هر کسی با داشتن نشانی کارت ملی راننده را می‌دید.
/// </summary>
public class DocumentStorage(IWebHostEnvironment env, IConfiguration cfg)
{
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".pdf" };

    private string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, cfg["Storage:Root"] ?? "storage"));

    /// <summary>مسیر نسبی فایل ذخیره‌شده، یا null اگر فایلی فرستاده نشده.</summary>
    public async Task<string?> SaveAsync(IFormFile? file, string folder, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > MaxBytes) throw new UserError("حجم فایل نباید بیشتر از ۸ مگابایت باشد.");

        var ext = Path.GetExtension(file.FileName);
        if (!Allowed.Contains(ext)) throw new UserError("فقط تصویر (jpg، png، webp) یا PDF پذیرفته می‌شود.");

        var safeFolder = new string(folder.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safeFolder.Length == 0) safeFolder = "misc";

        var rel = $"{safeFolder}/{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await using var fs = File.Create(full);
        await file.CopyToAsync(fs, ct);
        return rel;
    }

    /// <summary>مسیر فیزیکی یک فایل؛ هر تلاش برای بیرون رفتن از پوشهٔ ذخیره (../) → null.</summary>
    public string? Resolve(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var full = Path.GetFullPath(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    public static string Url(string? rel) => string.IsNullOrEmpty(rel) ? "" : "/files/" + rel;
}
