using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

// ---------------------------------------------------------------------------
//  حساب‌های کاربری — چهار پنل، چهار جدولِ جدا
//
//  راننده، صاحب بار، شرکت حمل‌ونقل و مدیر سامانه هر کدام جدول خودشان را دارند
//  (همان الگوی رنگیو: Admin / Union / Unit). دلیل: فیلدها و چرخهٔ تأییدشان هیچ
//  شباهتی به هم ندارد؛ راننده گواهینامه و کارت هوشمند دارد، شرکت شناسهٔ ملی و
//  مجوز فعالیت، و صاحب بار ممکن است شخص حقیقی باشد. یک جدولِ «User» با ستون نقش،
//  نیمی از ستون‌ها را برای هر ردیف خالی می‌گذاشت.
//
//  مبالغ همه‌جا به «ریال» و از نوع long ذخیره می‌شوند و فقط در نمایش به تومان
//  تبدیل می‌شوند (Fa.Toman).
// ---------------------------------------------------------------------------

/// <summary>نقش‌ها — مقدار همان چیزی است که در کوکی ورود و [Authorize(Roles)] می‌نشیند.</summary>
public static class Roles
{
    public const string Driver = "driver";
    public const string Shipper = "shipper";
    public const string Company = "company";
    public const string Admin = "admin";

    public static string Title(string role) => role switch
    {
        Driver => "راننده",
        Shipper => "صاحب بار",
        Company => "شرکت حمل‌ونقل",
        Admin => "مدیر سامانه",
        _ => ""
    };

    /// <summary>Area هر نقش — نشانیِ داشبورد پس از ورود.</summary>
    public static string AreaOf(string role) => role switch
    {
        Driver => "Driver",
        Shipper => "Shipper",
        Company => "Company",
        Admin => "Admin",
        _ => ""
    };
}

/// <summary>
/// «صاحبِ» یک ردیفِ مشترک (کیف پول، اعلان، تیکت، مدرک). جدول‌های مشترک به‌جای
/// چهار کلید خارجیِ اختیاری، یک جفت (OwnerKind, OwnerId) دارند.
/// </summary>
public static class OwnerKind
{
    public const string Driver = "driver";
    public const string Shipper = "shipper";
    public const string Company = "company";
    public const string Vehicle = "vehicle";
    public const string Admin = "admin";
    /// <summary>خودِ بارگو — کمیسیون در این کیف پول می‌نشیند.</summary>
    public const string Platform = "platform";

    public static string Label(string k) => k switch
    {
        Driver => "راننده",
        Shipper => "صاحب بار",
        Company => "شرکت",
        Vehicle => "خودرو",
        Admin => "مدیر",
        Platform => "بارگو",
        _ => k
    };
}

/// <summary>
/// ردیفی که صاحبش با (OwnerKind, OwnerId) مشخص می‌شود. فیلتر مالکیت برای همهٔ
/// این جدول‌ها یکی است: <c>db.Tickets.Of(me.Owner)</c> (Services/Scopes.cs).
/// </summary>
public interface IOwned
{
    string OwnerKind { get; }
    int OwnerId { get; }
}

/// <summary>وضعیت حساب راننده/شرکت/صاحب بار.</summary>
public static class AccountStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Suspended = "suspended";

    public static string Label(string s) => s switch
    {
        Pending => "در انتظار تأیید",
        Approved => "تأییدشده",
        Rejected => "ردشده",
        Suspended => "تعلیق‌شده",
        _ => s
    };

    /// <summary>کلاس پیل: ok / wait / no / mut</summary>
    public static string Tone(string s) => s switch
    {
        Approved => "ok",
        Pending => "wait",
        Rejected or Suspended => "no",
        _ => "mut"
    };
}

/// <summary>اختیارات مدیران سامانه — «نقش‌ها و دسترسی‌ها».</summary>
[Flags]
public enum AdminPermission
{
    None = 0,
    Users = 1,          // کاربران، رانندگان، شرکت‌ها، خودروها
    Verification = 2,   // بررسی مدارک و احراز هویت
    Operations = 4,     // بارها، سفرها، کنترل زنده
    Finance = 8,        // تراکنش‌ها، تسویه، استرداد، تعرفه
    Support = 16,       // تیکت، شکایت، تخلف
    Reports = 32,
    Content = 64,       // محتوا، اعلان عمومی، مناطق
    Settings = 128,     // تنظیمات سامانه — هرگز به مدیر غیرارشد تفویض نشود
    All = Users | Verification | Operations | Finance | Support | Reports | Content | Settings
}

