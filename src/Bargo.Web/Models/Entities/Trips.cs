using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

/// <summary>
/// مراحل سفر. ترتیب و مجاز بودنِ هر گذار در <c>Services/TripFlow.cs</c> تعریف شده
/// و هیچ کنترلری نباید Status را مستقیم بنویسد — وگرنه ردِ رویدادها (TripEvent)
/// که مبنای رسیدگی به اختلاف است ناقص می‌شود.
/// </summary>
public static class TripStatus
{
    /// <summary>شرکت پیشنهاد را برده ولی هنوز راننده/خودرو تعیین نکرده.</summary>
    public const string AwaitingAssignment = "awaiting_assignment";
    /// <summary>راننده تعیین شده، منتظر قبول مأموریت.</summary>
    public const string Assigned = "assigned";
    /// <summary>راننده مأموریت را پذیرفت — آماده حرکت.</summary>
    public const string Accepted = "accepted";
    public const string ToOrigin = "to_origin";
    public const string AtOrigin = "at_origin";
    public const string Loaded = "loaded";
    public const string InTransit = "in_transit";
    public const string Arrived = "arrived";
    public const string Unloaded = "unloaded";
    /// <summary>تحویل با کد گیرنده تأیید شد.</summary>
    public const string Delivered = "delivered";
    public const string Settled = "settled";
    public const string Cancelled = "cancelled";

    /// <summary>ترتیب نمایش مراحل در نوار پیشرفت.</summary>
    public static readonly string[] Steps =
        [Accepted, ToOrigin, AtOrigin, Loaded, InTransit, Arrived, Unloaded, Delivered, Settled];

    /// <summary>سفر «زنده»: راننده در راه است و موقعیتش معنا دارد.</summary>
    public static readonly string[] Live = [ToOrigin, AtOrigin, Loaded, InTransit, Arrived, Unloaded];

    /// <summary>هنوز شروع نشده.</summary>
    public static readonly string[] Upcoming = [AwaitingAssignment, Assigned, Accepted];

    public static readonly string[] Done = [Delivered, Settled];

    public static string Label(string s) => s switch
    {
        AwaitingAssignment => "در انتظار تخصیص",
        Assigned => "در انتظار قبول راننده",
        Accepted => "آماده حرکت",
        ToOrigin => "در مسیر مبدا",
        AtOrigin => "حضور در مبدا",
        Loaded => "بارگیری‌شده",
        InTransit => "در حال حمل",
        Arrived => "رسیده به مقصد",
        Unloaded => "تخلیه‌شده",
        Delivered => "تحویل‌شده",
        Settled => "تسویه‌شده",
        Cancelled => "لغوشده",
        _ => s
    };

    public static string Tone(string s) => s switch
    {
        AwaitingAssignment or Assigned => "wait",
        Accepted or ToOrigin or AtOrigin or Loaded or InTransit or Arrived or Unloaded => "inf",
        Delivered or Settled => "ok",
        Cancelled => "no",
        _ => "mut"
    };
}

/// <summary>سفر — عملیات حملِ یک بار، ساخته‌شده از روی پیشنهاد پذیرفته‌شده.</summary>
public class Trip
{
    public int TripId { get; set; }
    [MaxLength(20)] public string Code { get; set; } = "";

    public int LoadId { get; set; }
    public Load? Load { get; set; }
    public int? OfferId { get; set; }
    public Offer? Offer { get; set; }

