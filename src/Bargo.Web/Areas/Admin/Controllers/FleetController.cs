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
/// «مدیریت ناوگان و خودروها» — همهٔ خودروها (راننده‌های مستقل و شرکت‌ها) با مالک،
/// صف تأیید خودرو، و دادهٔ پایهٔ «انواع خودرو».
///
/// تأیید خودرو جدا از تأیید مدارکش است: مدارک در «احراز هویت و مدارک» بررسی
/// می‌شوند و اینجا مدیر با دیدن شمار مدارکِ در انتظار تصمیم نهایی را می‌گیرد.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Users)]
public class FleetController(
    BargoDbContext db,
    NotificationService notify,
    AuditService audit,
    SettingsService settings) : Controller
{
    private static bool ValidStatus(string? s) => s is not null && AdminOps.AccountStatuses.Contains(s);

    // ------------------------------------------------------------------
    //  همه خودروها / در انتظار تأیید
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? q, string? verify, string? owner, int? type, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = verify == AccountStatus.Pending ? "خودروهای در انتظار تأیید" : "همه خودروها";
        var term = AdminOps.Term(q);
        var src = db.Vehicles.AsNoTracking();
        if (term.Length > 0)
            src = src.Where(v => v.PlateNo.Contains(term) || (v.Brand != null && v.Brand.Contains(term)) ||
                                 (v.Driver != null && ((v.Driver.FirstName + " " + v.Driver.LastName).Contains(term) || v.Driver.Mobile.Contains(term))) ||
                                 (v.Company != null && v.Company.Name.Contains(term)));
        if (owner == OwnerKind.Driver) src = src.Where(v => v.CompanyId == null && v.DriverId != null);
        else if (owner == OwnerKind.Company) src = src.Where(v => v.CompanyId != null);
        if (type is int tid) src = src.Where(v => v.VehicleTypeId == tid);

        ViewBag.Counts = await src.GroupBy(v => v.VerifyStatus).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        if (ValidStatus(verify)) src = src.Where(v => v.VerifyStatus == verify);

        // در انتظارها قدیمی‌ترین اول (صف)، بقیه تازه‌ترین اول
        var ordered = verify == AccountStatus.Pending
            ? src.OrderBy(v => v.CreatedAt)
            : src.OrderBy(v => v.VerifyStatus == AccountStatus.Pending ? 0 : 1).ThenByDescending(v => v.CreatedAt);
        var vm = await PageVm<VehicleRow>.FromAsync(AdminOps.VehicleRows(db, ordered), page, PageLink.For(Request), ct: ct);

        ViewBag.Q = term;
        ViewBag.Verify = ValidStatus(verify) ? verify : null;
        ViewBag.Owner = owner is OwnerKind.Driver or OwnerKind.Company ? owner : null;
        ViewBag.Type = type;
        ViewBag.Types = await db.VehicleTypes.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new SelectOption(t.VehicleTypeId, t.Name)).ToListAsync(ct);
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        return View(vm);
    }

    /// <summary>تأیید یا رد خودرو؛ رد بدون علت پذیرفته نمی‌شود. مالک (راننده یا شرکت) اعلان می‌گیرد.</summary>
    [HttpPost]
    public async Task<IActionResult> Verify(int id, string status, string? note, string? returnUrl, CancellationToken ct)
    {
        var v = await db.Vehicles.Include(x => x.VehicleType).FirstOrDefaultAsync(x => x.VehicleId == id, ct);
        if (v is null) return NotFound();

        try
        {
            if (status is not (AccountStatus.Approved or AccountStatus.Rejected)) throw new UserError("وضعیت انتخاب‌شده معتبر نیست.");
            note = AdminOps.Note(note);
            if (status == AccountStatus.Rejected && note is null)
                throw new UserError("برای رد خودرو، علت را بنویسید؛ مالک باید بداند چه چیزی را اصلاح کند.");
            if (v.VerifyStatus == status)
                throw new UserError($"خودروی {Plate.Pretty(v.PlateNo)} از قبل «{AccountStatus.Label(status)}» است.");

            var from = v.VerifyStatus;
            v.VerifyStatus = status;

            var hint = "";
            if (status == AccountStatus.Approved)
            {
                var pending = await db.Documents.CountAsync(d => d.OwnerKind == OwnerKind.Vehicle && d.OwnerId == id && d.Status == AccountStatus.Pending, ct);
                var missing = DocumentKind.ForVehicle.Length -
                              await db.Documents.Where(d => d.OwnerKind == OwnerKind.Vehicle && d.OwnerId == id && d.Status == AccountStatus.Approved && DocumentKind.ForVehicle.Contains(d.Kind))
                                  .Select(d => d.Kind).Distinct().CountAsync(ct);
                if (pending > 0) hint += $" توجه: {Fa.N(pending)} مدرک این خودرو هنوز در انتظار بررسی است.";
                if (missing > 0) hint += $" {Fa.N(missing)} مدرک لازم خودرو هنوز تأیید نشده؛ راننده تا تأیید مدارک بار نمی‌گیرد.";
            }
            else if (await db.Trips.AnyAsync(t => t.VehicleId == id && TripStatus.Live.Contains(t.Status), ct))
                hint = " توجه: این خودرو هم‌اکنون در سفر است؛ سفر با رد خودرو متوقف نمی‌شود.";

            // اعلان به مالک — خودرو حساب کاربری ندارد
            var (kind, ownerId, link) = v.CompanyId is int c ? (OwnerKind.Company, c, "/Company/Fleet")
                : v.DriverId is int d ? (OwnerKind.Driver, d, "/Driver/Vehicle")
                : ("", 0, "");
            if (ownerId > 0)
                notify.Add(kind, ownerId,
                    status == AccountStatus.Approved ? $"خودروی {Plate.Pretty(v.PlateNo)} تأیید شد" : $"خودروی {Plate.Pretty(v.PlateNo)} تأیید نشد",
                    status == AccountStatus.Approved ? "خودرو می‌تواند در سفرها استفاده شود." + (note is null ? "" : " " + note) : note,
                    link, "document");

            audit.Add("Vehicle", id, "verify:" + status,
                $"خودروی {Plate.Pretty(v.PlateNo)} ({v.VehicleType?.Name}): {AccountStatus.Label(from)} ← {AccountStatus.Label(status)}",
                new { from, to = status, note, v.DriverId, v.CompanyId });
            await db.SaveChangesAsync(ct);

            TempData["ok"] = (status == AccountStatus.Approved ? $"خودروی {Plate.Pretty(v.PlateNo)} تأیید شد." : $"خودروی {Plate.Pretty(v.PlateNo)} رد شد و علت برای مالک ارسال شد.") + hint;
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return AdminOps.Back(this, returnUrl, "/Admin/Fleet?verify=pending");
    }

    // ------------------------------------------------------------------
    //  انواع خودرو
    // ------------------------------------------------------------------

    public async Task<IActionResult> Types(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "انواع خودرو";
        var items = await db.VehicleTypes.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        return View(new VehicleTypesVm
        {
            Items = items,
            Edit = edit is int id ? items.FirstOrDefault(t => t.VehicleTypeId == id) : null,
            Vehicles = await db.Vehicles.GroupBy(v => v.VehicleTypeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct),
            Loads = await db.Loads.Where(l => l.VehicleTypeId != null).GroupBy(l => l.VehicleTypeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct)
        });
    }

    /// <summary>
    /// ساخت/ویرایش نوع خودرو. حذف وجود ندارد: خودروها و بارها به نوع اشاره می‌کنند؛
    /// نوعِ بلااستفاده «غیرفعال» می‌شود و از فرم‌های ثبت خودرو و بار پنهان می‌ماند.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SaveType(int? id, string? name, string? bodyKind, string? capacityTon, string? sortOrder, bool isActive, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Types), new { edit = id });
        name = AdminOps.Note(name, 80);
        bodyKind = AdminOps.Note(bodyKind, 30);
        var cap = BackofficeLookup.ParseDecimal(capacityTon);

        if (name is null || name.Length < 2) { TempData["err"] = "نام نوع خودرو ۲ تا ۸۰ نویسه باشد."; return back; }
        if (cap is null || cap.Value <= 0 || cap.Value > 200) { TempData["err"] = "ظرفیت را به تن وارد کنید (بیشتر از صفر و کمتر از ۲۰۰)."; return back; }
        if (await db.VehicleTypes.AnyAsync(t => t.Name == name && t.VehicleTypeId != (id ?? 0), ct))
        { TempData["err"] = $"نوع خودرویی با نام «{name}» از قبل وجود دارد."; return back; }

        VehicleType? row = null;
        if (id is int tid)
        {
            row = await db.VehicleTypes.FirstOrDefaultAsync(t => t.VehicleTypeId == tid, ct);
            if (row is null) return NotFound();
        }
        var before = row is null ? null : new { row.Name, row.BodyKind, row.CapacityTon, row.SortOrder, row.IsActive };
        row ??= db.VehicleTypes.Add(new VehicleType()).Entity;
        row.Name = name;
        row.BodyKind = bodyKind;
        row.CapacityTon = cap.Value;
        row.SortOrder = BackofficeLookup.ParseInt(sortOrder) ?? (before is null ? await db.VehicleTypes.CountAsync(ct) + 1 : row.SortOrder);
        row.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        audit.Add("VehicleType", row.VehicleTypeId, before is null ? "create" : "update",
            $"نوع خودرو «{name}» ({bodyKind ?? "—"}، {Fa.N(cap.Value, 1)} تن){(isActive ? "" : " — غیرفعال")}",
            new { before, after = new { name, bodyKind, capacityTon = cap, isActive } });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"نوع خودرو «{name}» ذخیره شد.";
        return RedirectToAction(nameof(Types));
    }
}
