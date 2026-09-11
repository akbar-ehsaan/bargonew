using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

// ---------------------------------------------------------------- بانک مخاطبان

/// <summary>
/// یک شمارهٔ بازاریابی — سرنخِ کمپین پیامکی، آورده‌شده از الگوی کارکور.
///
/// ⚠️ این جدول با حساب‌های بارگو (راننده/صاحب بار/شرکت) یکی نیست و نباید بشود:
/// کاربر کسی است که خودش ثبت‌نام کرده؛ مخاطب کسی است که شماره‌اش از فهرست صنفی
/// یا خودِ بانک کاربران وارد شده و شاید هیچ رابطه‌ای با ما نداشته باشد. برای همین
/// <see cref="OptedOut"/> اینجاست و روی حساب‌ها نیست: تنها راهِ محترمانهٔ
/// نگه‌داشتن کسی که گفته «دیگر پیام نده».
///
/// Mobile یکتاست تا یک نفر از دو فهرستِ متفاوت دو بار پیامک نگیرد.
/// </summary>
public class MarketingContact
{
    public int MarketingContactId { get; set; }

    /// <summary>09xxxxxxxxx نرمال‌شده — کلید یکتای واقعی این جدول.</summary>
    [MaxLength(11)] public string Mobile { get; set; } = "";

    [MaxLength(200)] public string? Name { get; set; }

    /// <summary>گروه — «رانندگان»، «صاحبان بار»، «شرکت‌های حمل‌ونقل» یا هر برچسب فایل ورودی.</summary>
    [MaxLength(100)] public string? Category { get; set; }

    [MaxLength(50)] public string? Province { get; set; }
    [MaxLength(50)] public string? City { get; set; }
    [MaxLength(500)] public string? Address { get; set; }

    public double? Lat { get; set; }
    public double? Lng { get; set; }

    /// <summary>نام فایل یا منبعی که این ردیف از آن آمده — برای ردیابی.</summary>
    [MaxLength(120)] public string? Source { get; set; }

    /// <summary>
    /// گفته «دیگر پیام نده». هیچ کمپینی این شماره را نمی‌گیرد — نه فقط در لحظهٔ
    /// ساخت صف، بلکه دوباره در لحظهٔ ارسال هم بررسی می‌شود، چون بین ساخت صف و
    /// رسیدن نوبت ممکن است ساعت‌ها فاصله باشد.
    /// </summary>
    public bool OptedOut { get; set; }
    public DateTime? OptedOutAt { get; set; }

    /// <summary>آخرین باری که واقعاً پیامکی به این شماره رفت.</summary>
    public DateTime? LastSentAt { get; set; }

    /// <summary>چند پیامک تا حالا گرفته — برای اینکه کسی زیر بمباران نرود.</summary>
    public int SentCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------- کمپین

/// <summary>وضعیت کمپین — رشته مثل بقیهٔ وضعیت‌های بارگو، نه enum.</summary>
public static class CampaignStatus
{
    /// <summary>نوشته شده، صف ساخته شده، ولی هنوز دکمهٔ شروع زده نشده.</summary>
    public const string Draft = "draft";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Done = "done";
    public const string Cancelled = "cancelled";

    public static string Label(string s) => s switch
    {
        Draft => "پیش‌نویس",
        Running => "در حال ارسال",
        Paused => "متوقف",
        Done => "تمام‌شده",
        Cancelled => "لغوشده",
        _ => s
    };

    public static string Tone(string s) => s switch
    {
        Running => "inf",
        Done => "ok",
        Paused => "wait",
        Cancelled => "no",
        _ => "mut"
    };
}

/// <summary>وضعیت یک شماره در صف کمپین.</summary>
public static class CampaignRecipientStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    /// <summary>در لحظهٔ ارسال معلوم شد نباید برود (لغو اشتراک شده بود).</summary>
    public const string Skipped = "skipped";

    public static string Label(string s) => s switch
    {
        Sent => "موفق",
        Failed => "ناموفق",
        Skipped => "ردشده (لغو اشتراک)",
        _ => "در صف"
    };
}

