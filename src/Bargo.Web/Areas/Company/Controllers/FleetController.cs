namespace Bargo.Web.Areas.CompanyPanel.Controllers;

using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// «مدیریت ناوگان» — خودروهای شرکت (Vehicle.CompanyId). شرکت خودرو را ثبت و ویرایش
/// می‌کند و وضعیت عملیاتی‌اش (فعال/غیرفعال/در تعمیر) را عوض می‌کند؛ تأیید مدارک خودرو
/// (VerifyStatus) با مدیر بارگوست و هر تغییر در پلاک یا نوع، آن را دوباره به «در انتظار»
/// برمی‌گرداند. «تعلیق» فقط از پنل مدیر گذاشته و برداشته می‌شود.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Fleet)]
public class FleetController(BargoDbContext db, CurrentUser me, SettingsService settings, DocumentStorage storage, AuditService audit) : Controller
{
    private static readonly string[] StatusFilters =
        [VehicleStatus.Active, VehicleStatus.Inactive, VehicleStatus.Maintenance, VehicleStatus.Suspended, "unverified"];

    /// <summary>وضعیت‌هایی که شرکت خودش می‌تواند بگذارد.</summary>
    private static readonly string[] Settable = [VehicleStatus.Active, VehicleStatus.Inactive, VehicleStatus.Maintenance];

    // =====================================================================
    //  خودروهای شرکت
    // =====================================================================

    public async Task<IActionResult> Index(string? q, string? status, int? typeId, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "خودروهای شرکت";
        var vm = await ListAsync(q, status, typeId, page, ct);
        return View(vm);
    }

