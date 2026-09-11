using System.Security.Claims;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers;

/// <summary>
/// ورود و ثبت‌نام چهار نقش. ورود با موبایل + گذرواژه و انتخاب نقش، چون یک شماره
/// می‌تواند هم راننده باشد و هم صاحب بار (دو حسابِ مستقل).
///
/// TODO: ورود با کد پیامکی (جدول Otps آماده است) و محدودیت تعداد تلاش ناموفق.
/// </summary>
[Route("account")]
public class AccountController(BargoDbContext db, NotificationService notify) : Controller
{
    [HttpGet("login")]
    public IActionResult Login(string? role, string? returnUrl, int? disabled)
    {
        if (User.Identity?.IsAuthenticated == true && string.IsNullOrEmpty(returnUrl))
            return Redirect("/" + Roles.AreaOf(User.FindFirstValue(ClaimTypes.Role) ?? ""));
        ViewData["Title"] = "ورود";
        if (disabled == 1) TempData["err"] = "حساب کاربری شما غیرفعال شده است.";
        return View(new LoginVm { Role = role is Roles.Driver or Roles.Company or Roles.Admin ? role : Roles.Shipper, ReturnUrl = returnUrl });
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm, CancellationToken ct)
    {
        ViewData["Title"] = "ورود";
        if (!ModelState.IsValid) return View(vm);

        var mobile = Fa.NormMobile(vm.Mobile);
        const string wrong = "موبایل یا گذرواژه نادرست است.";

        (int Id, string Name, string Hash, int? CompanyId)? acc = vm.Role switch
        {
            Roles.Driver => await db.Drivers.Where(x => x.Mobile == mobile)
                .Select(x => new { x.DriverId, Name = x.FirstName + " " + x.LastName, x.PassHash, x.Status })
                .FirstOrDefaultAsync(ct) is { } d && d.Status != AccountStatus.Rejected
                ? (d.DriverId, d.Name, d.PassHash, null) : null,
            Roles.Shipper => await db.Shippers.Where(x => x.Mobile == mobile)
                .Select(x => new { x.ShipperId, x.FullName, x.PassHash })
                .FirstOrDefaultAsync(ct) is { } s ? (s.ShipperId, s.FullName, s.PassHash, null) : null,
            Roles.Company => await db.CompanyUsers.Where(x => x.Mobile == mobile && x.IsActive)
                .Select(x => new { x.CompanyUserId, x.Name, x.PassHash, x.CompanyId })
                .FirstOrDefaultAsync(ct) is { } c ? (c.CompanyUserId, c.Name, c.PassHash, c.CompanyId) : null,
            Roles.Admin => await db.Admins.Where(x => x.Mobile == mobile && x.IsActive)
                .Select(x => new { x.AdminId, x.Name, x.PassHash })
                .FirstOrDefaultAsync(ct) is { } a ? (a.AdminId, a.Name, a.PassHash, null) : null,
            _ => null
        };

        // هش را حتی برای حساب ناموجود محاسبه می‌کنیم تا زمان پاسخ، وجود شماره را لو ندهد
        var ok = PasswordHasher.Verify(vm.Password, acc?.Hash ?? "120000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        if (acc is null || !ok)
        {
            ModelState.AddModelError("", wrong);
            return View(vm);
        }

        await SignInAsync(vm.Role, acc.Value.Id, acc.Value.Name, acc.Value.CompanyId);

        if (vm.Role == Roles.Company)
            await db.CompanyUsers.Where(x => x.CompanyUserId == acc.Value.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastLoginAt, DateTime.UtcNow), ct);
        if (vm.Role == Roles.Admin)
            await db.Admins.Where(x => x.AdminId == acc.Value.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastLoginAt, DateTime.UtcNow), ct);

