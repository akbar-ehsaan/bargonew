using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

// ---------------------------------------------------------------------------
//  «بار» و «سفر» دو چیزند — این مرز را جابه‌جا نکنید
//
//  بار (Load) چیزی است که صاحب بار ثبت می‌کند: مبدا، مقصد، وزن، زمان بارگیری،
//  قیمت پیشنهادی. رانندگان و شرکت‌ها روی «بار» پیشنهاد (Offer) می‌دهند.
//
//  وقتی صاحب بار یک پیشنهاد را قبول کرد، از روی آن یک سفر (Trip) ساخته می‌شود و
//  از آن لحظه همهٔ عملیات حمل — تخصیص راننده و خودرو، مراحل، GPS، بارنامه، تحویل،
//  تسویه — روی سفر است. بار فقط وضعیتِ خلاصه دارد (booked / completed).
//
//  چرا: لغو راننده یعنی سفر لغو می‌شود ولی بار دوباره منتشر می‌شود؛ تعویض خودرو
//  روی سفر ثبت می‌شود؛ حمل چندمرحله‌ای یعنی چند سفر برای یک بار؛ و گزارش مالی
//  همیشه بر پایهٔ سفر است نه آگهی.
// ---------------------------------------------------------------------------

public static class LoadStatus
{
    public const string Draft = "draft";
    /// <summary>منتشرشده، هنوز پیشنهادی نگرفته — «در انتظار راننده»</summary>
    public const string Open = "open";
    /// <summary>دست‌کم یک پیشنهاد در انتظار دارد</summary>
    public const string Offering = "offering";
    /// <summary>پیشنهاد پذیرفته شد و سفر ساخته شد — ادامه روی Trip</summary>
    public const string Booked = "booked";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";

    /// <summary>بار در بازار دیده می‌شود و پیشنهاد می‌گیرد.</summary>
    public static readonly string[] Market = [Open, Offering];

    public static string Label(string s) => s switch
    {
        Draft => "پیش‌نویس",
        Open => "در انتظار راننده",
        Offering => "در حال دریافت پیشنهاد",
        Booked => "قطعی‌شده",
        Completed => "تحویل‌شده",
        Cancelled => "لغوشده",
        Expired => "منقضی",
        _ => s
    };

    public static string Tone(string s) => s switch
    {
        Open or Offering => "wait",
        Booked => "inf",
        Completed => "ok",
        Cancelled or Expired => "no",
        _ => "mut"
    };
}

public static class PriceMode
{
    public const string Fixed = "fixed";
    public const string Negotiable = "negotiable";
    public static string Label(string s) => s == Fixed ? "کرایه ثابت" : "توافقی";
}

/// <summary>
/// روش پرداخت کرایه — مثل «پرداخت بارنامه» در باربری‌های واقعی:
///   wallet: آنلاین از کیف پول؛ تا تحویل نزد بارگو امانت می‌ماند (روش پیش‌فرض).
///   cash:   نقدی به حمل‌کننده در مقصد؛ پولی از بارگو نمی‌گذرد و فقط کمیسیون
///           هنگام تسویه از کیف پول حمل‌کننده کسر می‌شود (الگوی کمیسیون باربری).
/// </summary>
public static class PayMethods
{
    public const string Wallet = "wallet";
    public const string Cash = "cash";
    public static string Label(string m) => m == Cash ? "نقدی به حمل‌کننده در مقصد" : "آنلاین از کیف پول (امانی)";
}

/// <summary>بار ثبت‌شده.</summary>
public class Load
{
    public int LoadId { get; set; }
    /// <summary>کد خوانا، مثل L-050621-0012</summary>
    [MaxLength(20)] public string Code { get; set; } = "";

    /// <summary>صاحب بار. اگر شرکت برای مشتری خودش بار ثبت کند، null و CompanyId پر است.</summary>
    public int? ShipperId { get; set; }
    public Shipper? Shipper { get; set; }
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    /// <summary>درخواست مستقیم برای یک شرکت — در بازار عمومی دیده نمی‌شود.</summary>
    public int? TargetCompanyId { get; set; }
    public Company? TargetCompany { get; set; }

    public int OriginCityId { get; set; }
    public City? OriginCity { get; set; }
    public string OriginAddress { get; set; } = "";
    public double? OriginLat { get; set; }
    public double? OriginLng { get; set; }

    public int DestCityId { get; set; }
    public City? DestCity { get; set; }
    public string DestAddress { get; set; } = "";
    public double? DestLat { get; set; }
    public double? DestLng { get; set; }

    [MaxLength(60)] public string CargoType { get; set; } = "";
    [MaxLength(150)] public string Title { get; set; } = "";
    [MaxLength(40)] public string? Packaging { get; set; }
    public decimal WeightTon { get; set; }
    public decimal? VolumeM3 { get; set; }
    public decimal? LengthM { get; set; }
    public decimal? WidthM { get; set; }
    public decimal? HeightM { get; set; }