    private async Task<OpsFleetListVm> ListAsync(string? q, string? status, int? typeId, int page, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var all = db.Vehicles.AsNoTracking().Where(v => v.CompanyId == cid);
        var vm = new OpsFleetListVm
        {
            Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            Status = StatusFilters.Contains(status) ? status : null,
            TypeId = typeId,
            WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct),
            Types = await db.VehicleTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct)
        };

        var byStatus = await all.GroupBy(v => v.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        foreach (var s in StatusFilters) vm.Counts[s] = byStatus.Where(x => x.Key == s).Sum(x => x.N);
        vm.Counts["all"] = byStatus.Sum(x => x.N);
        vm.Counts["unverified"] = await all.CountAsync(v => v.VerifyStatus != AccountStatus.Approved, ct);

        var list = all;
        if (vm.Status == "unverified") list = list.Where(v => v.VerifyStatus != AccountStatus.Approved);
        else if (vm.Status is not null) list = list.Where(v => v.Status == vm.Status);
        if (typeId is int tid) list = list.Where(v => v.VehicleTypeId == tid);
        if (vm.Q is not null)
        {
            // پلاک متعارف با ارقام لاتین و بی‌فاصله ذخیره می‌شود؛ پس جستجوی تکه‌ای («۱۲ ع») هم
            // باید بعد از لاتین‌کردن ارقام و حذف فاصله با آن مقایسه شود، نه متن خام کاربر.
            var plate = CompanyOps.NormPlate(vm.Q);
            var latin = Fa.Latin(vm.Q).Replace(" ", "");
            var term = vm.Q;
            list = list.Where(v => v.PlateNo.Contains(plate) || v.PlateNo.Contains(latin) || (v.Brand != null && v.Brand.Contains(term)) || (v.Model != null && v.Model.Contains(term)));
        }

        vm.Page = await PageVm<Vehicle>.FromAsync(
            list.Include(v => v.VehicleType)
                .OrderBy(v => v.Status == VehicleStatus.Active ? 0 : 1).ThenBy(v => v.VehicleType!.SortOrder).ThenBy(v => v.PlateNo),
            page, PageLink.For(Request), ct: ct);
        vm.Busy = await CompanyOps.BusyVehiclesAsync(db, cid, ct);
        return vm;
    }

    // =====================================================================
    //  افزودن / ویرایش
    // =====================================================================

    [HttpGet]
    public async Task<IActionResult> Add(CancellationToken ct)
    {
        ViewData["Title"] = "افزودن خودرو";
        await FillTypesAsync(ct);
        return View(new OpsVehicleForm());
    }

    [HttpPost]
    public async Task<IActionResult> Add(OpsVehicleForm f, CancellationToken ct)
    {
        ViewData["Title"] = "افزودن خودرو";
        var cid = me.CompanyId;
        var errors = await ValidateAsync(f, null, ct);
        if (errors.Count > 0)
        {
            ViewBag.Errors = errors;
            await FillTypesAsync(ct);
            return View(f);
        }

        var v = new Vehicle { CompanyId = cid, DriverId = null, Status = VehicleStatus.Active, VerifyStatus = AccountStatus.Pending };
        Apply(f, v);
        db.Vehicles.Add(v);
        await db.SaveChangesAsync(ct);
        audit.Add("Vehicle", v.VehicleId, "create", $"ثبت خودرو {v.PlateNo} در ناوگان شرکت", new { v.VehicleTypeId, v.CapacityTon });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"خودرو {v.PlateNo} ثبت شد. کارت خودرو، بیمه‌نامه و معاینه فنی را بارگذاری کنید؛ پس از تأیید مدیر بارگو در تخصیص انتخاب‌پذیر می‌شود.";
        return RedirectToAction(nameof(Documents), new { vehicleId = v.VehicleId });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var v = await db.Vehicles.AsNoTracking().Include(x => x.VehicleType).FirstOrDefaultAsync(x => x.VehicleId == id && x.CompanyId == cid, ct);
        if (v is null) return NotFound();
        ViewData["Title"] = $"خودرو {v.PlateNo}";
        await FillEditAsync(v, ct);
        return View(ToForm(v));
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, OpsVehicleForm f, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var v = await db.Vehicles.Include(x => x.VehicleType).FirstOrDefaultAsync(x => x.VehicleId == id && x.CompanyId == cid, ct);
        if (v is null) return NotFound();
        ViewData["Title"] = $"خودرو {v.PlateNo}";

        var errors = await ValidateAsync(f, id, ct);
        if (errors.Count > 0)
        {
            ViewBag.Errors = errors;
            await FillEditAsync(v, ct);
            f.VehicleId = id;
            f.Status = v.Status;
            f.VerifyStatus = v.VerifyStatus;
            return View(f);
        }

        var oldPlate = v.PlateNo;
        var oldType = v.VehicleTypeId;
        Apply(f, v);
        var reverify = v.VerifyStatus == AccountStatus.Approved && (v.PlateNo != oldPlate || v.VehicleTypeId != oldType);
        if (reverify) v.VerifyStatus = AccountStatus.Pending;

        audit.Add("Vehicle", id, "edit", $"ویرایش خودرو {v.PlateNo}" + (reverify ? " — نیازمند تأیید مجدد" : ""),
            new { oldPlate, v.PlateNo, oldType, v.VehicleTypeId, v.VerifyStatus });
        await db.SaveChangesAsync(ct);

        TempData["ok"] = reverify
            ? $"خودرو {v.PlateNo} ویرایش شد. چون پلاک یا نوع تغییر کرده، تا تأیید مجدد مدیر بارگو در تخصیص انتخاب‌پذیر نیست."
            : $"خودرو {v.PlateNo} ویرایش شد.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task FillTypesAsync(CancellationToken ct) =>
        ViewBag.Types = await db.VehicleTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);

    private async Task FillEditAsync(Vehicle v, CancellationToken ct)
    {
        await FillTypesAsync(ct);
        ViewBag.Vehicle = v;
        ViewBag.WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        ViewBag.Busy = (await CompanyOps.BusyVehiclesAsync(db, me.CompanyId, ct)).GetValueOrDefault(v.VehicleId);
        ViewBag.Documents = await db.Documents.AsNoTracking()
            .Where(x => x.OwnerKind == OwnerKind.Vehicle && x.OwnerId == v.VehicleId)
            .OrderByDescending(x => x.UploadedAt).ThenByDescending(x => x.DocumentId)
            .ToListAsync(ct);
    }

    private static OpsVehicleForm ToForm(Vehicle v) => new()
    {
        VehicleId = v.VehicleId, VehicleTypeId = v.VehicleTypeId, PlateNo = v.PlateNo, Brand = v.Brand, ModelName = v.Model,
        Year = v.Year?.ToString(), CapacityTon = v.CapacityTon.ToString(v.CapacityTon % 1 == 0 ? "0" : "0.###", System.Globalization.CultureInfo.InvariantCulture),
        VehicleCardNo = v.VehicleCardNo, InsurancePolicyNo = v.InsurancePolicyNo,
        InsuranceExpiresAt = v.InsuranceExpiresAt, InspectionExpiresAt = v.InspectionExpiresAt,
        VerifyStatus = v.VerifyStatus, Status = v.Status
    };

    /// <summary>اعتبارسنجی فرم و نرمال‌سازی مقادیر درجا (پلاک، شماره‌ها)؛ خطاها به فارسی.</summary>
    private async Task<List<string>> ValidateAsync(OpsVehicleForm f, int? selfId, CancellationToken ct)
    {
        var errors = CompanyOps.BindingErrors(ModelState);

        if (f.VehicleTypeId is not int vt || !await db.VehicleTypes.AnyAsync(t => t.VehicleTypeId == vt && t.IsActive, ct))
            errors.Add("نوع خودرو را انتخاب کنید.");

        var plate = CompanyOps.NormPlate(f.PlateNo);
        f.PlateNo = plate;
        if (plate.Length is < 5 or > 30) errors.Add("پلاک را کامل بنویسید؛ مثلاً «۱۲ ع ۳۴۵ ایران ۶۷».");
        else if (await db.Vehicles.AnyAsync(v => v.PlateNo == plate && (selfId == null || v.VehicleId != selfId), ct))
            errors.Add("خودرویی با این پلاک قبلاً در بارگو ثبت شده است.");

        f.Brand = Cut(f.Brand, 40);
        f.ModelName = Cut(f.ModelName, 40);

        if (!string.IsNullOrWhiteSpace(f.Year))
        {
            var year = CompanyOps.ParseInt(f.Year);
            if (year is null || year is < 1300 or > 2100 || year is > 1500 and < 1980)
                errors.Add("سال ساخت معتبر نیست (شمسی مثل ۱۴۰۰ یا میلادی مثل ۲۰۲۰).");
        }

        var cap = CompanyOps.ParseDecimal(f.CapacityTon);
        if (cap is null or <= 0 or > 100) errors.Add("ظرفیت را به تن وارد کنید (بیشتر از صفر و حداکثر ۱۰۰).");

        f.VehicleCardNo = Cut(Fa.Latin(f.VehicleCardNo), 30);
        f.InsurancePolicyNo = Cut(Fa.Latin(f.InsurancePolicyNo), 30);
        if (f.InsurancePolicyNo is not null && f.InsuranceExpiresAt is null) errors.Add("تاریخ انقضای بیمه‌نامه را وارد کنید.");
        if (f.InsuranceExpiresAt is DateTime ie && ie < DateTime.UtcNow.AddYears(-5)) errors.Add("تاریخ انقضای بیمه‌نامه معتبر نیست.");
        if (f.InspectionExpiresAt is DateTime xe && xe < DateTime.UtcNow.AddYears(-5)) errors.Add("تاریخ انقضای معاینه فنی معتبر نیست.");

        return errors;
    }

    private static void Apply(OpsVehicleForm f, Vehicle v)
    {
        v.VehicleTypeId = f.VehicleTypeId!.Value;
        v.PlateNo = f.PlateNo!;
        v.Brand = f.Brand;
        v.Model = f.ModelName;
        v.Year = string.IsNullOrWhiteSpace(f.Year) ? null : CompanyOps.ParseInt(f.Year);
        v.CapacityTon = CompanyOps.ParseDecimal(f.CapacityTon)!.Value;
        v.VehicleCardNo = f.VehicleCardNo;
        v.InsurancePolicyNo = f.InsurancePolicyNo;
        v.InsuranceExpiresAt = f.InsuranceExpiresAt;
        v.InspectionExpiresAt = f.InspectionExpiresAt;
    }

    private static string? Cut(string? s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return null;
        return s.Length > max ? s[..max] : s;
    }

    // =====================================================================
    //  نوع خودرو
    // =====================================================================

    public async Task<IActionResult> Types(CancellationToken ct)
    {
        ViewData["Title"] = "نوع خودرو";
        var cid = me.CompanyId;

        var agg = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == cid)
            .GroupBy(v => v.VehicleTypeId)
            .Select(g => new
            {
                g.Key,
                Total = g.Count(),
                Active = g.Count(v => v.Status == VehicleStatus.Active && v.VerifyStatus == AccountStatus.Approved),
                Capacity = g.Sum(v => v.CapacityTon)
            })
            .ToDictionaryAsync(x => x.Key, ct);

        // نوع‌های غیرفعال‌شده فقط اگر شرکت هنوز خودرویی از آن دارد
        var types = await db.VehicleTypes.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var rows = types
            .Where(t => t.IsActive || agg.ContainsKey(t.VehicleTypeId))
            .Select(t => agg.TryGetValue(t.VehicleTypeId, out var a)
                ? new OpsTypeRow(t, a.Total, a.Active, a.Capacity)
                : new OpsTypeRow(t, 0, 0, 0))
            .ToList();
        return View(rows);
    }

    // =====================================================================
    //  وضعیت خودرو
    // =====================================================================

    public async Task<IActionResult> Status(string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "وضعیت خودرو";
        var vm = await ListAsync(null, status, null, page, ct);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, string? status, string? back, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var v = await db.Vehicles.FirstOrDefaultAsync(x => x.VehicleId == id && x.CompanyId == cid, ct);
        if (v is null) return NotFound();

        try
        {
            if (status is null || !Settable.Contains(status)) throw new UserError("وضعیت انتخاب‌شده معتبر نیست.");
            if (v.Status == VehicleStatus.Suspended)
                throw new UserError($"خودرو {v.PlateNo} توسط مدیر بارگو تعلیق شده و شرکت نمی‌تواند وضعیتش را عوض کند؛ از پشتیبانی پیگیری کنید.");
            if (v.Status == status) throw new UserError($"خودرو {v.PlateNo} از قبل «{VehicleStatus.Label(status)}» است.");

            if (status != VehicleStatus.Active)
            {
                var busy = (await CompanyOps.BusyVehiclesAsync(db, cid, ct)).GetValueOrDefault(id);
                if (busy is { Hard: true })
                    throw new UserError($"خودرو {v.PlateNo} در سفر {busy.Code} است؛ ابتدا از «تغییر خودرو» خودروی آن سفر را عوض کنید.");
            }

            var old = v.Status;
            v.Status = status;
            audit.Add("Vehicle", id, "status", $"وضعیت خودرو {v.PlateNo}: {VehicleStatus.Label(old)} ← {VehicleStatus.Label(status)}", new { from = old, to = status });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"خودرو {v.PlateNo} «{VehicleStatus.Label(status)}» شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return back == "edit" ? RedirectToAction(nameof(Edit), new { id }) : RedirectToAction(nameof(Status));
    }

    // =====================================================================
    //  مدارک خودرو + بارگذاری
    // =====================================================================

    public async Task<IActionResult> Documents(string? kind, int? vehicleId, int page = 1, CancellationToken ct = default)
    {
        var cid = me.CompanyId;
        var vm = new OpsDocumentsVm
        {
            Kinds = DocumentKind.ForVehicle,
            Kind = DocumentKind.ForVehicle.Contains(kind) ? kind : null,
            WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct),
            OwnerOptions = await CompanyOps.VehicleListAsync(db, cid, ct)
        };
        ViewData["Title"] = vm.Kind switch
        {
            DocumentKind.Insurance => "بیمه‌نامه‌های ناوگان",
            DocumentKind.Inspection => "معاینه فنی ناوگان",
            DocumentKind.VehicleCard => "کارت خودروهای ناوگان",
            _ => "مدارک ناوگان"
        };
        if (vehicleId is int vid)
        {
            if (!vm.OwnerOptions.Any(o => o.Id == vid)) return NotFound();
            vm.OwnerId = vid;
        }

        var q = db.Documents.AsNoTracking()
            .Where(x => x.OwnerKind == OwnerKind.Vehicle && db.Vehicles.Any(v => v.VehicleId == x.OwnerId && v.CompanyId == cid));
        if (vm.OwnerId is int oid) q = q.Where(x => x.OwnerId == oid);
        if (vm.Kind is not null) q = q.Where(x => x.Kind == vm.Kind);

        vm.Page = await PageVm<Document>.FromAsync(q.OrderByDescending(x => x.UploadedAt).ThenByDescending(x => x.DocumentId), page, PageLink.For(Request), ct: ct);
        vm.Owners = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == cid)
            .ToDictionaryAsync(v => v.VehicleId, v => v.PlateNo, ct);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Upload(int? vehicleId, string? kind, string? number, DateTime? expiresAt, IFormFile? file, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var backKind = DocumentKind.ForVehicle.Contains(kind) ? kind : null;
        try
        {
            if (!ModelState.IsValid) throw new UserError("تاریخ انقضا معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            var v = vehicleId is int vid
                ? await db.Vehicles.FirstOrDefaultAsync(x => x.VehicleId == vid && x.CompanyId == cid, ct)
                : null;
            if (v is null) throw new UserError("خودرو را انتخاب کنید.");
            if (backKind is null) throw new UserError("نوع مدرک را انتخاب کنید.");
            if (file is null || file.Length == 0) throw new UserError("تصویر یا فایل PDF مدرک را انتخاب کنید.");

            var label = DocumentKind.Label(backKind);
            var expiring = DocumentKind.Expiring.Contains(backKind);
            if (expiring && expiresAt is null) throw new UserError($"تاریخ انقضای {label} را وارد کنید.");
            var num = Cut(Fa.Latin(number), 40);

            var path = await storage.SaveAsync(file, "docs", ct);
            var doc = new Document
            {
                OwnerKind = OwnerKind.Vehicle, OwnerId = v.VehicleId, Kind = backKind,
                Number = num, ExpiresAt = expiring ? expiresAt : null, FilePath = path, Status = AccountStatus.Pending
            };
            db.Documents.Add(doc);

            // شماره و تاریخِ روی خودرو با آخرین مدرک هم‌خوان می‌شود
            switch (backKind)
            {
                case DocumentKind.VehicleCard:
                    if (num is not null) v.VehicleCardNo = Cut(num, 30);
                    break;
                case DocumentKind.Insurance:
                    if (num is not null) v.InsurancePolicyNo = Cut(num, 30);
                    if (expiresAt is DateTime ie) v.InsuranceExpiresAt = ie;
                    break;
                case DocumentKind.Inspection:
                    if (expiresAt is DateTime xe) v.InspectionExpiresAt = xe;
                    break;
            }
            // خودروی ردشده با مدرک تازه دوباره به صف بررسی می‌رود
            if (v.VerifyStatus == AccountStatus.Rejected) v.VerifyStatus = AccountStatus.Pending;
            await db.SaveChangesAsync(ct);

            audit.Add("Document", doc.DocumentId, "upload", $"بارگذاری {label} برای خودرو {v.PlateNo} توسط شرکت",
                new { doc.OwnerKind, doc.OwnerId, doc.Kind, doc.ExpiresAt });
            await db.SaveChangesAsync(ct);

            TempData["ok"] = $"«{label}» برای خودرو {v.PlateNo} بارگذاری شد و در صف بررسی مدیر بارگو قرار گرفت.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Documents), new { vehicleId, kind = backKind });
    }

    // =====================================================================
    //  هشدار انقضا
    // =====================================================================

    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        ViewData["Title"] = "هشدار انقضای مدارک";
        var cid = me.CompanyId;
        var warnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        var vm = new OpsAlertsVm
        {
            WarnDays = warnDays,
            BlockOnExpired = await settings.GetBoolAsync(SettingsService.Keys.BlockOnExpired, ct),
            Alerts = await CompanyOps.ExpiryAlertsAsync(db, cid, warnDays, ct)
        };
        return View(vm);
    }
}
