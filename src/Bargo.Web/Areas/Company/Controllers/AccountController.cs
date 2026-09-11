using System.Globalization;
using System.Net.Mail;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CompanyEntity = Bargo.Web.Models.Entities.Company;

namespace Bargo.Web.Areas.CompanyPanel.Controllers;

/// <summary>
/// «حساب شرکت» — مشخصات، مجوزها، مدارک، اطلاعات بانکی و تنظیمات.
///
/// برای شرکتِ در انتظار تأیید هم باز است ([AllowUnapproved]): بدون این صفحه‌ها
/// شرکت تازه نمی‌تواند مدارکی را که برای تأیید لازم است بفرستد. شناسهٔ ملی پس از
/// تأیید قفل می‌شود (هویت شرکت است)؛ بقیهٔ مشخصات همیشه قابل ویرایش‌اند.
///
/// این کنترلر با AccountController ریشه (ورود/خروج، مسیر /account) هم‌نام است ولی در
/// Area شرکت زندگی می‌کند؛ مسیرش /Company/Account/… است.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[AllowUnapproved]
public class AccountController(BargoDbContext db, CurrentUser me, DocumentStorage storage, AuditService audit) : Controller
{
    // ------------------------------------------------------------------
    //  مشخصات شرکت
    // ------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "مشخصات شرکت";
        var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == me.CompanyId, ct);
        if (c is null) return NotFound();
        await FillIndexAsync(c, ct);
        return View(new CompanyAccountForm
        {
            Name = c.Name, NationalId = c.NationalId, RegistrationNo = c.RegistrationNo, ManagerName = c.ManagerName,
            ManagerNationalCode = c.ManagerNationalCode, Mobile = c.Mobile, Phone = c.Phone, Email = c.Email,
            CityId = c.CityId, Address = c.Address
        });
    }

    [HttpPost]
    public async Task<IActionResult> Index(CompanyAccountForm vm, CancellationToken ct)
    {
        ViewData["Title"] = "مشخصات شرکت";
        var c = await db.Companies.FirstOrDefaultAsync(x => x.CompanyId == me.CompanyId, ct);
        if (c is null) return NotFound();
        var approved = c.Status == AccountStatus.Approved;

        var name = (vm.Name ?? "").Trim();
        var nationalId = approved ? c.NationalId : Fa.Latin(vm.NationalId);
        var registrationNo = Fa.Latin(vm.RegistrationNo);
        var managerName = (vm.ManagerName ?? "").Trim();
        var managerCode = Fa.Latin(vm.ManagerNationalCode);
        var mobile = Fa.NormMobile(vm.Mobile);
        var phone = IranId.NormPhone(vm.Phone);
        var email = vm.Email?.Trim();
        var address = vm.Address?.Trim();

        var errors = new List<string>();
        if (name.Length is < 3 or > 150) errors.Add("نام شرکت را (۳ تا ۱۵۰ نویسه) بنویسید");
        if (!approved)
        {
            if (!IranId.IsCompanyNationalId(nationalId)) errors.Add("شناسهٔ ملی شرکت (۱۱ رقم) معتبر نیست");
            else if (await db.Companies.AnyAsync(x => x.NationalId == nationalId && x.CompanyId != c.CompanyId, ct))
                errors.Add("شرکت دیگری با این شناسهٔ ملی در بارگو ثبت شده است");
        }
        if (registrationNo.Length > 0 && (registrationNo.Length > 30 || !registrationNo.All(char.IsAsciiDigit)))
            errors.Add("شمارهٔ ثبت فقط رقم و حداکثر ۳۰ رقم باشد");
        if (managerName.Length is < 3 or > 100) errors.Add("نام مدیرعامل را (۳ تا ۱۰۰ نویسه) بنویسید");
        if (managerCode.Length > 0 && !IranId.IsNationalCode(managerCode)) errors.Add("کد ملی مدیرعامل معتبر نیست");
        if (mobile.Length == 0) errors.Add("شمارهٔ موبایل شرکت معتبر نیست (۰۹xxxxxxxxx)");
        if (phone.Length > 0 && !IranId.IsPhone(phone))
            errors.Add("تلفن ثابت معتبر نیست (با کد شهر، مثلاً ۰۲۱۸۸۰۰۰۰۰۰)");
        if (!string.IsNullOrEmpty(email) && (email.Length > 120 || !MailAddress.TryCreate(email, out _)))
            errors.Add("نشانی ایمیل معتبر نیست");
        if (vm.CityId is int cityId && !await db.Cities.AnyAsync(x => x.CityId == cityId && x.IsActive, ct))
            errors.Add("شهر را از فهرست انتخاب کنید");
        if (address is { Length: > 500 }) errors.Add("نشانی حداکثر ۵۰۰ نویسه باشد");

        if (errors.Count > 0)
        {
            ViewBag.Errors = errors;
            await FillIndexAsync(c, ct);
            if (approved) vm.NationalId = c.NationalId;
            return View(vm);
        }

        var identityChanged = c.Name != name || c.NationalId != nationalId || (c.RegistrationNo ?? "") != registrationNo
                              || c.ManagerName != managerName || (c.ManagerNationalCode ?? "") != managerCode;

        audit.Add("Company", c.CompanyId, "profile", $"ویرایش مشخصات شرکت «{c.Name}»", new
        {
            from = new { c.Name, c.NationalId, c.RegistrationNo, c.ManagerName, c.ManagerNationalCode, c.Mobile, c.Phone, c.Email, c.CityId },
            to = new { name, nationalId, registrationNo, managerName, managerCode, mobile, phone, email, vm.CityId }
        });

        c.Name = name;
        c.NationalId = nationalId;
        c.RegistrationNo = registrationNo.Length > 0 ? registrationNo : null;
        c.ManagerName = managerName;
        c.ManagerNationalCode = managerCode.Length > 0 ? managerCode : null;
        c.Mobile = mobile;
        c.Phone = phone.Length > 0 ? phone : null;
        c.Email = string.IsNullOrEmpty(email) ? null : email;
        c.CityId = vm.CityId;
        c.Address = string.IsNullOrEmpty(address) ? null : address;
        await db.SaveChangesAsync(ct);

        TempData["ok"] = identityChanged && approved
            ? "مشخصات ذخیره شد. تغییر نام یا مدیرعامل شرکت در سوابق ثبت شد و ممکن است پشتیبانی برای تطبیق با مدارک با شما تماس بگیرد."
            : "مشخصات شرکت ذخیره شد.";
        return RedirectToAction(nameof(Index));
    }

    private async Task FillIndexAsync(CompanyEntity c, CancellationToken ct)
    {
        ViewBag.Status = c.Status;
        ViewBag.StatusReason = c.StatusReason;
        ViewBag.ApprovedAt = c.ApprovedAt;
        ViewBag.CreatedAt = c.CreatedAt;
        ViewBag.Rating = (c.RatingAvg, c.RatingCount);
        ViewBag.Approved = c.Status == AccountStatus.Approved;
        ViewBag.Cities = await CompanyOps.CitiesAsync(db, ct);
        ViewBag.Checks = await ChecksAsync(ct);
        ViewBag.UserCount = await db.CompanyUsers.CountAsync(u => u.CompanyId == c.CompanyId && u.IsActive, ct);
    }

    // ------------------------------------------------------------------
    //  مجوزها
    // ------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Licenses(CancellationToken ct)
    {
        ViewData["Title"] = "مجوزها";
        var c = await db.Companies.AsNoTracking().Where(x => x.CompanyId == me.CompanyId)
            .Select(x => new { x.LicenseNo, x.LicenseExpiresAt, x.Status }).FirstOrDefaultAsync(ct);
        if (c is null) return NotFound();

        ViewBag.LicenseNo = c.LicenseNo;
        ViewBag.LicenseExpiresAt = c.LicenseExpiresAt;
        ViewBag.Status = c.Status;
        ViewBag.Check = (await ChecksAsync(ct)).First(k => k.Kind == DocumentKind.CompanyLicense);
        ViewBag.History = await DocsAsync(DocumentKind.CompanyLicense, ct);
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Licenses(string? licenseNo, DateTime? licenseExpiresAt, IFormFile? file, CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(x => x.CompanyId == me.CompanyId, ct);
        if (c is null) return NotFound();

        try
        {
            if (!ModelState.IsValid) throw new UserError("تاریخ انقضا معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            var no = Fa.Latin(licenseNo);
            if (no.Length > 40) throw new UserError("شمارهٔ مجوز حداکثر ۴۰ نویسه باشد.");
            var hasFile = file is { Length: > 0 };
            if (hasFile && no.Length == 0) throw new UserError("برای بارگذاری مجوز، شمارهٔ آن را هم وارد کنید.");
            if (hasFile && licenseExpiresAt is null) throw new UserError("تاریخ انقضای مجوز را وارد کنید.");

            var changed = (c.LicenseNo ?? "") != no || c.LicenseExpiresAt != licenseExpiresAt;
            if (changed)
            {
                audit.Add("Company", c.CompanyId, "license", "ویرایش مجوز فعالیت شرکت",
                    new { from = new { c.LicenseNo, c.LicenseExpiresAt }, to = new { no, licenseExpiresAt } });
                c.LicenseNo = no.Length > 0 ? no : null;
                c.LicenseExpiresAt = licenseExpiresAt;
            }

            if (hasFile)
            {
                var path = await storage.SaveAsync(file, "docs", ct);
                var d = new Document
                {
                    OwnerKind = OwnerKind.Company, OwnerId = c.CompanyId, Kind = DocumentKind.CompanyLicense,
                    Number = no, ExpiresAt = licenseExpiresAt, FilePath = path, Status = AccountStatus.Pending
                };
                db.Documents.Add(d);
                await db.SaveChangesAsync(ct);
                audit.Add("Document", d.DocumentId, "upload", "بارگذاری مجوز فعالیت شرکت", new { d.OwnerKind, d.OwnerId, d.Number, d.ExpiresAt });
                await db.SaveChangesAsync(ct);
                TempData["ok"] = "مجوز فعالیت بارگذاری شد و در صف بررسی مدیر سامانه قرار گرفت. نتیجه به شما اعلان می‌شود.";
            }
            else if (changed)
            {
                await db.SaveChangesAsync(ct);
                TempData["ok"] = "شماره و تاریخ مجوز ذخیره شد. تصویر مجوز را هم بارگذاری کنید تا بررسی شود.";
            }
            else TempData["ok"] = "تغییری داده نشد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Licenses));
    }

    // ------------------------------------------------------------------
    //  مدارک
    // ------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Documents(string? kind, CancellationToken ct)
    {
        ViewData["Title"] = "مدارک شرکت";
        var status = await db.Companies.AsNoTracking().Where(x => x.CompanyId == me.CompanyId).Select(x => x.Status).FirstOrDefaultAsync(ct);
        if (status is null) return NotFound();

        ViewBag.Status = status;
        ViewBag.Kind = DocumentKind.ForCompany.Contains(kind) ? kind : null;
        ViewBag.Checks = await ChecksAsync(ct);
        ViewBag.Docs = await DocsAsync(null, ct);
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Documents(string? kind, string? number, DateTime? expiresAt, IFormFile? file, CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(x => x.CompanyId == me.CompanyId, ct);
        if (c is null) return NotFound();

        try
        {
            if (!ModelState.IsValid) throw new UserError("تاریخ انقضا معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            if (kind is null || !DocumentKind.ForCompany.Contains(kind)) throw new UserError("نوع مدرک را انتخاب کنید.");
            if (file is null || file.Length == 0) throw new UserError("تصویر یا فایل PDF مدرک را انتخاب کنید.");
            var no = Fa.Latin(number);
            if (no.Length > 40) throw new UserError("شمارهٔ مدرک حداکثر ۴۰ نویسه باشد.");
            if (kind == DocumentKind.CompanyLicense && expiresAt is null) throw new UserError("مجوز فعالیت تاریخ انقضا دارد؛ آن را وارد کنید.");
            if (expiresAt is DateTime e && e < DateTime.UtcNow.AddDays(-1)) throw new UserError("مدرکِ منقضی پذیرفته نمی‌شود؛ نسخهٔ تمدیدشده را بارگذاری کنید.");

            var path = await storage.SaveAsync(file, "docs", ct);
            var d = new Document
            {
                OwnerKind = OwnerKind.Company, OwnerId = c.CompanyId, Kind = kind,
                Number = no.Length > 0 ? no : null, ExpiresAt = expiresAt, FilePath = path, Status = AccountStatus.Pending
            };
            db.Documents.Add(d);

            // مجوز فعالیت روی خودِ شرکت هم ستون دارد — یک منبع، نه دو
            if (kind == DocumentKind.CompanyLicense)
            {
                if (no.Length > 0) c.LicenseNo = no;
                c.LicenseExpiresAt = expiresAt;
            }

            await db.SaveChangesAsync(ct);
            audit.Add("Document", d.DocumentId, "upload", $"بارگذاری «{DocumentKind.Label(kind)}» شرکت", new { d.OwnerKind, d.OwnerId, d.Kind, d.Number, d.ExpiresAt });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"«{DocumentKind.Label(kind)}» بارگذاری شد و در صف بررسی قرار گرفت. نتیجه به شما اعلان می‌شود.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Documents));
    }

    // ------------------------------------------------------------------
    //  اطلاعات بانکی
    // ------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Bank(CancellationToken ct)
    {
        ViewData["Title"] = "اطلاعات بانکی";
        var c = await db.Companies.AsNoTracking().Where(x => x.CompanyId == me.CompanyId)
            .Select(x => new { x.Name, x.Sheba, x.BankName, x.WalletBalance }).FirstOrDefaultAsync(ct);
        if (c is null) return NotFound();

        ViewBag.Holder = c.Name;
        ViewBag.Sheba = c.Sheba;
        ViewBag.BankName = c.BankName;
        ViewBag.WalletBalance = c.WalletBalance;
        ViewBag.CanEdit = await db.CompanyUsers.AnyAsync(u => u.CompanyUserId == me.Id && u.IsActive && u.Permissions.HasFlag(CompanyPermission.Users), ct);
        return View();
    }

    /// <summary>تغییر حساب مقصدِ تسویه فقط با دسترسی «کاربران» (مدیر شرکت) — حسابدار نمی‌تواند شبا را عوض کند.</summary>
    [HttpPost]
    [RequireCompanyPermission(CompanyPermission.Users)]
    public async Task<IActionResult> Bank(string? sheba, string? bankName, bool remove, CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(x => x.CompanyId == me.CompanyId, ct);
        if (c is null) return NotFound();

        if (remove)
        {
            if (c.Sheba is null) return RedirectToAction(nameof(Bank));
            audit.Add("Company", c.CompanyId, "bank", "حذف شمارهٔ شبا شرکت", new { from = new { c.Sheba, c.BankName } });
            c.Sheba = null;
            c.BankName = null;
            await db.SaveChangesAsync(ct);
            TempData["ok"] = "شمارهٔ شبا حذف شد. تا ثبت شبای تازه، درخواست برداشت ممکن نیست.";
            return RedirectToAction(nameof(Bank));
        }

        if (string.IsNullOrWhiteSpace(sheba) || !IranId.IsSheba(sheba))
        {
            TempData["err"] = "شمارهٔ شبا معتبر نیست. قالب درست: IR و ۲۴ رقم.";
            return RedirectToAction(nameof(Bank));
        }
        var bank = (bankName ?? "").Trim();
        if (bank.Length > 60)
        {
            TempData["err"] = "نام بانک حداکثر ۶۰ نویسه باشد.";
            return RedirectToAction(nameof(Bank));
        }

        var next = IranId.NormSheba(sheba);
        var nextBank = bank.Length > 0 ? bank : null;
        if (c.Sheba == next && c.BankName == nextBank)
        {
            TempData["ok"] = "تغییری داده نشد.";
            return RedirectToAction(nameof(Bank));
        }

        audit.Add("Company", c.CompanyId, "bank", "تغییر اطلاعات بانکی شرکت",
            new { from = new { c.Sheba, c.BankName }, to = new { Sheba = next, BankName = nextBank } });
        c.Sheba = next;
        c.BankName = nextBank;
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "اطلاعات بانکی ذخیره شد. تسویه‌های شرکت به همین حساب واریز می‌شود.";
        return RedirectToAction(nameof(Bank));
    }

    // ------------------------------------------------------------------
    //  تنظیمات
    // ------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        ViewData["Title"] = "تنظیمات";
        var c = await db.Companies.AsNoTracking().Where(x => x.CompanyId == me.CompanyId)
            .Select(x => new { x.AcceptsDirectRequests, x.DriverSharePercent }).FirstOrDefaultAsync(ct);
        if (c is null) return NotFound();
        var u = await db.CompanyUsers.AsNoTracking().Where(x => x.CompanyUserId == me.Id)
            .Select(x => new { x.Name, x.Mobile, x.Title, x.Permissions, x.IsOwner, x.LastLoginAt }).FirstOrDefaultAsync(ct);
        if (u is null) return NotFound();

        ViewBag.AcceptsDirectRequests = c.AcceptsDirectRequests;
        ViewBag.DriverSharePercent = c.DriverSharePercent;
        ViewBag.CanEdit = u.IsOwner || u.Permissions.HasFlag(CompanyPermission.Users);
        ViewBag.MyName = u.Name;
        ViewBag.MyMobile = u.Mobile;
        ViewBag.MyTitle = u.Title;
        ViewBag.MyLastLoginAt = u.LastLoginAt;
        return View();
    }

    /// <summary>تنظیمات شرکت (سهم راننده، پذیرش درخواست مستقیم) روی مالی اثر دارد — فقط مدیر شرکت.</summary>
    [HttpPost]
    [RequireCompanyPermission(CompanyPermission.Users)]
    public async Task<IActionResult> Settings(bool acceptsDirectRequests, string? driverSharePercent, CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(x => x.CompanyId == me.CompanyId, ct);
        if (c is null) return NotFound();

        var raw = Fa.Latin(driverSharePercent).Replace('٫', '.').Replace('/', '.');
        if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var pct) || pct < 0 || pct > 100)
        {
            TempData["err"] = "درصد سهم راننده باید عددی بین ۰ تا ۱۰۰ باشد.";
            return RedirectToAction(nameof(Settings));
        }
        pct = Math.Round(pct, 1);

        if (c.AcceptsDirectRequests == acceptsDirectRequests && c.DriverSharePercent == pct)
        {
            TempData["ok"] = "تغییری داده نشد.";
            return RedirectToAction(nameof(Settings));
        }

        audit.Add("Company", c.CompanyId, "settings", "تغییر تنظیمات شرکت",
            new { from = new { c.AcceptsDirectRequests, c.DriverSharePercent }, to = new { acceptsDirectRequests, DriverSharePercent = pct } });
        c.AcceptsDirectRequests = acceptsDirectRequests;
        c.DriverSharePercent = pct;
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "تنظیمات ذخیره شد. سهم تازه فقط روی سفرهایی که از این پس تخصیص می‌یابند اعمال می‌شود.";
        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    public async Task<IActionResult> ChangePassword(string? current, string? next, string? confirm, CancellationToken ct)
    {
        var u = await db.CompanyUsers.FirstOrDefaultAsync(x => x.CompanyUserId == me.Id && x.CompanyId == me.CompanyId, ct);
        if (u is null) return NotFound();

        if (string.IsNullOrEmpty(current) || !PasswordHasher.Verify(current, u.PassHash))
            TempData["err"] = "گذرواژهٔ فعلی نادرست است.";
        else if ((next ?? "").Length < 6)
            TempData["err"] = "گذرواژهٔ تازه دست‌کم ۶ نویسه باشد.";
        else if (next != confirm)
            TempData["err"] = "گذرواژهٔ تازه و تکرار آن یکی نیستند.";
        else if (next == current)
            TempData["err"] = "گذرواژهٔ تازه با گذرواژهٔ فعلی یکی است.";
        else
        {
            u.PassHash = PasswordHasher.Hash(next!);
            audit.Add("CompanyUser", u.CompanyUserId, "password", "تغییر گذرواژه توسط خود کاربر");
            await db.SaveChangesAsync(ct);
            TempData["ok"] = "گذرواژه تغییر کرد.";
        }
        return RedirectToAction(nameof(Settings));
    }

    // ------------------------------------------------------------------

    /// <summary>وضعیت هر مدرکِ الزامی شرکت از روی آخرین نسخهٔ بارگذاری‌شده.</summary>
    private async Task<List<CompanyDocCheck>> ChecksAsync(CancellationToken ct)
    {
        var docs = await db.Documents.AsNoTracking().Of(me.Owner)
            .Where(d => DocumentKind.ForCompany.Contains(d.Kind))
            .OrderByDescending(d => d.UploadedAt).ToListAsync(ct);
        return DocumentKind.ForCompany
            .Select(k => CompanyDocCheck.For(k, docs.FirstOrDefault(d => d.Kind == k)))
            .ToList();
    }

    private Task<List<CompanyDocRow>> DocsAsync(string? kind, CancellationToken ct)
    {
        var q = db.Documents.AsNoTracking().Of(me.Owner);
        if (kind is not null) q = q.Where(d => d.Kind == kind);
        return q.OrderByDescending(d => d.UploadedAt).Select(d => new CompanyDocRow
        {
            DocumentId = d.DocumentId, Kind = d.Kind, Number = d.Number, ExpiresAt = d.ExpiresAt, FilePath = d.FilePath,
            Status = d.Status, ReviewNote = d.ReviewNote, ReviewedAt = d.ReviewedAt, UploadedAt = d.UploadedAt
        }).ToListAsync(ct);
    }
}
