namespace Bargo.Web.Services;

/// <summary>
/// مخفی‌سازی هویت طرفین تا تأیید نهایی. راننده/شرکت و صاحب بار تا وقتی کرایهٔ سفر
/// پرداخت نشده (<c>Trip.IsPaid</c>) نام، موبایل و پلاک همدیگر را نمی‌بینند؛ در مرحلهٔ
/// پیشنهاد هم همیشه مخفی است. ارتباط پیش از آن فقط از راه گفتگوی درون‌برنامه‌ای است
/// تا معامله بیرون از بارگو بسته نشود.
/// </summary>
public static class Privacy
{
    public const string HiddenName = "پس از پرداخت کرایه";
    public const string HiddenNote = "مشخصات طرف مقابل پس از پرداخت کرایه نمایش داده می‌شود؛ تا آن زمان از «گفتگو» پیام بدهید.";

    public static string Name(string? name, bool revealed) =>
        revealed ? (string.IsNullOrWhiteSpace(name) ? "—" : name!) : HiddenName;

    public static string? Optional(string? value, bool revealed) => revealed ? value : null;
}