/// <summary>
/// یک نوبت ارسال انبوه با متن دلخواه مدیر.
///
/// چرا صفِ ثبت‌شده و نه حلقه در همان درخواست: ارسال به چند هزار شماره ده‌ها دقیقه
/// طول می‌کشد. اگر در درخواست HTTP باشد، بستنِ مرورگر کار را نصفه می‌گذارد و
/// هیچ‌کس نمی‌داند کدام شماره‌ها پیام گرفتند. با صف، هر شماره وضعیت خودش را دارد
/// و ارسالِ دوباره کسی را دو بار نمی‌گیرد.
/// </summary>
public class SmsCampaign
{
    public int SmsCampaignId { get; set; }

    [MaxLength(120)] public string Title { get; set; } = "";

    /// <summary>متن دقیقی که ارسال می‌شود — قالبِ خام، جای‌نشان‌ها هنگام ارسال پر می‌شوند.</summary>
    [MaxLength(500)] public string Body { get; set; } = "";

    [MaxLength(12)] public string Status { get; set; } = CampaignStatus.Draft;

    // فیلترهایی که صف با آن‌ها ساخته شد — فقط برای نمایش در گزارش.
    [MaxLength(100)] public string? FilterCategory { get; set; }
    [MaxLength(50)] public string? FilterProvince { get; set; }
    [MaxLength(50)] public string? FilterCity { get; set; }

    /// <summary>
    /// سقف همین کمپین؛ صف بیش از این ساخته نمی‌شود.
    ///
    /// دلیل وجودش پول است: یک اشتباهِ فیلتر روی بانک بزرگ یعنی میلیون‌ها تومان.
    /// سقف یعنی بدترین حالتِ یک کلیک اشتباه، هزینهٔ همین عدد است.
    /// </summary>
    public int Cap { get; set; } = DefaultCap;

    public const int DefaultCap = 5000;

    public int Total { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }

    public int CreatedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public List<SmsCampaignRecipient> Recipients { get; set; } = [];

    /// <summary>
    /// تعداد پیامک هر گیرنده — با احتساب بند لغو که موتور خودش می‌افزاید.
    ///
    /// ⚠️ این عدد باید همانی باشد که اپراتور پول می‌گیرد، نه طول متن مدیر.
    /// قاعدهٔ ساده ceil(len/70) دو جا کم می‌شمرد: بخش دوم پیامک ۶۷ نویسه دارد
    /// نه ۷۰، و بند لغو اصلاً شمرده نمی‌شد — یعنی برآوردِ نصفِ واقعیت.
    /// </summary>
    public static int Parts(string? body) =>
        Services.SmsSegments.Count(WithOptOutAllowance(body));

    /// <summary>متن به‌علاوهٔ جای بند لغو — فقط برای اندازه‌گیری.</summary>
    public static string WithOptOutAllowance(string? body)
    {
        var s = (body ?? "").Trim();
        return s.Length == 0
            ? s
            : s + new string('م', Services.OptOutLinks.ReserveChars(Host));
    }

    /// <summary>
    /// همان دامنه‌ای که موتور ارسال در بند لغو می‌گذارد. بدون https:// تا چند
    /// نویسه از هر پیامک صرفه‌جویی شود — پیام‌رسان‌ها همین را هم لینک می‌کنند.
    /// </summary>
    public const string Host = "bargo.ir";
}

/// <summary>یک شماره در صف یک کمپین — وضعیتش جدا نگه داشته می‌شود.</summary>
public class SmsCampaignRecipient
{
    public int SmsCampaignRecipientId { get; set; }

    public int CampaignId { get; set; }
    public SmsCampaign? Campaign { get; set; }

    public int ContactId { get; set; }
    public MarketingContact? Contact { get; set; }

    /// <summary>
    /// شماره در لحظهٔ ساخت صف کپی می‌شود و از Contact خوانده نمی‌شود: گزارشِ
    /// «به چه شماره‌ای چه فرستادیم» باید بعدها هم درست بماند، حتی اگر ردیف
    /// مخاطب ویرایش یا حذف شود.
    /// </summary>
    [MaxLength(11)] public string Mobile { get; set; } = "";

    [MaxLength(12)] public string Status { get; set; } = CampaignRecipientStatus.Pending;

    [MaxLength(200)] public string? Error { get; set; }

    public DateTime? SentAt { get; set; }
}
