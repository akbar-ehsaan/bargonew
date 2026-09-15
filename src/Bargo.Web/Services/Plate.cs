using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace Bargo.Web.Services;

/// <summary>اجزای پلاک ملی خودرو: «دو رقم، حرف، سه رقم — ایران، دو رقم». ارقام همیشه لاتین‌اند تا مقایسه و جستجو در پایگاه‌داده یک‌شکل بماند؛ نمایش فارسی فقط لحظهٔ رندر انجام می‌شود.</summary>
public sealed record PlateParts(string TwoDigits, string Letter, string ThreeDigits, string Iran);

/// <summary>
/// پلاک خودرو در کل سامانه یک «رشته» می‌ماند (بدون تغییر اسکیما) ولی با قالب متعارف
/// <b>«12ب345-67»</b> ذخیره می‌شود: ارقام لاتین، حرف فارسی، خط تیره پیش از کد «ایران».
///
/// چرا رشتهٔ متعارف و نه چهار ستون؟ پلاک در ده‌ها جدول/نما فقط «نمایش» می‌شود و تنها
/// جای حساس، یکتایی و جستجوست؛ یک قالب متعارف هر دو را بدون Migration حل می‌کند.
/// چرا ارقام لاتین؟ تا «۱۲ ع ۳۴۵» و «12 ع 345» که کاربرها به هر دو شکل می‌نویسند،
/// بعد از Normalize یکی شمرده شوند (همان دردی که NormPlateهای قبلی جدا‌جدا حل می‌کردند).
/// ورودی‌های قدیمی/آزادِ غیرقابل‌تجزیه دست‌نخورده می‌مانند و در نمایش به همان شکل ساده
/// دیده می‌شوند؛ پس دادهٔ موجود هیچ‌وقت خراب نمی‌شود.
/// </summary>
public static class Plate
{
    /// <summary>
    /// حرف‌هایی که واقعاً روی پلاک خودروهای ایران می‌آیند — نه کل الفبا. بعضی حرف‌ها
    /// (چ، ح، خ، ذ، ر، ض، ظ، غ) در نظام شماره‌گذاری استفاده نمی‌شوند؛ «ژ» ویژهٔ
    /// جانبازان/معلولان است و «D» و «S» پلاک‌های دیپلماتیک و خدمت سفارت‌ها.
    /// «هـ» با کشیده نگه داشته می‌شود چون روی خود پلاک همین‌طور نوشته می‌شود.
    /// </summary>
    public static readonly string[] Letters =
    [
        "الف", "ب", "پ", "ت", "ث", "ج", "د", "ز", "س", "ش", "ص", "ط",
        "ع", "ف", "ق", "ک", "گ", "ل", "م", "ن", "و", "هـ", "ی",
        "ژ", "D", "S"
    ];

    /// <summary>
    /// تجزیهٔ آسان‌گیر: ارقام فارسی/عربی/لاتین، فاصله و خط تیره و نقطه، واژهٔ «ایران» در هر
    /// جای رشته، «ی/ي» و «ک/ك» عربی و «هـ/ه» — همه پذیرفته می‌شوند. سخت‌گیری فقط روی
    /// ساختار است (۲ رقم + حرف مجاز + ۵ رقم) تا متن آزاد اشتباهی پلاک شمرده نشود.
    /// </summary>
    public static PlateParts? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        // Fa.Latin ارقام را لاتین می‌کند؛ واژهٔ «ایران» فقط برچسب است و جزو داده نیست.
        var s = Fa.Latin(input).Replace("ایران", "").Replace("ايران", "");

        var head = new StringBuilder(2);    // دو رقم نخست
        var tail = new StringBuilder(5);    // سه رقم + کد ایران (ممکن است چسبیده بیایند: «12ع34567»)
        var letters = new StringBuilder(3);
        var afterLetter = false;