/// <summary>مدیر یا اپراتور سامانهٔ بارگو.</summary>
public class Admin
{
    public int AdminId { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(11)] public string Mobile { get; set; } = "";
    public string PassHash { get; set; } = "";
    /// <summary>مدیر ارشد همهٔ اختیارات را دارد و می‌تواند مدیر تعریف کند.</summary>
    public bool IsSuper { get; set; }
    public AdminPermission Permissions { get; set; } = AdminPermission.None;
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool Can(AdminPermission p) => IsSuper || Permissions.HasFlag(p);
}

/// <summary>راننده — مستقل یا عضو یک شرکت حمل‌ونقل.</summary>
public class Driver
{
    public int DriverId { get; set; }
    [MaxLength(11)] public string Mobile { get; set; } = "";
    public string PassHash { get; set; } = "";
    [MaxLength(60)] public string FirstName { get; set; } = "";
    [MaxLength(60)] public string LastName { get; set; } = "";
    [MaxLength(10)] public string NationalCode { get; set; } = "";
    public DateTime? BirthDate { get; set; }
    public int? CityId { get; set; }
    public City? City { get; set; }
    public string? Address { get; set; }

    [MaxLength(30)] public string? LicenseNo { get; set; }
    public DateTime? LicenseExpiresAt { get; set; }
    [MaxLength(30)] public string? SmartCardNo { get; set; }
    public DateTime? SmartCardExpiresAt { get; set; }

    /// <summary>
    /// شرکتی که راننده عضو آن است. null یعنی راننده مستقل است و خودش پیشنهاد می‌دهد؛
    /// رانندهٔ عضو شرکت هم می‌تواند مستقل بار بگیرد مگر شرکت محدودش کرده باشد.
    /// </summary>
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    [MaxLength(20)] public string Status { get; set; } = AccountStatus.Pending;
    public string? StatusReason { get; set; }
    public DateTime? ApprovedAt { get; set; }

