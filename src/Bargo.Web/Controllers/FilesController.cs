using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers;

/// <summary>
/// سرو فایل‌های بارگذاری‌شده. مدیر همه را می‌بیند؛ بقیه فقط مدارکی که مالکشان
/// هستند (یا مدارک خودروی خودشان/شرکتشان). اسناد سفر و تصویر بار با نام تصادفی
/// ذخیره شده‌اند و برای طرفین سفر لازم‌اند، پس برای کاربر واردشده باز است.
/// </summary>
[Authorize]
public class FilesController(DocumentStorage storage, BargoDbContext db, CurrentUser me) : Controller
{
    private static readonly FileExtensionContentTypeProvider Types = new();

    [HttpGet("/files/{**path}")]
    public async Task<IActionResult> Get(string path, CancellationToken ct)
    {
        var full = storage.Resolve(path);
        if (full is null) return NotFound();

        if (me.Role != Roles.Admin && path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.FilePath == path, ct);
            if (doc is not null && !await CanSeeAsync(doc, ct)) return Forbid();
        }

        if (!Types.TryGetContentType(full, out var type)) type = "application/octet-stream";
        return PhysicalFile(full, type);
    }

    private async Task<bool> CanSeeAsync(Document doc, CancellationToken ct)
    {
        var (kind, id) = me.Owner;
        if (doc.OwnerKind == kind && doc.OwnerId == id) return true;
        if (doc.OwnerKind == OwnerKind.Vehicle)
            return await db.Vehicles.AnyAsync(v => v.VehicleId == doc.OwnerId &&
                ((me.Role == Roles.Driver && v.DriverId == me.Id) || (me.Role == Roles.Company && v.CompanyId == me.CompanyId)), ct);
        if (doc.OwnerKind == OwnerKind.Driver && me.Role == Roles.Company)
            return await db.Drivers.AnyAsync(d => d.DriverId == doc.OwnerId && d.CompanyId == me.CompanyId, ct);
        return false;
    }
}
