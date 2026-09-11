using Bargo.Web.Models.Entities;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  پنل شرکت — «کاربران شرکت» و «حساب شرکت»
//
//  فرم‌ها کلاس‌های ساده با set هستند تا پس از خطای اعتبارسنجی، همان مقادیرِ
//  واردشده به فرم برگردند (نه اینکه با ریدایرکت پاک شوند).
// ---------------------------------------------------------------------------

/// <summary>فرم «تعریف کاربر» شرکت.</summary>
public sealed class CompanyUserForm
{
    public string? Name { get; set; }
    public string? Mobile { get; set; }
    public string? Password { get; set; }
    public string Title { get; set; } = "dispatcher";
    /// <summary>نامِ پرچم‌های انتخاب‌شده (Loads، Fleet، …) — از چک‌باکس‌ها.</summary>
    public string[] Perms { get; set; } = [];
}

/// <summary>فرم «مشخصات شرکت».</summary>
public sealed class CompanyAccountForm
{
    public string? Name { get; set; }
    public string? NationalId { get; set; }
    public string? RegistrationNo { get; set; }
    public string? ManagerName { get; set; }
    public string? ManagerNationalCode { get; set; }
    public string? Mobile { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public int? CityId { get; set; }
    public string? Address { get; set; }
}

/// <summary>یک مدرک شرکت در فهرست «مدارک» — با پیوند فایل و یادداشت بررسی.</summary>
public sealed class CompanyDocRow
{
    public int DocumentId { get; init; }
    public string Kind { get; init; } = "";
    public string? Number { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? FilePath { get; init; }
    public string Status { get; init; } = "";
    public string? ReviewNote { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public DateTime UploadedAt { get; init; }

    public string Label => DocumentKind.Label(Kind);
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value.Date < DateTime.UtcNow.Date;
}
