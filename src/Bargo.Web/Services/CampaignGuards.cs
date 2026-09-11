using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Services;

/// <summary>
/// دو نگهبان کمپین که هر دو دربارهٔ پول‌اند: «همین را قبلاً فرستاده‌ای» و
/// «امروز به‌اندازهٔ کافی فرستاده‌ای» (آورده‌شده از کارکور).
/// </summary>
public static class CampaignGuards
{
    // ================================================================ تکرار

    /// <summary>
    /// یکدست‌کردن متن برای مقایسه.
    ///
    /// ⚠️ مقایسهٔ خام کار نمی‌کند. متن کمپین معمولاً کپی‌وپیست می‌شود و سرِ راه
    /// یک فاصلهٔ اضافه، یک «ي» عربی به‌جای «ی»، یا یک نیم‌فاصله عوض می‌شود.
    /// آن‌وقت دو متنِ عملاً یکسان برای برنامه دو چیز متفاوت‌اند و هشدار هرگز
    /// درنمی‌آید — یعنی نگهبانی که فقط ادای نگهبانی درمی‌آورد.
    /// </summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        var t = s.Replace('ي', 'ی').Replace('ك', 'ک')
                 .Replace('‌', ' ')   // نیم‌فاصله
                 .Replace('‏', ' ')   // نشانهٔ راست‌به‌چپ
                 .Replace('\r', ' ').Replace('\n', ' ');

        while (t.Contains("  ", StringComparison.Ordinal))
            t = t.Replace("  ", " ", StringComparison.Ordinal);

        return t.Trim();
    }

    /// <summary>کمپین پیشینی که همین متن را فرستاده.</summary>
    public sealed record Duplicate(int CampaignId, string Title, DateTime? At, int SentCount);

    /// <summary>
    /// آیا همین متن قبلاً رفته؟ فقط کمپین‌هایی که واقعاً چیزی فرستاده‌اند حساب
    /// می‌شوند — پیش‌نویسی که هرگز شروع نشده تکرار نیست.
    /// </summary>
    public static async Task<List<Duplicate>> DuplicatesAsync(
        BargoDbContext db, string? body, int exceptCampaignId, int windowDays = 60)
    {
        var needle = Fold(body);
        if (needle.Length == 0) return [];

        var since = DateTime.UtcNow.AddDays(-windowDays);

        // مقایسهٔ یکدست‌شده در حافظه انجام می‌شود، پس نامزدها را محدود می‌کنیم:
        // فقط کمپین‌های همین بازه که ارسالی داشته‌اند.
        var candidates = await db.SmsCampaigns.AsNoTracking()
            .Where(c => c.SmsCampaignId != exceptCampaignId && c.SentCount > 0 && c.CreatedAt >= since)
            .OrderByDescending(c => c.SmsCampaignId)
            .Select(c => new { c.SmsCampaignId, c.Title, c.Body, c.StartedAt, c.SentCount })
            .Take(300)
            .ToListAsync();

        return [.. candidates
            .Where(c => Fold(c.Body) == needle)
            .Select(c => new Duplicate(c.SmsCampaignId, c.Title, c.StartedAt, c.SentCount))];
    }

    // ================================================================ سقف روز

    /// <summary>
    /// سقف روزانه روی *همهٔ* کمپین‌ها.
    ///
    /// ⚠️ سقف تک‌کمپین این را نمی‌گیرد: پنج کمپین پنج‌هزارتایی پشت هم، هرکدام
    /// زیر سقف خودشان‌اند و روی هم بیست‌وپنج هزار پیامک در یک روز. این سقف
    /// همان جایی است که آن حساب بسته می‌شود.
    ///
    /// ۰ یعنی بی‌سقف — تصمیم صریح مدیر، نه پیش‌فرض.
    /// </summary>
    public const int DailyCapDefault = 5000;

    public sealed record DailyBudget(int Cap, int SentToday)
    {
        public bool Capped => Cap > 0;
        public int Remaining => Capped ? Math.Max(0, Cap - SentToday) : int.MaxValue;
        public bool Exhausted => Capped && Remaining <= 0;
    }

    /// <summary>
    /// ⚠️ «امروز» به وقت تهران است و نه UTC. با UTC، سقف هر شب ساعت ۳:۳۰
    /// بامداد صفر می‌شد — وسط شب — و کمپینی که شب شروع شده بود دو برابر سقف
    /// می‌فرستاد بی‌آنکه کسی بفهمد.
    /// </summary>
    public static async Task<DailyBudget> BudgetAsync(BargoDbContext db, SettingsService settings, CancellationToken ct = default)
    {
        var cap = await settings.GetIntAsync(SettingsService.Keys.SmsCampaignDailyCap, ct);
        var since = Fa.TodayStartUtc;

        var sent = await db.SmsCampaignRecipients
            .CountAsync(r => r.Status == CampaignRecipientStatus.Sent && r.SentAt >= since, ct);

        return new DailyBudget(cap, sent);
    }
}