    public int? VehicleTypeId { get; set; }
    public VehicleType? VehicleType { get; set; }

    public DateTime LoadingFrom { get; set; }
    public DateTime? LoadingTo { get; set; }

    /// <summary>wallet | cash — روش پرداخت کرایه؛ هنگام ساخت سفر روی آن کپی می‌شود.</summary>
    [MaxLength(10)] public string PayMethod { get; set; } = PayMethods.Wallet;

    [MaxLength(12)] public string PriceMode { get; set; } = Entities.PriceMode.Negotiable;
    /// <summary>کرایهٔ اعلامی به ریال (در حالت توافقی، پیشنهاد اولیهٔ صاحب بار).</summary>
    public long? Price { get; set; }
    /// <summary>ارزش تقریبی بار به ریال — مبنای بیمه.</summary>
    public long? DeclaredValue { get; set; }
    public bool InsuranceRequested { get; set; }
    /// <summary>نوع بیمهٔ بار (باربری داخلی، تمام‌خطر، …) — در بارنامه ثبت می‌شود.</summary>
    [MaxLength(40)] public string? InsuranceType { get; set; }
    /// <summary>مبلغ/حق بیمه به ریال — در بارنامه ثبت می‌شود.</summary>
    public long? InsuranceAmount { get; set; }

    public string? Description { get; set; }
    [MaxLength(100)] public string? ReceiverName { get; set; }
    [MaxLength(11)] public string? ReceiverMobile { get; set; }

    /// <summary>
    /// هش کد تحویل (SHA256 با نمکِ Code). کد هنگام ثبت بار ساخته و به گیرنده پیامک
    /// می‌شود؛ گیرنده هنگام رسیدن بار آن را به راننده می‌دهد تا در سامانه ثبت کند.
    /// </summary>
    public string? DeliveryCodeHash { get; set; }

    public double? DistanceKm { get; set; }

    [MaxLength(20)] public string Status { get; set; } = LoadStatus.Open;
    public string? CancelReason { get; set; }
    /// <summary>بار گزارش‌شده (تخلف/آگهی نامعتبر) — در صف مدیر.</summary>
    public bool IsReported { get; set; }
    public string? ReportNote { get; set; }

    public DateTime? PublishedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Offer> Offers { get; set; } = [];
    public List<LoadPhoto> Photos { get; set; } = [];
    public List<Trip> Trips { get; set; } = [];

    public string OwnerName => Shipper?.DisplayName ?? Company?.Name ?? "";
}

public class LoadPhoto
{
    public int LoadPhotoId { get; set; }
    public int LoadId { get; set; }
    public Load? Load { get; set; }
    public string FilePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>بارِ نشان‌شده توسط راننده — «بارهای ذخیره‌شده».</summary>
public class SavedLoad
{
    public int SavedLoadId { get; set; }
    public int DriverId { get; set; }
    public int LoadId { get; set; }
    public Load? Load { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class CarrierKind
{
    public const string Driver = "driver";
    public const string Company = "company";
    public static string Label(string k) => k == Company ? "شرکت حمل‌ونقل" : "راننده";
}

public static class OfferStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
    public const string Expired = "expired";

    public static string Label(string s) => s switch
    {
        Pending => "در انتظار پاسخ",
        Accepted => "پذیرفته‌شده",
        Rejected => "ردشده",
        Withdrawn => "پس‌گرفته",
        Expired => "منقضی",
        _ => s
    };

    public static string Tone(string s) => s switch { Pending => "wait", Accepted => "ok", Rejected => "no", _ => "mut" };
}

/// <summary>
/// پیشنهاد حمل. در بار «کرایه ثابت» همان «درخواست حمل» است و Amount برابر Price
/// بار؛ در بار توافقی، مبلغی است که راننده/شرکت پیشنهاد می‌دهد.
/// </summary>
public class Offer
{
    public int OfferId { get; set; }
    public int LoadId { get; set; }
    public Load? Load { get; set; }

    [MaxLength(10)] public string CarrierKind { get; set; } = Entities.CarrierKind.Driver;
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    /// <summary>خودرویی که راننده با آن می‌آید (برای شرکت اختیاری — بعداً تخصیص می‌دهد).</summary>
    public int? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }

    public long Amount { get; set; }
    /// <summary>چند ساعت تا رسیدن به مبدا.</summary>
    public int? EtaHours { get; set; }
    public string? Note { get; set; }

    [MaxLength(20)] public string Status { get; set; } = OfferStatus.Pending;
    public DateTime? RespondedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string CarrierName => Company?.Name ?? Driver?.FullName ?? "";
}
