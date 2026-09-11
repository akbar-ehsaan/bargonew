namespace Bargo.Web.Services;

/// <summary>اعتبارسنجی شناسه‌های ایرانی — کد ملی، شناسهٔ ملی شرکت، شبا.</summary>
public static class IranId
{
    /// <summary>کد ملی ۱۰ رقمی با رقم کنترل.</summary>
    public static bool IsNationalCode(string? input)
    {
        var s = Fa.Latin(input);
        if (s.Length != 10 || !s.All(char.IsAsciiDigit) || s.Distinct().Count() == 1) return false;
        var check = s[9] - '0';
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (s[i] - '0') * (10 - i);
        var r = sum % 11;
        return r < 2 ? check == r : check == 11 - r;
    }

    /// <summary>شناسهٔ ملی اشخاص حقوقی — ۱۱ رقم با رقم کنترل.</summary>
    public static bool IsCompanyNationalId(string? input)
    {
        var s = Fa.Latin(input);
        if (s.Length != 11 || !s.All(char.IsAsciiDigit)) return false;
        int[] coef = [29, 27, 23, 19, 17, 29, 27, 23, 19, 17];
        var d = (s[9] - '0') + 2;
        var sum = 0;
        for (var i = 0; i < 10; i++) sum += ((s[i] - '0') + d) * coef[i];
        var r = sum % 11;
        if (r == 10) r = 0;
        return r == s[10] - '0';
    }

    /// <summary>شبا: IR + ۲۴ رقم، با بررسی mod 97.</summary>
    public static bool IsSheba(string? input)
    {
        var s = Fa.Latin(input).Replace(" ", "").ToUpperInvariant();
        if (!s.StartsWith("IR")) s = "IR" + s;
        if (s.Length != 26 || !s[2..].All(char.IsAsciiDigit)) return false;
        var rearranged = s[4..] + "1827" + s[2..4]; // I=18, R=27
        var mod = 0;
        foreach (var ch in rearranged) mod = (mod * 10 + (ch - '0')) % 97;
        return mod == 1;
    }

    public static string NormSheba(string? input)
    {
        var s = Fa.Latin(input).Replace(" ", "").ToUpperInvariant();
        return s.StartsWith("IR") ? s : "IR" + s;
    }

    /// <summary>
    /// موبایل ایرانی معتبر؟ قاعدهٔ نرمال‌سازی (۰۹xxxxxxxxx، پذیرش +98 و ارقام فارسی)
    /// یک‌جا در <see cref="Fa.NormMobile"/> است؛ این‌جا فقط به آن تکیه می‌شود تا دو تعریف نسازیم.
    /// </summary>
    public static bool IsMobile(string? input) => Fa.NormMobile(input).Length == 11;

    /// <summary>
    /// تلفن ثابت: ارقام لاتین، بدون فاصله و جداکنندهٔ هزارگان؛ خط تیره و + می‌مانند
    /// (مثل ۰۲۱-۸۸۰۰۰۰۰۰). همان چیزی که در پایگاه‌داده ذخیره می‌شود.
    /// </summary>
    public static string NormPhone(string? input) => Fa.Latin(input).Replace(" ", "");

    /// <summary>
    /// تلفن ثابت با کد شهر: ۸ تا ۲۰ نویسه، فقط رقم و «-» و «+». برای تلفن ثابت
    /// رقم کنترل رسمی وجود ندارد؛ همین قاعدهٔ ساختاری در کل پروژه ملاک است.
    /// </summary>
    public static bool IsPhone(string? input)
    {
        var s = NormPhone(input);
        return s.Length is >= 8 and <= 20 && s.All(ch => char.IsAsciiDigit(ch) || ch is '-' or '+');
    }

    /// <summary>
    /// کد پستی ۱۰ رقمی ایران — قاعدهٔ رسمی شرکت پست: با صفر شروع نمی‌شود، رقم «۲»
    /// در آن به‌کار نمی‌رود، رقم پنجم «۰» یا «۵» نیست و چهار رقم اول یکسان نیستند.
    /// (کد پستی رقم کنترل ندارد؛ فقط همین قواعد ساختاری قابل بررسی‌اند.)
    /// </summary>
    public static bool IsPostalCode(string? input)
    {
        var s = Fa.Latin(input).Replace("-", "").Replace(" ", "");
        if (s.Length != 10 || !s.All(char.IsAsciiDigit)) return false;
        if (s[0] == '0' || s.Contains('2')) return false;
        if (s[4] is '0' or '5') return false;
        return s[..4].Distinct().Count() > 1;
    }

    /// <summary>
    /// کد اقتصادی: ۱۰ تا ۱۴ رقم. سازمان مالیاتی الگوریتم رقم کنترل را منتشر نکرده و
    /// کدهای قدیمی (۱۲ رقمی با پیشوند ۴) و جدید (شناسهٔ ملی + پسوند) هر دو رایج‌اند؛
    /// پس فقط طول و رقم‌بودن بررسی می‌شود — همان قاعده‌ای که در فرم‌ها اعمال شده است.
    /// </summary>
    public static bool IsEconomicCode(string? input)
    {
        var s = Fa.Latin(input);
        return s.Length is >= 10 and <= 14 && s.All(char.IsAsciiDigit);
    }
}
