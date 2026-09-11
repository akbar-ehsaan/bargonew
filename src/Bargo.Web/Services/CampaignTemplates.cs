namespace Bargo.Web.Services;

/// <summary>
/// متن‌های آمادهٔ کمپین — پیشنهاد، نه قالب قفل‌شده. مدیر همیشه پیش از ارسال
/// ویرایششان می‌کند (الگوی کارکور، با متن‌های حوزهٔ حمل بار).
///
/// چرا از پیش نوشته شده‌اند: اولین کمپین معمولاً سرِ صفحهٔ خالی نوشته می‌شود و
/// همان‌جا دو چیز خراب می‌شود — متن یا آن‌قدر بلند می‌شود که بی‌خبر سه پیامک
/// شود، یا آن‌قدر کوتاه که گیرنده نفهمد چه کسی و چرا پیام داده.
///
/// ⚠️ هر متن با bargo.ir تمام می‌شود: لینک تبلیغ باید *پیش از* بند لغو و جدا
/// از آن دیده شود، نه قاطیِ «لغو: …».
///
/// ⚠️ طول همه با احتساب بند لغو باید دو پیامک بماند و نه سه — متنی که بی‌صدا
/// سه‌بخشی شود هزینهٔ کمپین را یک‌ونیم برابر می‌کند.
/// </summary>
public static class CampaignTemplates
{
    public sealed record Template(string Category, string Title, string Body);

    /// <summary>گروه «همه» — وقتی کمپین روی کل بانک است.</summary>
    public const string AnyCategory = "";

    /// <summary>گروه‌هایی که «ورود از کاربران بارگو» می‌سازد — متن‌ها به همین‌ها گره خورده‌اند.</summary>
    public const string CategoryDrivers = "رانندگان";
    public const string CategoryShippers = "صاحبان بار";
    public const string CategoryCompanies = "شرکت‌های حمل‌ونقل";

    /// <summary>نشانی‌ای که ته هر متن می‌نشیند.</summary>
    public const string Link = "bargo.ir";

    public static readonly IReadOnlyList<Template> All =
    [
        new(AnyCategory, "دعوت عمومی",
            "در بارگو بار و راننده بی‌واسطه به هم می‌رسند؛ ثبت‌نام رایگان است.\n" + Link),

        new(AnyCategory, "دعوت با نام",
            "{نام} گرامی، ثبت‌نام در سامانهٔ حمل بار بارگو رایگان است و چند دقیقه وقت می‌برد.\n" + Link),

        new(CategoryDrivers, "دعوت راننده",
            "رانندهٔ گرامی، بارهای مسیر خودتان را در بارگو ببینید و مستقیم قیمت بدهید.\n" + Link),

        new(CategoryDrivers, "بار برگشت",
            "{نام} گرامی، برای مسیر برگشت خالی برنگردید؛ بارهای {شهر} و اطراف در بارگوست.\n" + Link),

        new(CategoryShippers, "دعوت صاحب بار",
            "بار خود را رایگان در بارگو ثبت کنید و از راننده‌های نزدیک قیمت بگیرید.\n" + Link),

        new(CategoryShippers, "رهگیری بار",
            "{نام} گرامی، در بارگو بارتان را ثبت کنید و لحظه‌به‌لحظه روی نقشه رهگیری کنید.\n" + Link),

        new(CategoryCompanies, "دعوت شرکت حمل‌ونقل",
            "شرکت شما رایگان در بارگو ثبت می‌شود؛ ناوگان، رانندگان و بارها یک‌جا مدیریت می‌شوند.\n" + Link),

        // ── پیگیری: برای کسی که یک بار دعوت گرفته و ثبت‌نام نکرده.
        new(AnyCategory, "یادآوری — هنوز ثبت‌نام نکرده‌اید",
            "ثبت‌نام شما در بارگو هنوز کامل نشده؛ چند دقیقه وقت می‌برد و هزینه‌ای ندارد.\n" + Link),

        new(AnyCategory, "مزیت — بارنامه و تسویه",
            "در بارگو بارنامه، رهگیری و تسویهٔ کرایه همه در خود سامانه انجام می‌شود.\n" + Link),
    ];

    /// <summary>متن‌های مناسب یک گروه: مالِ خودش، به‌علاوهٔ عمومی‌ها.</summary>
    public static IEnumerable<Template> For(string? category) =>
        All.Where(t => t.Category == AnyCategory ||
                       string.Equals(t.Category, category, StringComparison.Ordinal));
}