    /// <summary>راننده خودش «آماده بار» بودن را روشن/خاموش می‌کند.</summary>
    public bool IsAvailable { get; set; } = true;
    public double? LastLat { get; set; }
    public double? LastLng { get; set; }
    public DateTime? LastSeenAt { get; set; }

    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }
    public int TripCount { get; set; }

    public long WalletBalance { get; set; }
    [MaxLength(26)] public string? Sheba { get; set; }

    /// <summary>کلید ارسال موقعیت از اپ — سرویس پس‌زمینه کوکی ندارد.</summary>
    [MaxLength(40)] public string TrackKey { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>صاحب بار — شخص حقیقی یا کسب‌وکار.</summary>
public class Shipper
{
    public int ShipperId { get; set; }
    [MaxLength(11)] public string Mobile { get; set; } = "";
    public string PassHash { get; set; } = "";
    /// <summary>person | business</summary>
    [MaxLength(10)] public string Kind { get; set; } = "person";
    [MaxLength(100)] public string FullName { get; set; } = "";
    [MaxLength(10)] public string? NationalCode { get; set; }
    [MaxLength(150)] public string? BusinessName { get; set; }
    [MaxLength(11)] public string? NationalId { get; set; }
    [MaxLength(14)] public string? EconomicCode { get; set; }
    public int? CityId { get; set; }
    public City? City { get; set; }
    public string? Address { get; set; }
    [MaxLength(120)] public string? Email { get; set; }

    /// <summary>صاحب بار بلافاصله فعال است؛ احراز هویت جداست و فقط سقف‌ها را باز می‌کند.</summary>
    [MaxLength(20)] public string Status { get; set; } = AccountStatus.Approved;
    public string? StatusReason { get; set; }
    /// <summary>pending | approved | rejected — و null یعنی هنوز مدرکی نفرستاده.</summary>
    [MaxLength(20)] public string? VerifyStatus { get; set; }
    public DateTime? VerifiedAt { get; set; }

    public long WalletBalance { get; set; }
    [MaxLength(26)] public string? Sheba { get; set; }
    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }

    // تنظیمات اعلان
    public bool NotifySms { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string DisplayName => Kind == "business" && !string.IsNullOrWhiteSpace(BusinessName) ? BusinessName! : FullName;
}

/// <summary>شرکت حمل‌ونقل.</summary>
public class Company
{
    public int CompanyId { get; set; }
    [MaxLength(150)] public string Name { get; set; } = "";
    /// <summary>شناسهٔ ملی ۱۱ رقمی.</summary>
    [MaxLength(11)] public string NationalId { get; set; } = "";
    [MaxLength(30)] public string? RegistrationNo { get; set; }
    [MaxLength(40)] public string? LicenseNo { get; set; }
    public DateTime? LicenseExpiresAt { get; set; }
    [MaxLength(100)] public string ManagerName { get; set; } = "";
    [MaxLength(10)] public string? ManagerNationalCode { get; set; }
    [MaxLength(11)] public string Mobile { get; set; } = "";
    [MaxLength(20)] public string? Phone { get; set; }
    [MaxLength(120)] public string? Email { get; set; }
    public int? CityId { get; set; }
    public City? City { get; set; }
    public string? Address { get; set; }

    [MaxLength(26)] public string? Sheba { get; set; }
    [MaxLength(60)] public string? BankName { get; set; }

    /// <summary>صاحب بار می‌تواند درخواست حمل را مستقیم برای این شرکت بفرستد.</summary>
    public bool AcceptsDirectRequests { get; set; } = true;

    /// <summary>درصد سهم پیش‌فرض راننده از کرایهٔ خالص (پس از کمیسیون بارگو).</summary>
    public decimal DriverSharePercent { get; set; } = 70;

    [MaxLength(20)] public string Status { get; set; } = AccountStatus.Pending;
    public string? StatusReason { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public long WalletBalance { get; set; }
    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<CompanyUser> Users { get; set; } = [];
}

/// <summary>اختیارات کاربران یک شرکت — «کاربران شرکت ← تعیین سطح دسترسی».</summary>
[Flags]
public enum CompanyPermission
{
    None = 0,
    Loads = 1,        // ثبت بار، پیشنهاد، درخواست‌های دریافتی
    Dispatch = 2,     // تخصیص و تعویض راننده/خودرو، کنترل سفر
    Drivers = 4,
    Fleet = 8,
    Finance = 16,
    Reports = 32,
    Customers = 64,
    Users = 128,      // فقط مدیر شرکت
    All = Loads | Dispatch | Drivers | Fleet | Finance | Reports | Customers | Users
}

/// <summary>
/// کاربرِ پنل شرکت. ورود به پنل شرکت همیشه با یکی از این ردیف‌هاست، نه با خودِ
/// ردیف Company؛ پس هر اقدام به یک شخص مشخص نسبت داده می‌شود (مثلاً «چه کسی
/// راننده را تعویض کرد»).
/// </summary>
public class CompanyUser
{
    public int CompanyUserId { get; set; }
    public int CompanyId { get; set; }
    public Company? Company { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(11)] public string Mobile { get; set; } = "";
    public string PassHash { get; set; } = "";
    /// <summary>owner | dispatcher | accountant | viewer — فقط برچسب؛ دسترسی واقعی از Permissions است.</summary>
    [MaxLength(20)] public string Title { get; set; } = "owner";
    public CompanyPermission Permissions { get; set; } = CompanyPermission.All;
    public bool IsOwner { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>آدرس‌های منتخب صاحب بار.</summary>
public class SavedAddress
{
    public int SavedAddressId { get; set; }
    public int ShipperId { get; set; }
    [MaxLength(60)] public string Title { get; set; } = "";
    public int CityId { get; set; }
    public City? City { get; set; }
    public string Address { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    [MaxLength(100)] public string? ContactName { get; set; }
    [MaxLength(11)] public string? ContactMobile { get; set; }
}

/// <summary>کد یکبارمصرف (ورود، تأیید موبایل هنگام ثبت‌نام).</summary>
public class Otp
{
    public int OtpId { get; set; }
    [MaxLength(11)] public string Mobile { get; set; } = "";
    [MaxLength(20)] public string Purpose { get; set; } = "login";
    public string CodeHash { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
