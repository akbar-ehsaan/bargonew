using Bargo.Web.Data;
using Bargo.Web.Models.Entities;

namespace Bargo.Web.Services;

public interface ISmsSender
{
    Task<bool> SendAsync(string mobile, string text, string purpose, CancellationToken ct = default);
}

/// <summary>
/// پیامکِ «فقط ثبت». هیچ پیامکی واقعاً ارسال نمی‌شود؛ متن در جدول SmsLogs و لاگ
/// کنسول می‌نشیند تا در توسعه کد تحویل و اطلاع‌رسانی‌ها دیده شوند.
///
/// اتصال به سرویس واقعی (کاوه‌نگار، ICTX، …): یک کلاس دیگر با همین اینترفیس بسازید
/// و در Program.cs به‌جای این ثبت کنید. کلید و خط از تنظیمات «Sms.*» خوانده شود.
/// </summary>
public class LogSmsSender(BargoDbContext db, ILogger<LogSmsSender> log) : ISmsSender
{
    public Task<bool> SendAsync(string mobile, string text, string purpose, CancellationToken ct = default)
    {
        log.LogInformation("[SMS:{Purpose}] {Mobile}: {Text}", purpose, mobile, text);
        db.SmsLogs.Add(new SmsLog { Mobile = mobile, Text = text, Purpose = purpose, Success = true, Response = "log-only" });
        return Task.FromResult(true);
    }
}
