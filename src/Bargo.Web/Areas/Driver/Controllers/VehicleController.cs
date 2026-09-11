using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// خودرو و مدارک راننده. [AllowUnapproved]: رانندهٔ تازه دقیقاً برای تأیید شدن باید
/// همین‌جا خودرو و مدارکش را ثبت کند.
///
/// تغییر پلاک یا نوع خودرو، تأیید مدیر را پس می‌گیرد (VerifyStatus → pending): تأیید
/// روی «این پلاک با این نوع» داده شده بود، نه روی ردیف. خودرویی که در سفر زنده است
/// نه پلاکش عوض می‌شود و نه از «فعال» بیرون می‌آید.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
[AllowUnapproved]
public class VehicleController(BargoDbContext db, CurrentUser me, SettingsService settings, DocumentStorage storage, DriverReadiness readiness) : Controller
{
    private const int MaxVehicles = 5;
    private static readonly string[] SelfStatuses = [VehicleStatus.Active, VehicleStatus.Inactive, VehicleStatus.Maintenance];

    // ------------------------------------------------------------------ مشخصات خودرو

    public async Task<IActionResult> Index(int? edit, CancellationToken ct)
    {
        ViewData["Title"] = "مشخصات خودرو";
        var driver = await db.Drivers.AsNoTracking().Where(d => d.DriverId == me.Id)
            .Select(d => new { CompanyName = d.Company!.Name }).FirstOrDefaultAsync(ct);
        if (driver is null) return NotFound();

        var vehicles = await Mine().Include(v => v.VehicleType).OrderByDescending(v => v.VehicleId).ToListAsync(ct);
        var ids = vehicles.Select(v => v.VehicleId).ToList();
        var docCounts = await db.Documents.AsNoTracking()
            .Where(d => d.OwnerKind == OwnerKind.Vehicle && ids.Contains(d.OwnerId))
            .GroupBy(d => new { d.OwnerId, d.Status }).Select(g => new { g.Key.OwnerId, g.Key.Status, N = g.Count() })
            .ToListAsync(ct);
        var live = await db.Trips.AsNoTracking()
            .Where(t => t.VehicleId != null && ids.Contains(t.VehicleId.Value) && TripStatus.Live.Contains(t.Status))
            .Select(t => t.VehicleId!.Value).Distinct().ToListAsync(ct);

        var vm = new VehiclePageVm
        {
            Types = await db.VehicleTypeListAsync(ct),
            CompanyName = driver.CompanyName,
            WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct),
            Vehicles = vehicles.Select(v => new VehicleRowVm
            {
                Vehicle = v,
                ApprovedDocs = docCounts.Where(c => c.OwnerId == v.VehicleId && c.Status == AccountStatus.Approved).Sum(c => c.N),
                PendingDocs = docCounts.Where(c => c.OwnerId == v.VehicleId && c.Status == AccountStatus.Pending).Sum(c => c.N),
                InLiveTrip = live.Contains(v.VehicleId)
            }).ToList()
        };
        if (edit is int id && vehicles.FirstOrDefault(v => v.VehicleId == id) is { } e) vm.Form = VehicleFormVm.From(e);
        ViewBag.CanAdd = vehicles.Count < MaxVehicles;
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Save(VehicleFormVm f, CancellationToken ct)
    {
        try
        {
            var plate = NormPlate(f.PlateNo);
            if (plate.Length is < 5 or > 30) throw new UserError("شمارهٔ پلاک را کامل وارد کنید (مثلاً «۱۲ ع ۳۴۵ ایران ۶۷»).");
            if (!await db.VehicleTypes.AnyAsync(t => t.VehicleTypeId == f.VehicleTypeId && t.IsActive, ct))
                throw new UserError("نوع خودرو را از فهرست انتخاب کنید.");
            if (f.CapacityTon is null or <= 0 or > 100) throw new UserError("ظرفیت بارگیری را به تن وارد کنید (بیشتر از صفر و حداکثر ۱۰۰).");
            if (f.Year is int y && y is < 1340 or > 1500) throw new UserError("سال ساخت را به شمسی وارد کنید (مثلاً ۱۳۹۸).");
            var brand = Clean(f.Brand, 40);
            var model = Clean(f.Model, 40);
            var cardNo = CleanLatin(f.VehicleCardNo, 30);
            var policyNo = CleanLatin(f.InsurancePolicyNo, 30);
            if (f.InsuranceExpiresAt is null && policyNo is not null) throw new UserError("تاریخ انقضای بیمه‌نامه را وارد کنید.");

            var dup = await db.Vehicles.AsNoTracking()
                .AnyAsync(v => v.PlateNo == plate && v.VehicleId != f.VehicleId && v.Status != VehicleStatus.Suspended, ct);
            if (dup) throw new UserError($"پلاک {Plate.Pretty(plate)} پیش‌تر در سامانه ثبت شده است. اگر خودرو مال شماست با پشتیبانی تماس بگیرید.");

            Vehicle v;
            var reverify = false;
            if (f.VehicleId is int id)
            {
                v = await Mine(tracking: true).FirstOrDefaultAsync(x => x.VehicleId == id, ct) ?? throw new UserError("خودرو پیدا نشد.");
                if (v.Status == VehicleStatus.Suspended)
                    throw new UserError("این خودرو توسط مدیر تعلیق شده و تا رفع تعلیق قابل ویرایش نیست. با پشتیبانی تماس بگیرید.");
                var identityChanged = v.PlateNo != plate || v.VehicleTypeId != f.VehicleTypeId;
                if (identityChanged && await InLiveTripAsync(id, ct))
                    throw new UserError("این خودرو در یک سفر زنده است؛ تغییر پلاک یا نوع تا پایان سفر ممکن نیست.");
                reverify = identityChanged && v.VerifyStatus == AccountStatus.Approved;
                if (identityChanged) v.VerifyStatus = AccountStatus.Pending;
            }
            else
            {
                if (await Mine().CountAsync(ct) >= MaxVehicles)
                    throw new UserError($"حداکثر {Fa.N(MaxVehicles)} خودرو می‌توانید ثبت کنید؛ یکی از خودروهای قبلی را ویرایش کنید.");
                v = new Vehicle { DriverId = me.Id, Status = VehicleStatus.Active, VerifyStatus = AccountStatus.Pending };
                db.Vehicles.Add(v);
            }

            v.PlateNo = plate;
            v.VehicleTypeId = f.VehicleTypeId;
            v.Brand = brand;
            v.Model = model;
            v.Year = f.Year;
            v.CapacityTon = f.CapacityTon ?? 0;
            v.VehicleCardNo = cardNo;
            v.InsurancePolicyNo = policyNo;
            v.InsuranceExpiresAt = f.InsuranceExpiresAt;
            v.InspectionExpiresAt = f.InspectionExpiresAt;
            await db.SaveChangesAsync(ct);

            TempData["ok"] = f.VehicleId is null
                ? $"خودروی {Plate.Pretty(plate)} ثبت شد. حالا مدارک آن (کارت خودرو، بیمه، معاینه فنی) را بارگذاری کنید تا مدیر تأییدش کند."
                : reverify
                    ? $"مشخصات خودروی {Plate.Pretty(plate)} ذخیره شد. چون پلاک یا نوع خودرو عوض شد، تأیید مدیر دوباره لازم است."
                    : $"مشخصات خودروی {Plate.Pretty(plate)} ذخیره شد.";
            return f.VehicleId is null
                ? RedirectToAction(nameof(Documents), new { kind = DocumentKind.VehicleCard, vehicleId = v.VehicleId })
                : RedirectToAction(nameof(Index));
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
            return RedirectToAction(nameof(Index), new { edit = f.VehicleId });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SetStatus(int id, string? status, CancellationToken ct)
    {
        var v = await Mine(tracking: true).FirstOrDefaultAsync(x => x.VehicleId == id, ct);
        if (v is null) return NotFound();
        try
        {
            if (status is null || !SelfStatuses.Contains(status)) throw new UserError("وضعیت انتخابی معتبر نیست.");
            if (v.Status == VehicleStatus.Suspended)
                throw new UserError("این خودرو توسط مدیر تعلیق شده است؛ فقط مدیر می‌تواند وضعیتش را تغییر دهد.");
            if (status != VehicleStatus.Active && await InLiveTripAsync(id, ct))
                throw new UserError("این خودرو در یک سفر زنده است و تا پایان سفر باید «فعال» بماند.");
            if (v.Status == status) throw new UserError($"خودرو همین حالا «{VehicleStatus.Label(status)}» است.");

            v.Status = status;
            await db.SaveChangesAsync(ct);
            TempData["ok"] = status == VehicleStatus.Active
                ? $"خودروی {Plate.Pretty(v.PlateNo)} فعال شد و در پیشنهاد قیمت قابل انتخاب است."
                : $"خودروی {Plate.Pretty(v.PlateNo)} به وضعیت «{VehicleStatus.Label(status)}» رفت و تا فعال شدن دوباره، با آن نمی‌توانید بار بگیرید.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ مدارک

    public async Task<IActionResult> Documents(string? kind, int? vehicleId, CancellationToken ct)
    {
        kind = DocumentsVm.Kinds.Contains(kind) ? kind! : DocumentKind.NationalCard;
        ViewData["Title"] = DocumentKind.Label(kind);

        var vehicles = await Mine().Include(v => v.VehicleType).OrderByDescending(v => v.Status == VehicleStatus.Active).ThenByDescending(v => v.VehicleId).ToListAsync(ct);
        var ids = vehicles.Select(v => v.VehicleId).ToList();
        var isVehicle = DocumentsVm.IsVehicleKind(kind);

        // مدرک خودرو: خودروی انتخابی، وگرنه خودروی فعال (اول فهرست)
        Vehicle? veh = null;
        if (isVehicle)
        {
            veh = vehicles.FirstOrDefault(v => v.VehicleId == vehicleId) ?? vehicles.FirstOrDefault();
            vehicleId = veh?.VehicleId;
        }
        else vehicleId = null;

        var counts = await db.Documents.AsNoTracking()
            .Where(d => (d.OwnerKind == OwnerKind.Driver && d.OwnerId == me.Id) || (d.OwnerKind == OwnerKind.Vehicle && ids.Contains(d.OwnerId)))
            .GroupBy(d => d.Kind).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        List<Document> docs;
        if (!isVehicle)
            docs = await db.Documents.AsNoTracking()
                .Where(d => d.OwnerKind == OwnerKind.Driver && d.OwnerId == me.Id && d.Kind == kind)
                .OrderByDescending(d => d.UploadedAt).ToListAsync(ct);
        else if (veh is null)
            docs = [];
        else
        {
            var vid = veh.VehicleId;
            docs = await db.Documents.AsNoTracking()
                .Where(d => d.OwnerKind == OwnerKind.Vehicle && d.OwnerId == vid && d.Kind == kind)
                .OrderByDescending(d => d.UploadedAt).ToListAsync(ct);
        }

        var current = docs.FirstOrDefault(d => d.Status != AccountStatus.Rejected);
        var ownerLabel = isVehicle ? $"خودروی {Plate.Pretty(veh?.PlateNo)}" : "شما";
        var vm = new DocumentsVm
        {
            Kind = kind,
            VehicleId = vehicleId,
            Vehicles = vehicles,
            Counts = counts,
            WarnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct),
            Rows = docs.Select(d => new DocRowVm { Doc = d, OwnerLabel = ownerLabel, IsCurrent = ReferenceEquals(d, current) }).ToList()
        };

        // شماره و انقضای ثبت‌شده روی پروفایل/خودرو — پیش‌فرض فرم بارگذاری
        var driver = await db.Drivers.AsNoTracking().Where(d => d.DriverId == me.Id)
            .Select(d => new { d.LicenseNo, d.LicenseExpiresAt, d.SmartCardNo, d.SmartCardExpiresAt, d.NationalCode }).FirstAsync(ct);
        string? number = null;
        DateTime? expires = null;
        switch (kind)
        {
            case DocumentKind.NationalCard: number = driver.NationalCode; break;
            case DocumentKind.License: number = driver.LicenseNo; expires = driver.LicenseExpiresAt; break;
            case DocumentKind.SmartCard: number = driver.SmartCardNo; expires = driver.SmartCardExpiresAt; break;
            case DocumentKind.VehicleCard: number = veh?.VehicleCardNo; break;
            case DocumentKind.Insurance: number = veh?.InsurancePolicyNo; expires = veh?.InsuranceExpiresAt; break;
            case DocumentKind.Inspection: expires = veh?.InspectionExpiresAt; break;
        }
        ViewBag.Number = number;
        ViewBag.ExpiresAt = expires;
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Upload(string? kind, int? vehicleId, string? number, DateTime? expiresAt, IFormFile? file, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Documents), new { kind, vehicleId });
        try
        {
            if (kind is null || !DocumentsVm.Kinds.Contains(kind)) throw new UserError("نوع مدرک معتبر نیست.");
            if (file is null || file.Length == 0) throw new UserError("تصویر یا فایل PDF مدرک را انتخاب کنید.");
            number = CleanLatin(number, 40);
            var expiring = DocumentsVm.IsExpiringKind(kind);
            if (expiring && expiresAt is null) throw new UserError($"تاریخ انقضای {DocumentKind.Label(kind)} را وارد کنید.");
            if (expiring && expiresAt < DateTime.UtcNow) throw new UserError($"این {DocumentKind.Label(kind)} منقضی شده است؛ نسخهٔ معتبر را بارگذاری کنید.");
            if (!expiring) expiresAt = null;

            Vehicle? veh = null;
            if (DocumentsVm.IsVehicleKind(kind))
            {
                veh = await Mine(tracking: true).FirstOrDefaultAsync(v => v.VehicleId == vehicleId, ct)
                      ?? throw new UserError("ابتدا خودرو را ثبت کنید، سپس مدارک آن را بارگذاری کنید.");
            }

            var path = await storage.SaveAsync(file, "docs", ct);
            db.Documents.Add(new Document
            {
                OwnerKind = veh is null ? OwnerKind.Driver : OwnerKind.Vehicle,
                OwnerId = veh?.VehicleId ?? me.Id,
                Kind = kind,
                Number = number,
                ExpiresAt = expiresAt,
                FilePath = path,
                Status = AccountStatus.Pending
            });

            // شماره و انقضا روی پروفایل/خودرو هم به‌روز شود تا «هشدار انقضا» و پیشنهاد قیمت یک عدد ببینند
            if (veh is null)
            {
                var d = await db.Drivers.FirstAsync(x => x.DriverId == me.Id, ct);
                switch (kind)
                {
                    case DocumentKind.License:
                        if (number is not null) d.LicenseNo = number;
                        d.LicenseExpiresAt = expiresAt;
                        break;
                    case DocumentKind.SmartCard:
                        if (number is not null) d.SmartCardNo = number;
                        d.SmartCardExpiresAt = expiresAt;
                        break;
                }
            }
            else
            {
                switch (kind)
                {
                    case DocumentKind.VehicleCard: if (number is not null) veh.VehicleCardNo = number; break;
                    case DocumentKind.Insurance:
                        if (number is not null) veh.InsurancePolicyNo = number;
                        veh.InsuranceExpiresAt = expiresAt;
                        break;
                    case DocumentKind.Inspection: veh.InspectionExpiresAt = expiresAt; break;
                }
            }
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"«{DocumentKind.Label(kind)}» بارگذاری شد و در صف بررسی مدیر قرار گرفت. نتیجه با اعلان به شما می‌رسد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return back;
    }

    // ------------------------------------------------------------------ هشدار انقضا

    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        ViewData["Title"] = "هشدار انقضای مدارک";
        var warnDays = await settings.GetIntAsync(SettingsService.Keys.ExpiryWarnDays, ct);
        var now = DateTime.UtcNow;
        var limit = now.AddDays(warnDays);

        var vehicles = await Mine().OrderByDescending(v => v.Status == VehicleStatus.Active).ThenByDescending(v => v.VehicleId).ToListAsync(ct);
        var ids = vehicles.Select(v => v.VehicleId).ToList();
        var plates = vehicles.ToDictionary(v => v.VehicleId, v => v.PlateNo);

        // فقط مدرکِ جاریِ هر (صاحب، نوع) — نسخهٔ قدیمیِ جایگزین‌شده هشدار نمی‌دهد
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.Status != AccountStatus.Rejected && d.ExpiresAt != null &&
                        ((d.OwnerKind == OwnerKind.Driver && d.OwnerId == me.Id) || (d.OwnerKind == OwnerKind.Vehicle && ids.Contains(d.OwnerId))))
            .OrderByDescending(d => d.UploadedAt).ToListAsync(ct);
        var current = docs.GroupBy(d => (d.OwnerKind, d.OwnerId, d.Kind)).Select(g => g.First())
            .Where(d => d.ExpiresAt < limit).OrderBy(d => d.ExpiresAt)
            .Select(d => new DocRowVm
            {
                Doc = d, IsCurrent = true,
                OwnerLabel = d.OwnerKind == OwnerKind.Vehicle ? $"خودروی {plates.GetValueOrDefault(d.OwnerId, "")}" : "شما"
            }).ToList();

        var driver = await db.Drivers.AsNoTracking().Where(d => d.DriverId == me.Id)
            .Select(d => new { d.LicenseExpiresAt, d.SmartCardExpiresAt }).FirstAsync(ct);
        var fields = new List<ExpiryFieldVm>
        {
            new("گواهینامه", driver.LicenseExpiresAt, "/Driver/Vehicle/Documents?kind=license"),
            new("کارت هوشمند", driver.SmartCardExpiresAt, "/Driver/Vehicle/Documents?kind=smart_card")
        };
        foreach (var v in vehicles)
        {
            fields.Add(new($"بیمه‌نامهٔ {Plate.Pretty(v.PlateNo)}", v.InsuranceExpiresAt, $"/Driver/Vehicle/Documents?kind=insurance&vehicleId={v.VehicleId}"));
            fields.Add(new($"معاینه فنی {Plate.Pretty(v.PlateNo)}", v.InspectionExpiresAt, $"/Driver/Vehicle/Documents?kind=inspection&vehicleId={v.VehicleId}"));
        }

        return View(new AlertsVm
        {
            Issues = await readiness.CheckAsync(me.Id, null, ct),
            Expired = current.Where(r => r.Doc.ExpiresAt < now).ToList(),
            Expiring = current.Where(r => r.Doc.ExpiresAt >= now).ToList(),
            Fields = fields,
            WarnDays = warnDays,
            BlockOnExpired = await settings.GetBoolAsync(SettingsService.Keys.BlockOnExpired, ct)
        });
    }

    // ------------------------------------------------------------------

    private IQueryable<Vehicle> Mine(bool tracking = false)
    {
        var q = tracking ? db.Vehicles : db.Vehicles.AsNoTracking();
        return q.Where(v => v.DriverId == me.Id);
    }

    private Task<bool> InLiveTripAsync(int vehicleId, CancellationToken ct) =>
        db.Trips.AnyAsync(t => t.VehicleId == vehicleId && TripStatus.Live.Contains(t.Status), ct);

    /// <summary>پلاک به قالب متعارف «12ب345-67» می‌رود (Plate.Normalize) تا یکتایی و جستجو در همهٔ پنل‌ها یک‌شکل باشد؛ ورودی غیرقابل‌تجزیه دست‌نخورده می‌ماند.</summary>
    private static string NormPlate(string? s) => Plate.Normalize(s);

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static string? CleanLatin(string? s, int max)
    {
        var v = Fa.Latin(s);
        if (v.Length == 0) return null;
        return v.Length > max ? v[..max] : v;
    }
}