    [MaxLength(10)] public string CarrierKind { get; set; } = Entities.CarrierKind.Driver;
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }

    // ---- مالی (ریال) — هنگام ساخت سفر قفل می‌شود تا تغییر تعرفه، سفرهای جاری را عوض نکند ----
    public long Fare { get; set; }
    public decimal CommissionPercent { get; set; }
    public long Commission { get; set; }
    /// <summary>سهم حمل‌کننده (راننده مستقل یا شرکت) = کرایه − کمیسیون.</summary>
    public long CarrierShare { get; set; }
    /// <summary>فقط برای سفر شرکتی: سهم رانندهٔ شرکت.</summary>
    public long? DriverShare { get; set; }
    /// <summary>هزینهٔ بارگیری — در تسویه به حمل‌کننده می‌رسد.</summary>
    public long LoadingFee { get; set; }
    /// <summary>هزینهٔ تخلیه — در تسویه به حمل‌کننده می‌رسد.</summary>
    public long UnloadingFee { get; set; }
    /// <summary>هزینهٔ صدور بارنامه — در تسویه به بارگو می‌رسد.</summary>
    public long WaybillFee { get; set; }
    /// <summary>درصد ارزش افزوده در لحظهٔ ساخت سفر (۰ = غیرفعال بود).</summary>
    public decimal VatPercent { get; set; }
    /// <summary>مبلغ ارزش افزوده روی کرایه و هزینه‌ها — در تسویه به بارگو می‌رسد.</summary>
    public long Vat { get; set; }
    /// <summary>کل مبلغی که صاحب بار می‌پردازد = کرایه + هزینه‌ها + ارزش افزوده.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public long TotalPayable => Fare + LoadingFee + UnloadingFee + WaybillFee + Vat;
    /// <summary>کرایه از صاحب بار دریافت شده (کیف پول/درگاه) و نزد بارگو امانت است.</summary>
    public bool IsPaid { get; set; }
    /// <summary>wallet | cash — از بار کپی می‌شود؛ در نقدی فقط کمیسیون از حمل‌کننده کسر می‌شود.</summary>
    [MaxLength(10)] public string PayMethod { get; set; } = PayMethods.Wallet;
    /// <summary>
    /// هویت طرفین آشکار است؟ در پرداخت آنلاین با پرداخت کرایه، و در نقدی همان لحظهٔ
    /// ساخت سفر (پرداختی در کار نیست که منتظرش بمانیم).
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool Revealed => IsPaid || PayMethod == PayMethods.Cash;
    /// <summary>شرکت سهم راننده را از کیف پول خودش به کیف پول راننده منتقل کرد.</summary>
    public DateTime? DriverSharePaidAt { get; set; }

    [MaxLength(24)] public string Status { get; set; } = TripStatus.Accepted;

    public DateTime? ScheduledDepartureAt { get; set; }
    public double? PlannedKm { get; set; }
    public double TravelledKm { get; set; }
    public DateTime? EtaAt { get; set; }
    public double? LastLat { get; set; }
    public double? LastLng { get; set; }
    public DateTime? LastPointAt { get; set; }

    // ---- تحویل با کد ----
    public string? DeliveryOtpHash { get; set; }
    public DateTime? DeliveryOtpSentAt { get; set; }
    public int DeliveryOtpAttempts { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? LoadedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? SettledAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>سفر مشکل‌دار — در صف مدیر.</summary>
    public bool IsProblem { get; set; }
    public string? ProblemNote { get; set; }

    /// <summary>توکن صفحهٔ رهگیری عمومی برای گیرنده (/t/{token}).</summary>
    [MaxLength(40)] public string TrackToken { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<TripEvent> Events { get; set; } = [];
    public List<TripAssignment> Assignments { get; set; } = [];
    public List<TripDocument> Documents { get; set; } = [];
    public Waybill? Waybill { get; set; }
}

/// <summary>
/// هر تغییر وضعیت سفر، با زمان، انجام‌دهنده و موقعیت. مدیر در «پروندهٔ سفر» برای
/// رسیدگی به اختلاف همین فهرست را می‌بیند؛ پس فقط اضافه می‌شود و هرگز ویرایش نمی‌شود.
/// </summary>
public class TripEvent
{
    public long TripEventId { get; set; }
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    [MaxLength(24)] public string? FromStatus { get; set; }
    [MaxLength(24)] public string ToStatus { get; set; } = "";
    [MaxLength(12)] public string ActorKind { get; set; } = "";
    public int ActorId { get; set; }
    [MaxLength(100)] public string? ActorName { get; set; }
    public string? Note { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>سابقهٔ تخصیص و تعویض راننده/خودرو — «تغییر راننده» باید ردپا داشته باشد.</summary>
public class TripAssignment
{
    public int TripAssignmentId { get; set; }
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    public int DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }
    public int? PreviousDriverId { get; set; }
    public int? PreviousVehicleId { get; set; }
    public int? ByCompanyUserId { get; set; }
    [MaxLength(100)] public string? ByName { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>نقطهٔ GPS. جدول پرحجم است؛ فقط اضافه می‌شود.</summary>
public class TrackingPoint
{
    public long TrackingPointId { get; set; }
    public int? TripId { get; set; }
    public int DriverId { get; set; }
    public int? VehicleId { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? SpeedKmh { get; set; }
    public double? AccuracyM { get; set; }
    public double StepKm { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public class Waybill
{
    public int WaybillId { get; set; }
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    [MaxLength(40)] public string Number { get; set; } = "";
    /// <summary>registered (ثبت شماره توسط راننده/شرکت) | verified (تأیید مدیر) | void</summary>
    [MaxLength(12)] public string Status { get; set; } = "registered";
    [MaxLength(12)] public string IssuedByKind { get; set; } = "";
    public int IssuedById { get; set; }
    public string? FilePath { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
}

public static class TripDocumentKind
{
    public const string LoadingReceipt = "loading_receipt";
    public const string DeliveryReceipt = "delivery_receipt";
    public const string CargoInsurance = "cargo_insurance";
    public const string Photo = "photo";
    public const string Other = "other";

    public static string Label(string k) => k switch
    {
        LoadingReceipt => "رسید بارگیری",
        DeliveryReceipt => "رسید تحویل",
        CargoInsurance => "بیمه‌نامه بار",
        Photo => "تصویر بار",
        _ => "سایر"
    };
}

public class TripDocument
{
    public int TripDocumentId { get; set; }
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    [MaxLength(24)] public string Kind { get; set; } = TripDocumentKind.Other;
    [MaxLength(150)] public string Title { get; set; } = "";
    public string? FilePath { get; set; }
    [MaxLength(12)] public string UploadedByKind { get; set; } = "";
    public int UploadedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>امتیاز و نظر پس از پایان سفر.</summary>
public class Rating
{
    public int RatingId { get; set; }
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    [MaxLength(12)] public string FromKind { get; set; } = "";
    public int FromId { get; set; }
    [MaxLength(12)] public string ToKind { get; set; } = "";
    public int ToId { get; set; }
    public int Score { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class FavoriteDriver
{
    public int FavoriteDriverId { get; set; }
    public int ShipperId { get; set; }
    public int DriverId { get; set; }
    public Driver? Driver { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
