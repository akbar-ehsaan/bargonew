namespace Bargo.Web.Services;

/// <summary>
/// شمارش «چند پیامک» — همان چیزی که اپراتور پول می‌گیرد (آورده‌شده از کارکور).
///
/// ⚠️ قاعدهٔ ساده ceil(len / 70) دو جا غلط می‌شمرد:
///
/// ۱) پیامک چندبخشی ۷۰ نویسه ندارد، ۶۷ دارد. سه نویسه صرف سرآیندی می‌شود که
///    بخش‌ها را به هم می‌چسباند. یعنی متن ۱۴۰ نویسه‌ای سه بخش است و نه دو، و
///    روی ده‌ها هزار گیرنده این یعنی ده‌ها هزار پیامک حساب‌نشده.
///
/// ۲) متن لاتین اصلاً ۷۰ نویسه‌ای نیست؛ ۱۶۰ (و در چندبخشی ۱۵۳) است. با قاعدهٔ
///    ساده، یک متن انگلیسی بیش از دو برابر واقعیت گران دیده می‌شد.
///
/// قاعده: اگر *یک* نویسه هم بیرون از جدول GSM باشد — هر حرف فارسی، هر اموجی،
/// حتی نیم‌فاصله — کل پیام یونیکد می‌شود.
/// </summary>
public static class SmsSegments
{
    public const int UnicodeSingle = 70;
    public const int UnicodeMulti = 67;
    public const int GsmSingle = 160;
    public const int GsmMulti = 153;

    /// <summary>جدول پایهٔ GSM 03.38 — هرچه بیرون این باشد یونیکد است.</summary>
    private const string GsmBasic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?"
        + "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    /// <summary>این‌ها در GSM دو جا می‌گیرند، نه یکی.</summary>
    private const string GsmExtended = "^{}\\[~]|€";

    public readonly record struct Measurement(int Chars, int Parts, int PerPart, bool Unicode);

    public static bool IsUnicode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
            if (!GsmBasic.Contains(ch) && !GsmExtended.Contains(ch))
                return true;
        return false;
    }

    public static Measurement Measure(string? text)
    {
        var s = text ?? "";
        if (s.Length == 0) return new Measurement(0, 0, UnicodeSingle, true);

        var unicode = IsUnicode(s);

        // در حالت GSM، نویسه‌های جدول گسترده دو جا می‌گیرند.
        var weight = s.Length;
        if (!unicode)
            foreach (var ch in s)
                if (GsmExtended.Contains(ch)) weight++;

        var single = unicode ? UnicodeSingle : GsmSingle;
        var multi = unicode ? UnicodeMulti : GsmMulti;

        return weight <= single
            ? new Measurement(s.Length, 1, single, unicode)
            : new Measurement(s.Length, (int)Math.Ceiling(weight / (double)multi), multi, unicode);
    }

    public static int Count(string? text) => Measure(text).Parts;
}
