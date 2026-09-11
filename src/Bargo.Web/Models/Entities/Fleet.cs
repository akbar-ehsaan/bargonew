using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

/// <summary>نوع خودرو (داده پایه) — تریلی چادری، کامیون تک یخچالی و …</summary>
public class VehicleType
{
    public int VehicleTypeId { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    /// <summary>بارگیر: چادری، کفی، یخچالی، بغل‌باز، کمپرسی، تانکر، کانتینری</summary>
    [MaxLength(30)] public string? BodyKind { get; set; }
    public decimal CapacityTon { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public static class VehicleStatus
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Maintenance = "maintenance";
    public const string Suspended = "suspended";

    public static string Label(string s) => s switch
    {
        Active => "فعال",
        Inactive => "غیرفعال",
        Maintenance => "در تعمیر",
        Suspended => "تعلیق",
        _ => s
    };

    public static string Tone(string s) => s switch { Active => "ok", Maintenance => "wait", Suspended => "no", _ => "mut" };
}

/// <summary>
/// خودرو. مالکش یا یک رانندهٔ مستقل است (DriverId) یا یک شرکت (CompanyId).
/// خودروی شرکت در لحظهٔ تخصیص به یک راننده سپرده می‌شود و این سپردن روی
/// <see cref="Trip.VehicleId"/> ثبت می‌شود، نه اینجا.
/// </summary>
public class Vehicle
{
    public int VehicleId { get; set; }
    /// <summary>پلاک به قالب «۱۲ ع ۳۴۵ ایران ۶۷».</summary>
    [MaxLength(30)] public string PlateNo { get; set; } = "";
    public int VehicleTypeId { get; set; }
    public VehicleType? VehicleType { get; set; }
    [MaxLength(40)] public string? Brand { get; set; }
    [MaxLength(40)] public string? Model { get; set; }
    public int? Year { get; set; }
    public decimal CapacityTon { get; set; }

    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    [MaxLength(30)] public string? VehicleCardNo { get; set; }
    [MaxLength(30)] public string? InsurancePolicyNo { get; set; }
    public DateTime? InsuranceExpiresAt { get; set; }
    public DateTime? InspectionExpiresAt { get; set; }

    [MaxLength(20)] public string Status { get; set; } = VehicleStatus.Active;
    /// <summary>تأیید مدارک خودرو توسط مدیر.</summary>
    [MaxLength(20)] public string VerifyStatus { get; set; } = AccountStatus.Pending;

    public double? LastLat { get; set; }
    public double? LastLng { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public double TrackedKm { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class DocumentKind
{
    public const string NationalCard = "national_card";
    public const string License = "license";
    public const string SmartCard = "smart_card";
    public const string VehicleCard = "vehicle_card";
    public const string Insurance = "insurance";
    public const string Inspection = "inspection";
    public const string CompanyLicense = "company_license";
    public const string Registration = "registration";     // آگهی تأسیس / روزنامه رسمی
    public const string Other = "other";

    public static string Label(string k) => k switch
    {
        NationalCard => "کارت ملی",
        License => "گواهینامه",
        SmartCard => "کارت هوشمند",
        VehicleCard => "کارت خودرو",
        Insurance => "بیمه‌نامه",
        Inspection => "معاینه فنی",
        CompanyLicense => "مجوز فعالیت",
        Registration => "آگهی تأسیس",
        _ => "سایر"
    };

    /// <summary>مدارکی که تاریخ انقضا دارند و منقضی‌شدنشان پذیرش بار را می‌بندد.</summary>
    public static readonly string[] Expiring = [License, SmartCard, Insurance, Inspection, CompanyLicense];

    public static readonly string[] ForDriver = [NationalCard, License, SmartCard];
    public static readonly string[] ForVehicle = [VehicleCard, Insurance, Inspection];
    public static readonly string[] ForCompany = [Registration, CompanyLicense, NationalCard];
    public static readonly string[] ForShipper = [NationalCard, Registration];
}

/// <summary>
/// مدرک بارگذاری‌شده. یک جدول برای همهٔ صاحبان (راننده، خودرو، شرکت، صاحب بار)
/// تا صف «مدارک در انتظار بررسی» مدیر یک پرس‌وجو باشد، نه چهار.
/// </summary>
public class Document : IOwned
{
    public int DocumentId { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    [MaxLength(30)] public string Kind { get; set; } = DocumentKind.Other;
    [MaxLength(40)] public string? Number { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? FilePath { get; set; }
    [MaxLength(20)] public string Status { get; set; } = AccountStatus.Pending;
    public string? ReviewNote { get; set; }
    public int? ReviewedByAdminId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value.Date < DateTime.UtcNow.Date;
}
