using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

/// <summary>استان — ProvinceId یک‌مبنا و هم‌ترتیب با IranGeo.Provinces.</summary>
public class Province
{
    public int ProvinceId { get; set; }
    [MaxLength(40)] public string Name { get; set; } = "";
}

public class City
{
    public int CityId { get; set; }
    public int ProvinceId { get; set; }
    public Province? Province { get; set; }
    [MaxLength(40)] public string Name { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>پایانهٔ بار.</summary>
public class Terminal
{
    public int TerminalId { get; set; }
    public int CityId { get; set; }
    public City? City { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    public string? Address { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>«مبادی و مقاصد» — مسیرهای تعریف‌شده با فاصلهٔ مبنا (برای برآورد کرایه و ETA).</summary>
public class RouteLane
{
    public int RouteLaneId { get; set; }
    public int OriginCityId { get; set; }
    public City? OriginCity { get; set; }
    public int DestCityId { get; set; }
    public City? DestCity { get; set; }
    public double DistanceKm { get; set; }
    /// <summary>کرایهٔ مرجع هر تن به ریال — فقط راهنما، الزام‌آور نیست.</summary>
    public long? BaseRatePerTon { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>محدودهٔ جغرافیایی (دایره) — منطقهٔ ممنوعه، بندر، پایانه.</summary>
public class Geofence
{
    public int GeofenceId { get; set; }
    [MaxLength(100)] public string Title { get; set; } = "";
    /// <summary>restricted | terminal | port | custom</summary>
    [MaxLength(12)] public string Kind { get; set; } = "custom";
    public double CenterLat { get; set; }
    public double CenterLng { get; set; }
    public double RadiusKm { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>تنظیمات کلید/مقدار سامانه — دسترسی فقط از SettingsService.</summary>
public class Setting
{
    public int SettingId { get; set; }
    [MaxLength(100)] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class ContentKind
{
    public const string Banner = "banner";
    public const string News = "news";
    public const string Faq = "faq";
    public const string Rule = "rule";
    public const string Page = "page";

    public static string Label(string k) => k switch
    {
        Banner => "بنر",
        News => "خبر",
        Faq => "سوال متداول",
        Rule => "قوانین",
        Page => "صفحهٔ ثابت",
        _ => k
    };
}

/// <summary>مدیریت محتوا: بنر، خبر، سوالات متداول، قوانین، صفحات ثابت.</summary>
public class ContentItem
{
    public int ContentItemId { get; set; }
    [MaxLength(10)] public string Kind { get; set; } = ContentKind.Page;
    [MaxLength(100)] public string? Slug { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? ImagePath { get; set; }
    public string? Link { get; set; }
    /// <summary>all | driver | shipper | company — بنر و خبر برای کدام پنل</summary>
    [MaxLength(10)] public string Audience { get; set; } = "all";
    public int SortOrder { get; set; }
    public bool IsPublished { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>مشتریان شرکت حمل‌ونقل — صاحب بار عضو بارگو یا مشتری بیرونی.</summary>
public class CompanyCustomer
{
    public int CompanyCustomerId { get; set; }
    public int CompanyId { get; set; }
    public int? ShipperId { get; set; }
    public Shipper? Shipper { get; set; }
    [MaxLength(150)] public string Name { get; set; } = "";
    [MaxLength(11)] public string? Mobile { get; set; }
    [MaxLength(11)] public string? NationalId { get; set; }
    /// <summary>person | corporate</summary>
    [MaxLength(10)] public string Kind { get; set; } = "corporate";
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Contract
{
    public int ContractId { get; set; }
    public int CompanyId { get; set; }
    public Company? Company { get; set; }
    public int? CompanyCustomerId { get; set; }
    public CompanyCustomer? Customer { get; set; }
    [MaxLength(150)] public string Title { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public long? Amount { get; set; }
    public string? FilePath { get; set; }
    /// <summary>draft | active | expired | terminated</summary>
    [MaxLength(12)] public string Status { get; set; } = "active";
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>ردّ حسابرسی عملیات حساس (تأیید مدرک، تعلیق، تسویه، تعویض راننده).</summary>
public class AuditLog
{
    public long AuditLogId { get; set; }
    [MaxLength(40)] public string Entity { get; set; } = "";
    public int EntityId { get; set; }
    [MaxLength(40)] public string Action { get; set; } = "";
    [MaxLength(12)] public string ActorRole { get; set; } = "";
    public int ActorId { get; set; }
    [MaxLength(100)] public string? ActorName { get; set; }
    public string? Summary { get; set; }
    public string? DetailJson { get; set; }
    [MaxLength(45)] public string? Ip { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