        foreach (var raw in s)
        {
            var ch = raw switch { 'ي' => 'ی', 'ك' => 'ک', 'd' => 'D', 's' => 'S', _ => raw };
            if (ch is ' ' or '-' or '_' or '.' or '،' or '/' or '\t' or '‌' or '‎' or '‏') continue;
            if (ch is >= '0' and <= '9')
            {
                var b = afterLetter ? tail : head;
                if (b.Length >= (afterLetter ? 5 : 2)) return null; // رقم اضافه یعنی این متن پلاک نیست
                b.Append(ch);
            }
            else
            {
                if (tail.Length > 0) return null;   // حرف بعد از ارقام پایانی: ورودی آزاد است
                if (ch == 'ـ') continue;            // کشیدهٔ «هـ» حرف مستقلی نیست
                if (letters.Length >= 3) return null;
                letters.Append(ch);
                afterLetter = true;
            }
        }

        // «ه» تنها، همان «هـ» روی پلاک است؛ بقیهٔ حرف‌ها باید عیناً در فهرست مجاز باشند.
        var letter = letters.ToString() is "ه" ? "هـ" : letters.ToString();
        if (head.Length != 2 || tail.Length != 5 || !Letters.Contains(letter)) return null;
        var t = tail.ToString();
        return new PlateParts(head.ToString(), letter, t[..3], t[3..]);
    }

    /// <summary>قالب متعارف برای ذخیره («12ب345-67»)؛ ورودی غیرقابل‌تجزیه فقط Trim می‌شود تا دادهٔ قدیمی از دست نرود.</summary>
    public static string Normalize(string? input) =>
        TryParse(input) is { } p ? $"{p.TwoDigits}{p.Letter}{p.ThreeDigits}-{p.Iran}" : (input ?? "").Trim();

    /// <summary>شکل خواندنی برای متن‌های جاری (پیام، گزینهٔ فهرست): «۱۲ ب ۳۴۵ ایران ۶۷» — چون قالب متعارفِ خط‌تیره‌دار وسط جملهٔ راست‌به‌چپ به‌هم می‌ریزد.</summary>
    public static string Pretty(string? input) =>
        TryParse(input) is { } p
            ? $"{Fa.Digits(p.TwoDigits)} {p.Letter} {Fa.Digits(p.ThreeDigits)} ایران {Fa.Digits(p.Iran)}"
            : (input ?? "").Trim();

    /// <summary>
    /// نشان پلاک (همان چیزی که partial «_Plate» رندر می‌کند): نوار آبی با پرچم، بخش اصلی
    /// «دو رقم، حرف، سه رقم» و خانهٔ «ایران/کد استان». چیدمان مثل خود پلاک چپ‌به‌راست است،
    /// پس کل نشان dir=ltr می‌گیرد تا وسط صفحهٔ RTL آینه نشود. پلاک غیرقابل‌تجزیه با همان
    /// ظاهر سادهٔ قدیمی (کلاس plate) می‌آید تا دادهٔ آزادِ قدیمی هم نمایشی معقول داشته باشد.
    /// </summary>
    public static IHtmlContent Html(string? plateNo)
    {
        if (string.IsNullOrWhiteSpace(plateNo)) return new HtmlString("<span class=\"muted\">—</span>");
        var e = HtmlEncoder.Default;
        if (TryParse(plateNo) is not { } p)
            return new HtmlString($"<span class=\"plate\">{e.Encode(plateNo.Trim())}</span>");
        return new HtmlString(
            $"<span class=\"plate-ir\" dir=\"ltr\" title=\"{e.Encode(Pretty(plateNo))}\">" +
            "<span class=\"band\"><i></i></span>" +
            // نام کلاس «mid» و نه «main» — «main» کلاس پوستهٔ صفحه است (margin سایدبار) و پلاک را کش می‌داد
            $"<span class=\"mid\"><span>{Fa.Digits(p.TwoDigits)}</span><b class=\"l\">{e.Encode(p.Letter)}</b><span>{Fa.Digits(p.ThreeDigits)}</span></span>" +
            $"<span class=\"ir\"><small>ایران</small><b>{Fa.Digits(p.Iran)}</b></span>" +
            "</span>");
    }
}
