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

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// حساب کاربری راننده: مشخصات، گواهینامه و کارت هوشمند، شبا، گذرواژه.
/// [AllowUnapproved]: رانندهٔ در انتظار تأیید یا ردشده باید بتواند همین‌جا اطلاعاتش را درست کند.
///
/// پس از تأیید، نام و کد ملی قفل می‌شوند: مدیر همان هویت را تأیید کرده و تغییرش
/// یعنی احراز هویت دوباره از صفر. رانندهٔ ردشده با ذخیرهٔ مشخصات به صف بررسی برمی‌گردد.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
[AllowUnapproved]
public class ProfileController(BargoDbContext db, CurrentUser me, SettingsService settings) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "حساب کاربری";
        var d = await db.Drivers.AsNoTracking().Include(x => x.City).Include(x => x.Company).FirstOrDefaultAsync(x => x.DriverId == me.Id, ct);
        if (d is null) return NotFound();

        var form = new ProfileFormVm
        {
            FirstName = d.FirstName, LastName = d.LastName, NationalCode = d.NationalCode, BirthDate = d.BirthDate,
            CityId = d.CityId, Address = d.Address, LicenseNo = d.LicenseNo, LicenseExpiresAt = d.LicenseExpiresAt,
            SmartCardNo = d.SmartCardNo, SmartCardExpiresAt = d.SmartCardExpiresAt, Sheba = d.Sheba
        };
        return View(await BuildAsync(d, form, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Index(ProfileFormVm form, CancellationToken ct)
    {
        ViewData["Title"] = "حساب کاربری";
        var d = await db.Drivers.Include(x => x.City).Include(x => x.Company).FirstOrDefaultAsync(x => x.DriverId == me.Id, ct);
        if (d is null) return NotFound();
        var locked = d.Status == AccountStatus.Approved;

        var first = form.FirstName?.Trim() ?? "";
        var last = form.LastName?.Trim() ?? "";
        var nationalCode = Fa.Latin(form.NationalCode);
        var address = form.Address?.Trim();
        var licenseNo = Fa.Latin(form.LicenseNo);
        var smartCardNo = Fa.Latin(form.SmartCardNo);
        var sheba = Fa.Latin(form.Sheba).Replace(" ", "");
        var now = DateTime.UtcNow;

        if (!locked)
        {
            if (first.Length is < 2 or > 60) ModelState.AddModelError(nameof(form.FirstName), "نام را کامل بنویسید (۲ تا ۶۰ نویسه).");
            if (last.Length is < 2 or > 60) ModelState.AddModelError(nameof(form.LastName), "نام خانوادگی را کامل بنویسید (۲ تا ۶۰ نویسه).");
            if (!IranId.IsNationalCode(nationalCode)) ModelState.AddModelError(nameof(form.NationalCode), "کد ملی ۱۰ رقمی معتبر نیست.");
            else if (await db.Drivers.AnyAsync(x => x.NationalCode == nationalCode && x.DriverId != me.Id, ct))
                ModelState.AddModelError(nameof(form.NationalCode), "با این کد ملی حساب رانندهٔ دیگری ثبت شده است.");
        }
        if (form.BirthDate is DateTime b)
        {
            if (b > now.AddYears(-18)) ModelState.AddModelError(nameof(form.BirthDate), "راننده باید دست‌کم ۱۸ سال داشته باشد.");
            else if (b < now.AddYears(-90)) ModelState.AddModelError(nameof(form.BirthDate), "تاریخ تولد معتبر نیست.");
        }
        if (form.CityId is null || !await db.Cities.AnyAsync(c => c.CityId == form.CityId && c.IsActive, ct))
            ModelState.AddModelError(nameof(form.CityId), "شهر محل سکونت را از فهرست انتخاب کنید؛ «بارهای نزدیک من» بر همین اساس است.");
        if (address is { Length: > 500 }) ModelState.AddModelError(nameof(form.Address), "نشانی حداکثر ۵۰۰ نویسه باشد.");
        if (licenseNo.Length > 30) ModelState.AddModelError(nameof(form.LicenseNo), "شمارهٔ گواهینامه حداکثر ۳۰ نویسه باشد.");
        if (smartCardNo.Length > 30) ModelState.AddModelError(nameof(form.SmartCardNo), "شمارهٔ کارت هوشمند حداکثر ۳۰ نویسه باشد.");
        if (licenseNo.Length > 0 && form.LicenseExpiresAt is null) ModelState.AddModelError(nameof(form.LicenseExpiresAt), "تاریخ انقضای گواهینامه را وارد کنید.");
        if (smartCardNo.Length > 0 && form.SmartCardExpiresAt is null) ModelState.AddModelError(nameof(form.SmartCardExpiresAt), "تاریخ انقضای کارت هوشمند را وارد کنید.");
        if (sheba.Length > 0 && !IranId.IsSheba(sheba)) ModelState.AddModelError(nameof(form.Sheba), "شمارهٔ شبا معتبر نیست. قالب درست: IR و ۲۴ رقم.");

        if (!ModelState.IsValid) return View(await BuildAsync(d, form, ct));

        if (!locked)
        {
            d.FirstName = first;
            d.LastName = last;
            d.NationalCode = nationalCode;
        }
        d.BirthDate = form.BirthDate;
        d.CityId = form.CityId;
        d.Address = string.IsNullOrEmpty(address) ? null : address;
        d.LicenseNo = licenseNo.Length > 0 ? licenseNo : null;
        d.LicenseExpiresAt = licenseNo.Length > 0 ? form.LicenseExpiresAt : null;
        d.SmartCardNo = smartCardNo.Length > 0 ? smartCardNo : null;
        d.SmartCardExpiresAt = smartCardNo.Length > 0 ? form.SmartCardExpiresAt : null;
        d.Sheba = sheba.Length > 0 ? IranId.NormSheba(sheba) : null;

        // رانندهٔ ردشده با اصلاح مشخصات دوباره به صف بررسی مدیر می‌رود
        var resubmitted = d.Status == AccountStatus.Rejected;
        if (resubmitted)
        {
            d.Status = AccountStatus.Pending;
            d.StatusReason = null;
        }
        await db.SaveChangesAsync(ct);

        if (d.FullName != me.Name) await RefreshNameAsync(d.FullName);

        TempData["ok"] = resubmitted
            ? "مشخصات ذخیره شد و حساب شما دوباره در صف بررسی مدیر قرار گرفت."
            : "مشخصات ذخیره شد.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Password(string? current, string? next, string? confirm, CancellationToken ct)
    {
        var d = await db.Drivers.FirstOrDefaultAsync(x => x.DriverId == me.Id, ct);
        if (d is null) return NotFound();

        if (string.IsNullOrEmpty(current) || !PasswordHasher.Verify(current, d.PassHash))
            TempData["err"] = "گذرواژهٔ فعلی نادرست است.";
        else if ((next ?? "").Length < 6)
            TempData["err"] = "گذرواژهٔ تازه دست‌کم ۶ نویسه باشد.";
        else if (next != confirm)
            TempData["err"] = "گذرواژهٔ تازه و تکرار آن یکی نیستند.";
        else if (next == current)
            TempData["err"] = "گذرواژهٔ تازه با گذرواژهٔ فعلی یکی است.";
        else
        {
            d.PassHash = PasswordHasher.Hash(next!);
            await db.SaveChangesAsync(ct);
            TempData["ok"] = "گذرواژه تغییر کرد. از این پس با گذرواژهٔ تازه وارد شوید.";
        }
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------

    private async Task<ProfileVm> BuildAsync(Models.Entities.Driver d, ProfileFormVm form, CancellationToken ct) => new()
    {
        Driver = d,
        Form = form,
        Cities = await db.CityListAsync(ct),
        Provinces = await db.ProvinceListAsync(ct),
        CompanyName = d.Company?.Name,
        IntervalMin = await settings.GetIntAsync(SettingsService.Keys.TrackIntervalMin, ct)
    };

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
