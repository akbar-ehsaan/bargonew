using System.Globalization;
using System.Text;

namespace Bargo.Web.Services;

/// <summary>
/// تاریخ شمسی، ساعت تهران، ارقام فارسی و مبلغ.
///
/// قاعدهٔ زمان در کل پروژه: همهٔ ستون‌های DateTime در پایگاه‌داده <b>UTC</b>اند.
/// فقط هنگام نمایش به وقت تهران تبدیل می‌شوند. تاریخ‌های «تقویمی» که از فرم می‌آیند
/// (انقضای بیمه، تاریخ بارگیری) ظهرِ همان روز به وقت تهران ذخیره می‌شوند؛ ظهر
/// عمداً انتخاب شده تا تبدیل UTC↔تهران هرگز روز را جابه‌جا نکند.
/// </summary>
public static class Fa
{
    private static readonly PersianCalendar Pc = new();
    private static readonly TimeZoneInfo Tehran = FindTehran();

    private static TimeZoneInfo FindTehran()
    {
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Tehran", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
    }

    private static readonly string[] MonthNames =
        ["", "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];

    // ------------------------------------------------------------------ زمان

    public static DateTime Now => ToTehran(DateTime.UtcNow);

    public static DateTime ToTehran(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tehran);

    public static DateTime ToUtc(DateTime tehranLocal) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(tehranLocal, DateTimeKind.Unspecified), Tehran);

    /// <summary>ابتدای امروز به وقت تهران، به UTC — مرز «امروز» در داشبوردها.</summary>
    public static DateTime TodayStartUtc => ToUtc(Now.Date);

    /// <summary>ابتدای ماه شمسی جاری، به UTC.</summary>
    public static DateTime MonthStartUtc
    {
        get
        {
            var n = Now;
            return ToUtc(Pc.ToDateTime(Pc.GetYear(n), Pc.GetMonth(n), 1, 0, 0, 0, 0));
        }
    }

    /// <summary>همان روزِ تقویمی (به وقت تهران) با ساعت مشخص، به UTC.</summary>
    public static DateTime WithHour(DateTime utc, int hour) =>
        ToUtc(ToTehran(utc).Date.AddHours(Math.Clamp(hour, 0, 23)));

    // ------------------------------------------------------------------ نمایش