        if (!string.IsNullOrEmpty(vm.ReturnUrl) && Url.IsLocalUrl(vm.ReturnUrl) &&
            vm.ReturnUrl.StartsWith("/" + Roles.AreaOf(vm.Role), StringComparison.OrdinalIgnoreCase))
            return Redirect(vm.ReturnUrl);
        return Redirect("/" + Roles.AreaOf(vm.Role));
    }

    private async Task SignInAsync(string role, int id, string name, int? companyId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new(ClaimTypes.Role, role),
            new(CurrentUser.ClaimName, name.Trim())
        };
        if (companyId is int cid) claims.Add(new Claim(CurrentUser.ClaimCompany, cid.ToString()));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/account/login");
    }

    [HttpGet("denied")]
    public IActionResult Denied()
    {
        ViewData["Title"] = "دسترسی ندارید";
        return View();
    }

    // ------------------------------------------------------------------
    //  ثبت‌نام
    // ------------------------------------------------------------------

    [HttpGet("register")]
    public async Task<IActionResult> Register(string? type, CancellationToken ct)
    {
        ViewData["Title"] = "ثبت‌نام";
        await FillListsAsync(ct);
        return View(new RegisterVm { Type = type is Roles.Driver or Roles.Company ? type : Roles.Shipper });
    }

    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterVm vm, CancellationToken ct)
    {
        ViewData["Title"] = "ثبت‌نام";
        var mobile = Fa.NormMobile(vm.Mobile);

        if (mobile.Length == 0) ModelState.AddModelError(nameof(vm.Mobile), "شمارهٔ موبایل معتبر نیست.");
        if ((vm.Password ?? "").Length < 6) ModelState.AddModelError(nameof(vm.Password), "گذرواژه دست‌کم ۶ نویسه باشد.");
        if (vm.CityId is null) ModelState.AddModelError(nameof(vm.CityId), "شهر را انتخاب کنید.");
        if (!vm.AcceptTerms) ModelState.AddModelError(nameof(vm.AcceptTerms), "پذیرش قوانین و مقررات لازم است.");

        switch (vm.Type)
        {
            case Roles.Driver:
                if (string.IsNullOrWhiteSpace(vm.FirstName) || string.IsNullOrWhiteSpace(vm.LastName))
                    ModelState.AddModelError(nameof(vm.FirstName), "نام و نام خانوادگی را وارد کنید.");
                if (!IranId.IsNationalCode(vm.NationalCode)) ModelState.AddModelError(nameof(vm.NationalCode), "کد ملی معتبر نیست.");
                if (string.IsNullOrWhiteSpace(vm.LicenseNo)) ModelState.AddModelError(nameof(vm.LicenseNo), "شمارهٔ گواهینامه را وارد کنید.");
                if (string.IsNullOrWhiteSpace(vm.SmartCardNo)) ModelState.AddModelError(nameof(vm.SmartCardNo), "شمارهٔ کارت هوشمند را وارد کنید.");
                if (vm.VehicleTypeId is null || string.IsNullOrWhiteSpace(vm.PlateNo))
                    ModelState.AddModelError(nameof(vm.PlateNo), "نوع خودرو و پلاک را وارد کنید.");
                if (mobile.Length > 0 && await db.Drivers.AnyAsync(x => x.Mobile == mobile, ct))
                    ModelState.AddModelError(nameof(vm.Mobile), "با این شماره قبلاً حساب راننده ساخته شده است.");
                break;
            case Roles.Shipper:
                if (string.IsNullOrWhiteSpace(vm.FullName)) ModelState.AddModelError(nameof(vm.FullName), "نام را وارد کنید.");
                if (vm.ShipperKind == "business" && string.IsNullOrWhiteSpace(vm.BusinessName))
                    ModelState.AddModelError(nameof(vm.BusinessName), "نام کسب‌وکار را وارد کنید.");
                if (mobile.Length > 0 && await db.Shippers.AnyAsync(x => x.Mobile == mobile, ct))
                    ModelState.AddModelError(nameof(vm.Mobile), "با این شماره قبلاً حساب صاحب بار ساخته شده است.");
                break;
            case Roles.Company:
                if (string.IsNullOrWhiteSpace(vm.CompanyName)) ModelState.AddModelError(nameof(vm.CompanyName), "نام شرکت را وارد کنید.");
                if (!IranId.IsCompanyNationalId(vm.NationalId)) ModelState.AddModelError(nameof(vm.NationalId), "شناسهٔ ملی شرکت معتبر نیست.");
                if (string.IsNullOrWhiteSpace(vm.ManagerName)) ModelState.AddModelError(nameof(vm.ManagerName), "نام مدیرعامل را وارد کنید.");
                if (!string.IsNullOrWhiteSpace(vm.Sheba) && !IranId.IsSheba(vm.Sheba)) ModelState.AddModelError(nameof(vm.Sheba), "شمارهٔ شبا معتبر نیست.");
                if (mobile.Length > 0 && await db.CompanyUsers.AnyAsync(x => x.Mobile == mobile, ct))
                    ModelState.AddModelError(nameof(vm.Mobile), "این شماره قبلاً کاربر یک شرکت است.");
                var nid = Fa.Latin(vm.NationalId);
                if (nid.Length > 0 && await db.Companies.AnyAsync(x => x.NationalId == nid, ct))
                    ModelState.AddModelError(nameof(vm.NationalId), "شرکتی با این شناسهٔ ملی قبلاً ثبت شده است.");
                break;
            default:
                return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            await FillListsAsync(ct);
            return View(vm);
        }

        var hash = PasswordHasher.Hash(vm.Password!);
        switch (vm.Type)
        {
            case Roles.Driver:
            {
                var type = await db.VehicleTypes.FirstAsync(t => t.VehicleTypeId == vm.VehicleTypeId, ct);
                var d = new Driver
                {
                    Mobile = mobile, PassHash = hash, FirstName = vm.FirstName!.Trim(), LastName = vm.LastName!.Trim(),
                    NationalCode = Fa.Latin(vm.NationalCode), CityId = vm.CityId, LicenseNo = Fa.Latin(vm.LicenseNo), SmartCardNo = Fa.Latin(vm.SmartCardNo)
                };
                db.Drivers.Add(d);
                await db.SaveChangesAsync(ct);
                db.Vehicles.Add(new Vehicle { DriverId = d.DriverId, VehicleTypeId = type.VehicleTypeId, PlateNo = vm.PlateNo!.Trim(), CapacityTon = vm.CapacityTon ?? type.CapacityTon });
                notify.Add(OwnerKind.Driver, d.DriverId, "به بارگو خوش آمدید",
                    "برای فعال شدن حساب، گواهینامه، کارت هوشمند، کارت ملی و مدارک خودرو را بارگذاری کنید.", "/Driver/Vehicle/Documents", "system");
                await db.SaveChangesAsync(ct);
                await SignInAsync(Roles.Driver, d.DriverId, d.FullName, null);
                return Redirect("/Driver");
            }
            case Roles.Shipper:
            {
                var s = new Shipper
                {
                    Mobile = mobile, PassHash = hash, Kind = vm.ShipperKind == "business" ? "business" : "person",
                    FullName = vm.FullName!.Trim(), BusinessName = vm.BusinessName?.Trim(), CityId = vm.CityId
                };
                db.Shippers.Add(s);
                await db.SaveChangesAsync(ct);
                notify.Add(OwnerKind.Shipper, s.ShipperId, "به بارگو خوش آمدید", "اولین بار خود را ثبت کنید تا رانندگان پیشنهاد بدهند.", "/Shipper/Loads/Create", "system");
                await db.SaveChangesAsync(ct);
                await SignInAsync(Roles.Shipper, s.ShipperId, s.FullName, null);
                return Redirect("/Shipper");
            }
            default:
            {
                var c = new Company
                {
                    Name = vm.CompanyName!.Trim(), NationalId = Fa.Latin(vm.NationalId), RegistrationNo = Fa.Latin(vm.RegistrationNo),
                    LicenseNo = vm.LicenseNoCompany?.Trim(), ManagerName = vm.ManagerName!.Trim(), Mobile = mobile, CityId = vm.CityId,
                    Sheba = string.IsNullOrWhiteSpace(vm.Sheba) ? null : IranId.NormSheba(vm.Sheba)
                };
                var owner = new CompanyUser { Name = c.ManagerName, Mobile = mobile, PassHash = hash, Title = "owner", IsOwner = true, Permissions = CompanyPermission.All };
                c.Users.Add(owner);
                db.Companies.Add(c);
                await db.SaveChangesAsync(ct);
                notify.Add(OwnerKind.Company, c.CompanyId, "درخواست عضویت شرکت ثبت شد",
                    "آگهی تأسیس، مجوز فعالیت و کارت ملی مدیرعامل را بارگذاری کنید تا مدیر بارگو پنل شرکت را فعال کند.", "/Company/Account/Documents", "system");
                await db.SaveChangesAsync(ct);
                await SignInAsync(Roles.Company, owner.CompanyUserId, owner.Name, c.CompanyId);
                return Redirect("/Company");
            }
        }
    }

    private async Task FillListsAsync(CancellationToken ct)
    {
        ViewBag.Cities = await db.Cities.AsNoTracking().Include(c => c.Province)
            .OrderBy(c => c.ProvinceId).ThenBy(c => c.Name)
            .Select(c => new { c.CityId, c.Name, Province = c.Province!.Name })
            .ToListAsync(ct);
        ViewBag.VehicleTypes = await db.VehicleTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToListAsync(ct);
    }
}
