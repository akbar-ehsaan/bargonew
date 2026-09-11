using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Services;

/// <summary>
/// موتور ارسال کمپین — صف <see cref="SmsCampaignRecipient"/> را دسته‌دسته خالی
/// می‌کند (آورده‌شده از کارکور، روی ISmsSender بارگو).
///
/// چرا کار پس‌زمینه و نه حلقه در کنترلر: ارسال به پنج هزار شماره با فاصلهٔ لازم
/// بین ارسال‌ها حدود نیم‌ساعت طول می‌کشد. در درخواست HTTP یعنی تایم‌اوت، و بستن
/// مرورگر یعنی کار نصفه‌ای که کسی نمی‌داند تا کجا رفته.
///
/// هر شماره پس از ارسال بلافاصله ذخیره می‌شود؛ اگر برنامه وسط کار ری‌استارت شود،
/// نوبت بعد از همان‌جا ادامه می‌دهد و هیچ‌کس دو بار پیام نمی‌گیرد.
/// </summary>
public class CampaignSenderJob(IServiceScopeFactory scopes, ILogger<CampaignSenderJob> logger)
    : BackgroundService
{
    /// <summary>چند شماره در هر بیدارشدن — کوچک، تا توقف زود اثر کند.</summary>
    public const int BatchSize = 40;

    /// <summary>فاصلهٔ بین دو ارسال، تا درگاه ترافیک را رد نکند.</summary>
    public const int SendDelayMs = 350;

    /// <summary>مقصود ثبت‌شده در SmsLogs برای پیامک‌های کمپین.</summary>
    public const string Purpose = "campaign";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var moved = 0;
            try { moved = await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "اجرای موتور کمپین ناموفق بود.");
            }

            // وقتی کار هست زود برگرد تا صفحهٔ گزارش زنده بماند؛ وقتی نیست
            // بیهوده پایگاه‌داده را نپرس.
            var wait = moved > 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30);
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// یک دستهٔ کمپینِ در حال اجرا را می‌فرستد و تعداد رسیدگی‌شده را برمی‌گرداند.
    /// جدا از <see cref="ExecuteAsync"/> است تا بشود جدا هم آزمود و صدایش زد.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BargoDbContext>();
        var sms = scope.ServiceProvider.GetRequiredService<ISmsSender>();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var links = scope.ServiceProvider.GetRequiredService<OptOutLinks>();

        // قدیمی‌ترین کمپین در حال اجرا: کمپین‌ها پشت سر هم می‌روند نه موازی،
        // وگرنه دو کمپین با هم درگاه را اشباع می‌کنند.
        var campaign = await db.SmsCampaigns
            .Where(c => c.Status == CampaignStatus.Running)
            .OrderBy(c => c.StartedAt ?? c.CreatedAt).ThenBy(c => c.SmsCampaignId)
            .FirstOrDefaultAsync(ct);

        if (campaign == null) return 0;

        // کلید کمپین باید باز باشد. اینجا کمپین را «متوقف» نمی‌کنیم چون بستن
        // موقت کلید نباید حالت کمپین را عوض کند؛ صفحهٔ گزارش خودش هشدار می‌دهد
        // که چرا هیچ‌چیز جلو نمی‌رود.
        if (!await settings.GetBoolAsync(SettingsService.Keys.SmsCampaignEnabled, ct)) return 0;

        // ⚠️ سقف روزانه اینجا اعمال می‌شود و نه فقط روی صفحه.
        //
        // سقف تک‌کمپین جلوی «پنج کمپین پنج‌هزارتایی در یک روز» را نمی‌گیرد؛
        // هرکدام زیر سقف خودشان‌اند و روی هم بیست‌وپنج هزار پیامک. نگهبانی که
        // فقط در صفحهٔ ساخت باشد، همان روزی غایب است که لازم می‌شود.
        //
        // کمپین «متوقف» نمی‌شود: فردا خودش از همان‌جا ادامه می‌دهد. متوقف‌کردنش
        // یعنی مدیر باید یادش بماند دوباره شروعش کند.
        var budget = await CampaignGuards.BudgetAsync(db, settings, ct);
        if (budget.Exhausted)
        {
            logger.LogInformation(
                "سقف روزانهٔ پیامک ({Cap}) پر شده؛ کمپین {Id} فردا ادامه می‌یابد.",
                budget.Cap, campaign.SmsCampaignId);
            return 0;
        }

        var batch = await db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaign.SmsCampaignId && r.Status == CampaignRecipientStatus.Pending)
            .OrderBy(r => r.SmsCampaignRecipientId)
            .Take(Math.Min(BatchSize, budget.Remaining))
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            campaign.Status = CampaignStatus.Done;
            campaign.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("کمپین {Id} تمام شد: {Sent} ارسال، {Failed} ناموفق.",
                campaign.SmsCampaignId, campaign.SentCount, campaign.FailedCount);
            return 0;
        }

        // شماره‌هایی که از وقتِ ساخت صف تا حالا «دیگر پیام نده» زده‌اند.
        var mobiles = batch.Select(r => r.Mobile).ToList();
        var optedOut = await db.MarketingContacts
            .Where(c => c.OptedOut && mobiles.Contains(c.Mobile))
            .Select(c => c.Mobile)
            .ToListAsync(ct);

        var body = campaign.Body;
        var handled = 0;

        // جای‌نشان‌ها («{نام} گرامی…») برای هر گیرنده جدا جایگزین می‌شوند و
        // نام/شهر/استان از خود مخاطب می‌آید — یک پرس‌وجو برای کل دسته.
        var hasTokens = CampaignMessage.HasTokens(body);
        var batchIds = batch.Select(r => r.ContactId).ToList();
        var batchContacts = hasTokens
            ? await db.MarketingContacts.AsNoTracking()
                .Where(c => batchIds.Contains(c.MarketingContactId))
                .ToDictionaryAsync(c => c.MarketingContactId, ct)
            : new Dictionary<int, MarketingContact>();

        foreach (var r in batch)
        {
            if (ct.IsCancellationRequested) break;

            // حالت کمپین ممکن است وسط همین دسته عوض شده باشد (توقف/لغو از
            // پنل). بدون این بررسی، دکمهٔ «توقف» تا آخر دسته اثر نمی‌کرد.
            var state = await db.SmsCampaigns.Where(c => c.SmsCampaignId == campaign.SmsCampaignId)
                .Select(c => c.Status).FirstAsync(ct);
            if (state != CampaignStatus.Running) break;

            if (optedOut.Contains(r.Mobile))
            {
                r.Status = CampaignRecipientStatus.Skipped;
                r.Error = "لغو اشتراک";
                await db.SaveChangesAsync(ct);
                handled++;
                continue;
            }

            // ⚠️ راه خروج به هر پیامک اضافه می‌شود و اختیاری نیست. اگر نوشتن
            // این خط به عهدهٔ متن مدیر بود، اولین کمپینی که یادش می‌رفت
            // پیامکِ بی‌راهِ‌خروج می‌شد.
            var rendered = hasTokens && batchContacts.TryGetValue(r.ContactId, out var who)
                ? CampaignMessage.Render(body, who.Name, who.City, who.Province)
                : CampaignMessage.Render(body, null, null, null);
            var withOptOut = rendered + links.Suffix(r.ContactId, SmsCampaign.Host);

            bool ok;
            try { ok = await sms.SendAsync(r.Mobile, withOptOut, Purpose, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ok = false;
                logger.LogWarning(ex, "ارسال کمپین {Id} به {Mobile} خطا داد.", campaign.SmsCampaignId, r.Mobile);
            }

            if (ok)
            {
                r.Status = CampaignRecipientStatus.Sent;
                r.SentAt = DateTime.UtcNow;
                r.Error = null;
                campaign.SentCount++;

                var contact = await db.MarketingContacts.FirstOrDefaultAsync(c => c.MarketingContactId == r.ContactId, ct);
                if (contact != null)
                {
                    contact.LastSentAt = DateTime.UtcNow;
                    contact.SentCount++;
                }
            }
            else
            {
                r.Status = CampaignRecipientStatus.Failed;
                r.Error = "درگاه پیامک پیام را نپذیرفت";
                campaign.FailedCount++;
            }

            // ذخیره پس از هر شماره: اگر برنامه همین‌جا بمیرد، آنچه رفته رفته
            // حساب شده و دوباره فرستاده نمی‌شود. ردیف SmsLog که ISmsSender در
            // صف گذاشته هم با همین ذخیره می‌نشیند.
            await db.SaveChangesAsync(ct);
            handled++;

            try { await Task.Delay(SendDelayMs, ct); } catch (OperationCanceledException) { break; }
        }

        return handled;
    }
}
