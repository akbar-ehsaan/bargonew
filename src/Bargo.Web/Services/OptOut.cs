using System.Security.Cryptography;
using System.Text;

namespace Bargo.Web.Services;

/// <summary>
/// لینک «دیگر پیام نده» که ته هر پیامک تبلیغاتی می‌رود (آورده‌شده از کارکور).
///
/// چرا لازم است: موتور کمپین لغو اشتراک را محترم می‌شمارد، ولی اگر تنها راهِ
/// ست‌شدن آن تیک دستی مدیر باشد، گیرندهٔ پیامک هیچ راهی برای قطعش ندارد و آن
/// بررسی عملاً روی هیچ‌کس اثر نمی‌کند.
///
/// ⚠️ چرا امضا و نه فقط شناسه: با /u/12 هر کسی می‌توانست با شمردن عدد، کل
/// بانک مخاطبان را از فهرست بیندازد.
///
/// ⚠️ چرا HMAC و نه IDataProtector.Protect: خروجی Protect هر بار فرق می‌کند
/// (nonce تصادفی دارد)، پس امضایی که با آن ساخته شود هیچ‌وقت با خودش هم
/// نمی‌خواند. ضمناً خروجی‌اش برای پیامک خیلی بلند است.
/// </summary>
public class OptOutLinks
{
    private const int SigChars = 8;

    // حروف مبهم حذف شده، چون ممکن است کسی لینک را از روی کاغذ تایپ کند.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly byte[] _key;

    /// <param name="keyDir">
    /// همان پوشه‌ای که کلیدهای DataProtection بارگو در آن می‌نشینند (keys/).
    /// کلیدِ رفته یعنی لینک‌هایی که در پیامک‌های فرستاده‌شده هست، دیگر باز نمی‌شوند.
    /// </param>
    public OptOutLinks(string keyDir)
    {
        Directory.CreateDirectory(keyDir);
        // System.IO.Path با صراحت، چون متد Path(int) همین کلاس سایه‌اش می‌اندازد
        var file = System.IO.Path.Combine(keyDir, "optout.key");

        if (File.Exists(file))
        {
            _key = Convert.FromBase64String(File.ReadAllText(file).Trim());
            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(file, Convert.ToBase64String(_key));
    }

    /// <summary>امضای کوتاه یک شناسه.</summary>
    public string Sign(int contactId)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"optout:{contactId}"));
        var sb = new StringBuilder(SigChars);
        for (var i = 0; i < SigChars; i++) sb.Append(Alphabet[mac[i] % Alphabet.Length]);
        return sb.ToString();
    }

    /// <summary>امضا درست است؟</summary>
    public bool Verify(int contactId, string? signature) =>
        !string.IsNullOrWhiteSpace(signature) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Sign(contactId)),
            Encoding.UTF8.GetBytes(signature.Trim().ToUpperInvariant()));

    /// <summary>مسیر نسبی لغو برای این مخاطب.</summary>
    public string Path(int contactId) => $"/u/{contactId}{Sign(contactId)}";

    /// <summary>بندی که ته متن کمپین اضافه می‌شود.</summary>
    /// <remarks>
    /// ⚠️ خودِ موتور اضافه‌اش می‌کند و نه مدیر. اگر نوشتنش به عهدهٔ متن مدیر
    /// بود، اولین کمپینی که یادش می‌رفت پیامک تبلیغاتیِ بی‌راهِ‌خروج می‌شد —
    /// برای کسانی که هیچ‌وقت اجازه‌ای نداده‌اند.
    /// </remarks>
    public string Suffix(int contactId, string host) => $"\nلغو: {host}{Path(contactId)}";

    /// <summary>
    /// جایی که بند لغو خواهد گرفت — برای برآورد هزینه *پیش از* ارسال.
    ///
    /// ⚠️ بدون این، صفحهٔ ساخت کمپین دروغ می‌گفت: مدیر ۷۰ نویسه می‌نوشت، صفحه
    /// «۱ پیامک» نشان می‌داد، و چون موتور هنگام ارسال این بند را هم می‌چسباند،
    /// واقعاً ۲ پیامک می‌رفت — یعنی دو برابر مبلغی که مدیر تصمیم گرفته بود.
    ///
    /// بدترین حالت حساب می‌شود (شناسهٔ هفت‌رقمی) تا برآورد از واقعیت کمتر نباشد.
    /// </summary>
    public static int ReserveChars(string host) =>
        "\nلغو: ".Length + host.Length + "/u/".Length + 7 + SigChars;

    /// <summary>«12ABCDEFGH» را به شناسه و امضا می‌شکند.</summary>
    public static (int Id, string Sig)? Split(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length <= SigChars) return null;
        var idPart = token[..^SigChars];
        var sig = token[^SigChars..];
        return int.TryParse(idPart, out var id) && id > 0 ? (id, sig) : null;
    }
}
