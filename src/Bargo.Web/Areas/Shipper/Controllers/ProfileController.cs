using System.Net.Mail;
using System.Security.Claims;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// حساب کاربری صاحب بار: مشخصات، احراز هویت، آدرس‌های منتخب، شبا و تنظیمات.
/// برای حساب تعلیق‌شده هم باز است تا بتواند مدارک و مشخصاتش را اصلاح کند.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
[AllowUnapproved]
public class ProfileController(BargoDbContext db, CurrentUser me, DocumentStorage storage) : Controller
{
    private const int MaxAddresses = 30;

    // ------------------------------------------------------------------ مشخصات

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "مشخصات حساب";
        var s = await db.Shippers.AsNoTracking().FirstOrDefaultAsync(x => x.ShipperId == me.Id, ct);
        if (s is null) return NotFound();
        await FillAsync(ct);
        ViewBag.Mobile = s.Mobile;
        ViewBag.VerifyStatus = s.VerifyStatus;
        ViewBag.CreatedAt = s.CreatedAt;
        ViewBag.Rating = (s.RatingAvg, s.RatingCount);
        return View(new ShipperProfileForm
        {
            Kind = s.Kind, FullName = s.FullName, BusinessName = s.BusinessName, NationalCode = s.NationalCode,
            NationalId = s.NationalId, EconomicCode = s.EconomicCode, CityId = s.CityId, Address = s.Address, Email = s.Email
        });
    }

    [HttpPost]
    public async Task<IActionResult> Index(ShipperProfileForm vm, CancellationToken ct)
    {
        ViewData["Title"] = "مشخصات حساب";
        var s = await db.Shippers.FirstOrDefaultAsync(x => x.ShipperId == me.Id, ct);
        if (s is null) return NotFound();

        var kind = vm.Kind == "business" ? "business" : "person";
        var fullName = vm.FullName?.Trim() ?? "";
        var business = vm.BusinessName?.Trim();
        var nationalCode = Fa.Latin(vm.NationalCode);
        var nationalId = Fa.Latin(vm.NationalId);
        var economic = Fa.Latin(vm.EconomicCode);
        var email = vm.Email?.Trim();
        var address = vm.Address?.Trim();

        if (fullName.Length is < 3 or > 100) ModelState.AddModelError(nameof(vm.FullName), "نام و نام خانوادگی را کامل بنویسید (۳ تا ۱۰۰ نویسه).");
        if (kind == "business" && string.IsNullOrWhiteSpace(business)) ModelState.AddModelError(nameof(vm.BusinessName), "نام کسب‌وکار را وارد کنید.");
        if (business is { Length: > 150 }) ModelState.AddModelError(nameof(vm.BusinessName), "نام کسب‌وکار حداکثر ۱۵۰ نویسه باشد.");
        if (nationalCode.Length > 0 && !IranId.IsNationalCode(nationalCode)) ModelState.AddModelError(nameof(vm.NationalCode), "کد ملی معتبر نیست.");
        if (kind == "business" && nationalId.Length > 0 && !IranId.IsCompanyNationalId(nationalId))
            ModelState.AddModelError(nameof(vm.NationalId), "شناسهٔ ملی شرکت (۱۱ رقم) معتبر نیست.");
        if (kind == "business" && economic.Length > 0 && (economic.Length is < 10 or > 14 || !economic.All(char.IsAsciiDigit)))
            ModelState.AddModelError(nameof(vm.EconomicCode), "کد اقتصادی باید ۱۰ تا ۱۴ رقم باشد.");
        if (!string.IsNullOrEmpty(email) && (email.Length > 120 || !MailAddress.TryCreate(email, out _)))
            ModelState.AddModelError(nameof(vm.Email), "نشانی ایمیل معتبر نیست.");
        if (vm.CityId is int cid && !await db.Cities.AnyAsync(c => c.CityId == cid, ct))
            ModelState.AddModelError(nameof(vm.CityId), "شهر را از فهرست انتخاب کنید.");
        if (address is { Length: > 500 }) ModelState.AddModelError(nameof(vm.Address), "نشانی حداکثر ۵۰۰ نویسه باشد.");

        if (!ModelState.IsValid)
        {
            await FillAsync(ct);
            ViewBag.Mobile = s.Mobile;
            ViewBag.VerifyStatus = s.VerifyStatus;
            ViewBag.CreatedAt = s.CreatedAt;
            ViewBag.Rating = (s.RatingAvg, s.RatingCount);
            return View(vm);
        }

        var newBusiness = kind == "business" ? business : null;
        var newNationalId = kind == "business" && nationalId.Length > 0 ? nationalId : null;
        var identityChanged = s.Kind != kind || s.FullName != fullName || (s.NationalCode ?? "") != nationalCode ||
                              (s.BusinessName ?? "") != (newBusiness ?? "") || (s.NationalId ?? "") != (newNationalId ?? "");

        s.Kind = kind;
        s.FullName = fullName;
        s.BusinessName = newBusiness;
        s.NationalCode = nationalCode.Length > 0 ? nationalCode : null;
        s.NationalId = newNationalId;
        s.EconomicCode = kind == "business" && economic.Length > 0 ? economic : null;
        s.CityId = vm.CityId;
        s.Address = string.IsNullOrEmpty(address) ? null : address;
        s.Email = string.IsNullOrEmpty(email) ? null : email;

        var reverify = identityChanged && s.VerifyStatus == AccountStatus.Approved;
        if (reverify)
        {
            s.VerifyStatus = AccountStatus.Pending;
            s.VerifiedAt = null;
        }
        await db.SaveChangesAsync(ct);

        if (fullName != me.Name) await RefreshNameAsync(fullName);

        TempData["ok"] = reverify
            ? "مشخصات ذخیره شد. چون اطلاعات هویتی تغییر کرد، احراز هویت دوباره بررسی می‌شود."
            : "مشخصات ذخیره شد.";
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------ احراز هویت

    [HttpGet]
    public async Task<IActionResult> Verification(CancellationToken ct)
    {
        ViewData["Title"] = "احراز هویت";
        var s = await db.Shippers.AsNoTracking().FirstOrDefaultAsync(x => x.ShipperId == me.Id, ct);
        if (s is null) return NotFound();

        var missing = new List<string>();
        if (string.IsNullOrEmpty(s.NationalCode)) missing.Add("کد ملی");
        if (s.Kind == "business" && string.IsNullOrEmpty(s.NationalId)) missing.Add("شناسهٔ ملی شرکت");
        if (s.CityId is null) missing.Add("شهر");

        return View(new ShipperVerificationVm
        {
            Kind = s.Kind,
            VerifyStatus = s.VerifyStatus,
            VerifiedAt = s.VerifiedAt,
            MissingProfile = missing,
            ProfileComplete = missing.Count == 0,
            Docs = await db.Documents.AsNoTracking().Of(me.Owner).OrderByDescending(d => d.UploadedAt).ToListAsync(ct),
            RequiredKinds = s.Kind == "business" ? [DocumentKind.Registration, DocumentKind.NationalCard] : [DocumentKind.NationalCard]
        });
    }

    [HttpPost]
    public async Task<IActionResult> Verification(string? kind, string? number, IFormFile? file, CancellationToken ct)
    {
        if (kind is null || !DocumentKind.ForShipper.Contains(kind))
        {
            TempData["err"] = "نوع مدرک را انتخاب کنید.";
            return RedirectToAction(nameof(Verification));
        }
        if (file is null || file.Length == 0)
        {
            TempData["err"] = "تصویر یا فایل PDF مدرک را انتخاب کنید.";
            return RedirectToAction(nameof(Verification));
        }
        number = Fa.Latin(number);
        if (number.Length > 40) number = number[..40];

        var s = await db.Shippers.FirstOrDefaultAsync(x => x.ShipperId == me.Id, ct);
        if (s is null) return NotFound();

        try
        {
            var path = await storage.SaveAsync(file, "docs", ct);
            db.Documents.Add(new Document
            {
                OwnerKind = OwnerKind.Shipper, OwnerId = me.Id, Kind = kind,
                Number = number.Length > 0 ? number : null, FilePath = path, Status = AccountStatus.Pending
            });
            s.VerifyStatus = AccountStatus.Pending;
            s.VerifiedAt = null;
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"«{DocumentKind.Label(kind)}» بارگذاری شد و در صف بررسی قرار گرفت. نتیجه به شما اعلان می‌شود.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Verification));
    }

    // ------------------------------------------------------------------ آدرس‌های منتخب

    [HttpGet]
    public async Task<IActionResult> Addresses(CancellationToken ct)
    {
        ViewData["Title"] = "آدرس‌های منتخب";
        await FillAsync(ct);
        var list = await db.SavedAddresses.AsNoTracking().Include(a => a.City).ThenInclude(c => c!.Province)
            .Where(a => a.ShipperId == me.Id).OrderBy(a => a.Title).ToListAsync(ct);
        return View(list);
    }

    [HttpPost]
    public async Task<IActionResult> Addresses(string? title, int? cityId, string? address, string? lat, string? lng,
        string? contactName, string? contactMobile, CancellationToken ct)
    {
        title = title?.Trim() ?? "";
        address = address?.Trim() ?? "";
        contactName = contactName?.Trim();
        var errors = new List<string>();

        if (title.Length is < 2 or > 60) errors.Add("عنوان آدرس (۲ تا ۶۰ نویسه) را بنویسید");
        if (cityId is null || !await db.Cities.AnyAsync(c => c.CityId == cityId && c.IsActive, ct)) errors.Add("شهر را انتخاب کنید");
        if (address.Length is < 5 or > 500) errors.Add("نشانی را کامل بنویسید");
        double? la = null, lo = null;
        if (!string.IsNullOrWhiteSpace(lat) || !string.IsNullOrWhiteSpace(lng))
        {
            la = ShipperUi.ParseDouble(lat);
            lo = ShipperUi.ParseDouble(lng);
            if (la is null || lo is null || la is < 24 or > 40 || lo is < 44 or > 64)
                errors.Add("مختصات معتبر نیست (عرض ۲۴ تا ۴۰، طول ۴۴ تا ۶۴)");
        }
        string? mobile = null;
        if (!string.IsNullOrWhiteSpace(contactMobile))
        {
            mobile = Fa.NormMobile(contactMobile);
            if (mobile.Length == 0) errors.Add("موبایل تحویل‌گیرنده معتبر نیست");
        }
        if (contactName is { Length: > 100 }) errors.Add("نام تحویل‌گیرنده حداکثر ۱۰۰ نویسه باشد");
        if (await db.SavedAddresses.CountAsync(a => a.ShipperId == me.Id, ct) >= MaxAddresses)
            errors.Add($"حداکثر {Fa.N(MaxAddresses)} آدرس منتخب مجاز است؛ یکی را حذف کنید");
        if (title.Length > 0 && await db.SavedAddresses.AnyAsync(a => a.ShipperId == me.Id && a.Title == title, ct))
            errors.Add("آدرسی با همین عنوان دارید");

        if (errors.Count > 0)
        {
            TempData["err"] = string.Join("؛ ", errors) + ".";
            return RedirectToAction(nameof(Addresses));
        }

        db.SavedAddresses.Add(new SavedAddress
        {
            ShipperId = me.Id, Title = title, CityId = cityId!.Value, Address = address, Lat = la, Lng = lo,
            ContactName = string.IsNullOrEmpty(contactName) ? null : contactName, ContactMobile = mobile
        });
        await db.SaveChangesAsync(ct);
        TempData["ok"] = $"آدرس «{title}» ذخیره شد و در فرم ثبت بار قابل انتخاب است.";
        return RedirectToAction(nameof(Addresses));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteAddress(int id, CancellationToken ct)
    {
        var n = await db.SavedAddresses.Where(a => a.SavedAddressId == id && a.ShipperId == me.Id).ExecuteDeleteAsync(ct);
        if (n == 0) return NotFound();
        TempData["ok"] = "آدرس حذف شد.";
        return RedirectToAction(nameof(Addresses));
    }

    // ------------------------------------------------------------------ اطلاعات مالی

    [HttpGet]
    public async Task<IActionResult> Bank(CancellationToken ct)
    {
        ViewData["Title"] = "اطلاعات مالی";
        var s = await db.Shippers.AsNoTracking().Where(x => x.ShipperId == me.Id)
            .Select(x => new { x.Sheba, x.FullName, x.BusinessName, x.Kind }).FirstOrDefaultAsync(ct);
        if (s is null) return NotFound();
        ViewBag.Sheba = s.Sheba;
        ViewBag.Holder = s.Kind == "business" && !string.IsNullOrEmpty(s.BusinessName) ? s.BusinessName : s.FullName;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Bank(string? sheba, bool remove, CancellationToken ct)
    {
        var s = await db.Shippers.FirstOrDefaultAsync(x => x.ShipperId == me.Id, ct);
        if (s is null) return NotFound();

        if (remove)
        {
            s.Sheba = null;
            await db.SaveChangesAsync(ct);
            TempData["ok"] = "شمارهٔ شبا حذف شد.";
            return RedirectToAction(nameof(Bank));
        }
        if (string.IsNullOrWhiteSpace(sheba) || !IranId.IsSheba(sheba))
        {
            TempData["err"] = "شمارهٔ شبا معتبر نیست. قالب درست: IR و ۲۴ رقم.";
            return RedirectToAction(nameof(Bank));
        }
        s.Sheba = IranId.NormSheba(sheba);
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "شمارهٔ شبا ذخیره شد. استرداد وجه در صورت نیاز به همین حساب واریز می‌شود.";
        return RedirectToAction(nameof(Bank));
    }

    // ------------------------------------------------------------------ تنظیمات

    [HttpGet]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        ViewData["Title"] = "تنظیمات";
        var s = await db.Shippers.AsNoTracking().Where(x => x.ShipperId == me.Id)
            .Select(x => new { x.NotifySms, x.Mobile }).FirstOrDefaultAsync(ct);
        if (s is null) return NotFound();
        ViewBag.NotifySms = s.NotifySms;
        ViewBag.Mobile = s.Mobile;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Settings(bool notifySms, CancellationToken ct)
    {
        var n = await db.Shippers.Where(x => x.ShipperId == me.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.NotifySms, notifySms), ct);
        if (n == 0) return NotFound();
        TempData["ok"] = notifySms ? "اطلاع‌رسانی پیامکی روشن شد." : "اطلاع‌رسانی پیامکی خاموش شد. کد تحویل همچنان برای گیرنده پیامک می‌شود.";
        return RedirectToAction(nameof(Settings));
    }

    [HttpPost]
    public async Task<IActionResult> ChangePassword(string? current, string? next, string? confirm, CancellationToken ct)
    {
        var s = await db.Shippers.FirstOrDefaultAsync(x => x.ShipperId == me.Id, ct);
        if (s is null) return NotFound();

        if (string.IsNullOrEmpty(current) || !PasswordHasher.Verify(current, s.PassHash))
            TempData["err"] = "گذرواژهٔ فعلی نادرست است.";
        else if ((next ?? "").Length < 6)
            TempData["err"] = "گذرواژهٔ تازه دست‌کم ۶ نویسه باشد.";
        else if (next != confirm)
            TempData["err"] = "گذرواژهٔ تازه و تکرار آن یکی نیستند.";
        else if (next == current)
            TempData["err"] = "گذرواژهٔ تازه با گذرواژهٔ فعلی یکی است.";
        else
        {
            s.PassHash = PasswordHasher.Hash(next!);
            await db.SaveChangesAsync(ct);
            TempData["ok"] = "گذرواژه تغییر کرد.";
        }
        return RedirectToAction(nameof(Settings));
    }

    // ------------------------------------------------------------------

    private async Task FillAsync(CancellationToken ct) =>
        ViewBag.Cities = await db.Cities.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.ProvinceId).ThenBy(c => c.Name)
            .Select(c => new ShipperCityOption { CityId = c.CityId, Name = c.Name, Province = c.Province!.Name, Lat = c.Lat, Lng = c.Lng })
            .ToListAsync(ct);

    /// <summary>نام نمایشی در کوکی ورود — تا نوار بالا بی‌خروج و ورود دوباره به‌روز شود.</summary>
    private async Task RefreshNameAsync(string name)
    {
        var claims = User.Claims.Where(c => c.Type != CurrentUser.ClaimName).ToList();
        claims.Add(new Claim(CurrentUser.ClaimName, name));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }
}
