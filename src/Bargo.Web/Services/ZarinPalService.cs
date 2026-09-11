using System.Text;
using System.Text.Json;

namespace Bargo.Web.Services;

/// <summary>
/// درگاه زرین‌پال (REST v4) — آورده‌شده از پروژهٔ قدیمی بارگو. دو مرحله دارد:
/// <see cref="RequestAsync"/> یک Authority می‌گیرد و کاربر به صفحهٔ پرداخت می‌رود؛
/// در بازگشت، <see cref="VerifyAsync"/> پرداخت را قطعی می‌کند. تا Verify موفق نشده
/// هیچ پولی به کیف پول نمی‌نشیند. شناسهٔ پذیرنده و حالت آزمایشی از تنظیمات
/// (Gateway.MerchantId و Gateway.Sandbox) خوانده می‌شوند.
/// </summary>
public class ZarinPalService(SettingsService settings, ILogger<ZarinPalService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public record RequestResult(bool Ok, string? Authority, string? Error);
    public record VerifyResult(bool Ok, long RefId, string? CardPan, string? Error);

    public async Task<string> MerchantAsync(CancellationToken ct = default) =>
        (await settings.GetAsync(SettingsService.Keys.GatewayMerchant, ct)).Trim();

    public async Task<bool> SandboxAsync(CancellationToken ct = default) =>
        await settings.GetBoolAsync(SettingsService.Keys.GatewaySandbox, ct);

    /// <summary>شناسهٔ پذیرندهٔ زرین‌پال ۳۶ نویسه است؛ کوتاه‌تر یعنی درگاه پیکربندی نشده.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        (await MerchantAsync(ct)).Length >= 30;

    private static string Api(bool sandbox) => sandbox ? "https://sandbox.zarinpal.com" : "https://api.zarinpal.com";

    public static string StartPayUrl(bool sandbox, string authority) =>
        (sandbox ? "https://sandbox.zarinpal.com" : "https://www.zarinpal.com") + "/pg/StartPay/" + authority;

    public async Task<RequestResult> RequestAsync(long amountRial, string callbackUrl, string description, string? mobile = null,
        CancellationToken ct = default)
    {
        var merchant = await MerchantAsync(ct);
        if (merchant.Length < 30) return new(false, null, "درگاه پرداخت هنوز پیکربندی نشده است (شناسهٔ پذیرنده خالی است).");
        if (amountRial < 10_000) return new(false, null, "حداقل مبلغ پرداخت ۱,۰۰۰ تومان است.");
        var sandbox = await SandboxAsync(ct);

        var payload = new Dictionary<string, object?>
        {
            ["merchant_id"] = merchant,
            ["amount"] = amountRial,
            ["currency"] = "IRR",
            ["callback_url"] = callbackUrl,
            ["description"] = description.Length > 250 ? description[..250] : description
        };
        if (!string.IsNullOrEmpty(mobile)) payload["metadata"] = new Dictionary<string, string> { ["mobile"] = mobile };

        try
        {
            using var resp = await Http.PostAsync(Api(sandbox) + "/pg/v4/payment/request.json",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("code", out var code) && code.GetInt32() == 100 &&
                data.TryGetProperty("authority", out var auth))
                return new(true, auth.GetString(), null);

            var err = ReadError(doc.RootElement);
            log.LogWarning("ZarinPal request failed: {Error} — {Body}", err, body);
            return new(false, null, err);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "ZarinPal request error");
            return new(false, null, "اتصال به درگاه پرداخت برقرار نشد؛ دوباره تلاش کنید.");
        }
    }

    public async Task<VerifyResult> VerifyAsync(long amountRial, string authority, CancellationToken ct = default)
    {
        var merchant = await MerchantAsync(ct);
        var sandbox = await SandboxAsync(ct);
        var payload = new { merchant_id = merchant, amount = amountRial, authority };

        try
        {
            using var resp = await Http.PostAsync(Api(sandbox) + "/pg/v4/payment/verify.json",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("code", out var code) && code.GetInt32() is 100 or 101)
            {
                var refId = data.TryGetProperty("ref_id", out var r) ? r.GetInt64() : 0;
                var cardPan = data.TryGetProperty("card_pan", out var c) ? c.GetString() : null;
                return new(true, refId, cardPan, null);
            }

            var err = ReadError(doc.RootElement);
            log.LogWarning("ZarinPal verify failed: {Error} — {Body}", err, body);
            return new(false, 0, null, err);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "ZarinPal verify error");
            return new(false, 0, null, "تأیید پرداخت با درگاه ممکن نشد؛ اگر مبلغ کسر شده، تا ۷۲ ساعت آینده برمی‌گردد.");
        }
    }

    private static string ReadError(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object &&
            errors.TryGetProperty("code", out var code))
            return Describe(code.GetInt32(), errors.TryGetProperty("message", out var m) ? m.GetString() : null);
        return "پاسخ نامشخص از درگاه پرداخت.";
    }

    /// <summary>ترجمهٔ کدهای خطای زرین‌پال به پیام فارسی قابل نمایش.</summary>
    public static string Describe(int code, string? fallback = null) => code switch
    {
        -9 => "خطای اعتبارسنجی: مبلغ یا مشخصات پرداخت نادرست است.",
        -10 => "شناسهٔ پذیرنده یا آی‌پی پذیرنده نادرست است.",
        -11 => "شناسهٔ پذیرنده فعال نیست.",
        -12 => "تلاش بیش از حد در بازهٔ کوتاه؛ کمی بعد دوباره امتحان کنید.",
        -15 => "درگاه پذیرنده به حالت تعلیق درآمده است.",
        -16 => "سطح تأیید پذیرنده پایین‌تر از نقره‌ای است.",
        -30 => "پذیرنده اجازهٔ دسترسی به تسویهٔ اشتراکی شناور را ندارد.",
        -31 => "حساب بانکی تسویه معرفی نشده است.",
        -33 => "درصدهای تسویهٔ اشتراکی معتبر نیست.",
        -34 => "مبلغ از کل تراکنش بیشتر است.",
        -40 => "پارامترهای اضافی نامعتبر است.",
        -50 => "مبلغ پرداخت‌شده با مبلغ تأیید متفاوت است.",
        -51 => "پرداخت ناموفق بود.",
        -52 => "خطای غیرمنتظره؛ با پشتیبانی تماس بگیرید.",
        -53 => "پرداخت متعلق به این پذیرنده نیست.",
        -54 => "اتوریتی نامعتبر است.",
        101 => "این پرداخت قبلاً تأیید شده است.",
        _ => fallback ?? $"خطای درگاه (کد {code})."
    };
}
