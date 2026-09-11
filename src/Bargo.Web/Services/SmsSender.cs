using System.Text;
using System.Text.Json;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;

namespace Bargo.Web.Services;

public interface ISmsSender
{
    Task<bool> SendAsync(string mobile, string text, string purpose, CancellationToken ct = default);
}

/// <summary>
/// ارسال پیامک — آورده‌شده از پروژهٔ قدیمی بارگو (وب‌سرویس ICTX). سرویس از تنظیمات
/// انتخاب می‌شود: <c>Sms.Provider=log</c> فقط در جدول SmsLogs ثبت می‌کند (پیش‌فرض توسعه)
/// و <c>ictx</c> واقعاً می‌فرستد. کلید، خط و آدرس وب‌سرویس هم از تنظیمات «Sms.*»
/// خوانده می‌شوند تا مدیر بدون انتشار دوباره عوضشان کند. هر ارسال — موفق یا ناموفق —
/// یک ردیف SmsLog دارد؛ SaveChanges با فراخواننده است (مثل WalletService).
/// </summary>
public class SmsSender(BargoDbContext db, SettingsService settings, ILogger<SmsSender> log) : ISmsSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public async Task<bool> SendAsync(string mobile, string text, string purpose, CancellationToken ct = default)
    {
        var provider = (await settings.GetAsync(SettingsService.Keys.SmsProvider, ct)).Trim().ToLowerInvariant();

        var (success, response) = provider switch
        {
            "ictx" => await SendIctxAsync(mobile, text, ct),
            _ => (true, "log-only")
        };

        if (provider != "ictx")
            log.LogInformation("[SMS:{Purpose}] {Mobile}: {Text}", purpose, mobile, text);

        db.SmsLogs.Add(new SmsLog { Mobile = mobile, Text = text, Purpose = purpose, Success = success, Response = response });
        return success;
    }

    private async Task<(bool Ok, string Response)> SendIctxAsync(string mobile, string text, CancellationToken ct)
    {
        var authKey = (await settings.GetAsync(SettingsService.Keys.SmsApiKey, ct)).Trim();
        var line = (await settings.GetAsync(SettingsService.Keys.SmsLine, ct)).Trim();
        var url = (await settings.GetAsync(SettingsService.Keys.SmsUrl, ct)).Trim();
        var username = (await settings.GetAsync(SettingsService.Keys.SmsUsername, ct)).Trim();

        if (authKey.Length == 0 || line.Length == 0)
        {
            log.LogWarning("SMS to {Mobile} skipped: ictx selected but Sms.ApiKey or Sms.Line empty", mobile);
            return (false, "پیکربندی ناقص: کلید API یا شماره خط خالی است");
        }

        var payload = new
        {
            SmsSender = line,
            Mobile = mobile,
            Message = text,
            Authentication = new { Username = username, AuthKey = authKey }
        };

        try
        {
            using var resp = await Http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("SMS to {Mobile} failed: HTTP {Status} — {Body}", mobile, (int)resp.StatusCode, body);
                return (false, $"HTTP {(int)resp.StatusCode}: {Trim(body)}");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("Status", out var st) &&
                    st.TryGetProperty("Code", out var code) && code.GetInt32() == 200)
                    return (true, Trim(body));
                log.LogWarning("SMS to {Mobile} rejected by provider: {Body}", mobile, body);
                return (false, Trim(body));
            }
            catch (JsonException)
            {
                // پاسخ JSON نبود؛ همان متن خام ثبت می‌شود
                return (resp.IsSuccessStatusCode, Trim(body));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "SMS to {Mobile} failed", mobile);
            return (false, "خطای اتصال: " + Trim(e.Message));
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
