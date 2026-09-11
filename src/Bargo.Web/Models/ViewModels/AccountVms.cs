using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.ViewModels;

public class LoginVm
{
    /// <summary>driver | shipper | company | admin</summary>
    public string Role { get; set; } = "shipper";

    [Required(ErrorMessage = "شمارهٔ موبایل را وارد کنید.")]
    public string Mobile { get; set; } = "";

    [Required(ErrorMessage = "گذرواژه را وارد کنید.")]
    public string Password { get; set; } = "";

    public string? ReturnUrl { get; set; }
}

/// <summary>
/// ثبت‌نام هر سه نقش در یک فرم با فیلدهای متفاوت. اعتبارسنجیِ وابسته به نقش در
/// AccountController انجام می‌شود، چون DataAnnotations شرط «فقط برای راننده» ندارد.
/// </summary>
public class RegisterVm
{
    /// <summary>driver | shipper | company</summary>
    public string Type { get; set; } = "shipper";

    // مشترک
    public string Mobile { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>تکرار گذرواژه — مثل پت‌اوآیدی، جلوی غلط تایپی رمز را می‌گیرد.</summary>
    public string? ConfirmPassword { get; set; }
    /// <summary>خانم | آقا — فقط برای راننده و صاحب بار حقیقی.</summary>
    public string? Gender { get; set; }
    public int? CityId { get; set; }
    public bool AcceptTerms { get; set; }
    /// <summary>پاسخ عبارت امنیتی حسابی.</summary>
    public string? Captcha { get; set; }

    // راننده
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? NationalCode { get; set; }
    public string? LicenseNo { get; set; }
    public string? SmartCardNo { get; set; }
    public int? VehicleTypeId { get; set; }
    public string? PlateNo { get; set; }
    public decimal? CapacityTon { get; set; }

    // صاحب بار
    public string ShipperKind { get; set; } = "person";
    public string? FullName { get; set; }
    public string? BusinessName { get; set; }

    // شرکت
    public string? CompanyName { get; set; }
    public string? NationalId { get; set; }
    public string? RegistrationNo { get; set; }
    public string? LicenseNoCompany { get; set; }
    public string? ManagerName { get; set; }
    public string? Sheba { get; set; }
}
