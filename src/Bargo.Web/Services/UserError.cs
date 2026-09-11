namespace Bargo.Web.Services;

/// <summary>
/// خطایی که متنش برای نمایش به کاربر نوشته شده («موجودی کافی نیست»، «این بار دیگر
/// پیشنهاد نمی‌پذیرد»). کنترلرها فقط همین نوع را می‌گیرند و در TempData["err"]
/// می‌گذارند؛ هر استثنای دیگری خطای برنامه است و نباید با پیامِ کاربرپسند پنهان شود.
/// </summary>
public sealed class UserError(string message) : Exception(message);

/// <summary>انجام‌دهندهٔ یک اقدام — در رویدادهای سفر و ردّ حسابرسی ثبت می‌شود.</summary>
/// <param name="Kind">driver | shipper | company | admin | system</param>
/// <param name="Id">شناسهٔ حساب (برای شرکت: CompanyUserId)</param>
/// <param name="CompanyId">فقط برای کاربر پنل شرکت</param>
public sealed record Actor(string Kind, int Id, string Name, int? CompanyId = null)
{
    public static readonly Actor System = new("system", 0, "سامانه");
}

public static class Codes
{
    /// <summary>کد خوانا از شناسه: «T-050621-0042».</summary>
    public static string Make(string prefix, int id, DateTime createdUtc) => $"{prefix}-{Fa.Stamp6(createdUtc)}-{id:0000}";
}