    public static string Digits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch is >= '0' and <= '9' ? (char)('۰' + (ch - '0')) : ch);
        return sb.ToString();
    }

    /// <summary>ارقام فارسی/عربی → لاتین؛ جداکننده‌های هزارگان و نیم‌فاصله حذف می‌شوند.</summary>
    public static string Latin(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= '۰' and <= '۹') sb.Append((char)('0' + (ch - '۰')));
            else if (ch is >= '٠' and <= '٩') sb.Append((char)('0' + (ch - '٠')));
            else if (ch is ',' or '٬' or '‌') { }
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    public static string N(long v) => Digits(v.ToString("N0", CultureInfo.InvariantCulture));
    public static string N(int v) => Digits(v.ToString("N0", CultureInfo.InvariantCulture));
    public static string N(double v, int decimals = 0) => Digits(v.ToString("N" + decimals, CultureInfo.InvariantCulture));
    public static string N(decimal v, int decimals = 0) => Digits(v.ToString("N" + decimals, CultureInfo.InvariantCulture));

    /// <summary>مبلغ ریالی به تومان: «۱۲,۵۰۰,۰۰۰ تومان».</summary>
    public static string Toman(long rial) => N(rial / 10) + " تومان";
    public static string Toman(long? rial) => rial.HasValue ? Toman(rial.Value) : "—";

    /// <summary>ورودی تومان از فرم (با ارقام فارسی یا ویرگول) → ریال.</summary>
    public static long? ParseToman(string? input) =>
        long.TryParse(Latin(input), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t * 10 : null;

    public static string Date(DateTime? utc)
    {
        if (utc is null || utc.Value == default) return "—";
        var t = ToTehran(utc.Value);
        return Digits($"{Pc.GetYear(t):0000}/{Pc.GetMonth(t):00}/{Pc.GetDayOfMonth(t):00}");
    }

    public static string Time(DateTime? utc) =>
        utc is null || utc.Value == default ? "—" : Digits(ToTehran(utc.Value).ToString("HH:mm", CultureInfo.InvariantCulture));

    /// <summary>تاریخ و ساعت: «۱۴۰۵/۰۶/۲۱ ۱۴:۳۰».</summary>
    public static string Stamp(DateTime? utc) => utc is null || utc.Value == default ? "—" : $"{Date(utc)} {Time(utc)}";

    /// <summary>«۲۱ شهریور ۱۴۰۵»</summary>
    public static string LongDate(DateTime utc)
    {
        var t = ToTehran(utc);
        return Digits($"{Pc.GetDayOfMonth(t)} {MonthNames[Pc.GetMonth(t)]} {Pc.GetYear(t)}");
    }

    /// <summary>مقدارِ کادرِ ورودی تاریخ (با ارقام لاتین تا jdate.js بخواند).</summary>
    public static string DateBox(DateTime? utc)
    {
        if (utc is null || utc.Value == default) return "";
        var t = ToTehran(utc.Value);
        return $"{Pc.GetYear(t):0000}/{Pc.GetMonth(t):00}/{Pc.GetDayOfMonth(t):00}";
    }

    public static string Ago(DateTime utc)
    {
        var s = DateTime.UtcNow - utc;
        if (s.TotalMinutes < 1) return "همین الان";
        if (s.TotalMinutes < 60) return $"{N((int)s.TotalMinutes)} دقیقه پیش";
        if (s.TotalHours < 24) return $"{N((int)s.TotalHours)} ساعت پیش";
        if (s.TotalDays < 30) return $"{N((int)s.TotalDays)} روز پیش";
        return Date(utc);
    }

    /// <summary>«۰۵۰۶۲۱» — دو رقم سال، ماه، روز شمسی؛ برای کد بار و سفر (با ارقام لاتین).</summary>
    public static string Stamp6(DateTime utc)
    {
        var t = ToTehran(utc);
        return $"{Pc.GetYear(t) % 100:00}{Pc.GetMonth(t):00}{Pc.GetDayOfMonth(t):00}";
    }

    // ------------------------------------------------------------------ ورودی

    /// <summary>
    /// «۱۴۰۵/۰۶/۲۱» یا «1405-06-21» (یا میلادی) → ظهرِ همان روز به وقت تهران، به UTC.
    /// ورودی نامعتبر → null.
    /// </summary>
    public static DateTime? ParseDateToUtc(string? input)
    {
        var s = Latin(input);
        if (s.Length == 0) return null;
        var cut = s.IndexOfAny(['T', 't', ' ']);
        if (cut > 0) s = s[..cut];
        var parts = s.Replace('-', '/').Replace('.', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var d))
            return null;
        try
        {
            DateTime local;
            if (y > 1700) local = new DateTime(y, m, d, 12, 0, 0);
            else
            {
                if (y < 100) y += 1400;
                if (m is < 1 or > 12 || d < 1 || d > Pc.GetDaysInMonth(y, m)) return null;
                local = Pc.ToDateTime(y, m, d, 12, 0, 0, 0);
            }
            return ToUtc(local);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>موبایل ایرانی به قالب 09xxxxxxxxx؛ نامعتبر → رشتهٔ خالی.</summary>
    public static string NormMobile(string? input)
    {
        var s = new string(Latin(input).Where(char.IsDigit).ToArray());
        if (s.StartsWith("0098")) s = "0" + s[4..];
        else if (s.StartsWith("98") && s.Length == 12) s = "0" + s[2..];
        else if (s.Length == 10 && s.StartsWith('9')) s = "0" + s;
        return s.Length == 11 && s.StartsWith("09") ? s : "";
    }
}
