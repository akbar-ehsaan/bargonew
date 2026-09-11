using System.Globalization;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Bargo.Web.Services;

/// <summary>
/// تنظیمات سامانه (جدول Settings) با مقدار پیش‌فرض. هر کلیدی که اینجا تعریف نشده،
/// در پنل مدیر هم دیده نمی‌شود — پس افزودن تنظیم تازه یعنی افزودن یک ردیف به <see cref="Defaults"/>.
/// </summary>
public class SettingsService(BargoDbContext db, IMemoryCache cache)
{
    private const string CacheKey = "bargo:settings";

    public static class Keys
    {
        /// <summary>کمیسیون رانندهٔ مستقل ↔ پلتفرم — الگوی راهداری: ۱۰٪ بیرون از پایانه.</summary>
        public const string CommissionPercentDriver = "Finance.CommissionPercentDriver";
        /// <summary>کمیسیون شرکت حمل‌ونقل ↔ پلتفرم — الگوی راهداری: ۸٪ در پایانه.</summary>
        public const string CommissionPercentCompany = "Finance.CommissionPercentCompany";
        public const string CommissionMinRial = "Finance.CommissionMinRial";
        public const string MinPayoutRial = "Finance.MinPayoutRial";
        public const string VatEnabled = "Finance.VatEnabled";
        public const string VatPercent = "Finance.VatPercent";
        public const string LoadingFeeRial = "Finance.LoadingFeeRial";
        public const string UnloadingFeeRial = "Finance.UnloadingFeeRial";
        public const string WaybillFeeRial = "Finance.WaybillFeeRial";

        public const string DeliveryOtp = "Trip.DeliveryOtp";
        public const string RequireWaybill = "Trip.RequireWaybill";
        public const string OfferExpiryHours = "Trip.OfferExpiryHours";

        public const string ExpiryWarnDays = "Documents.ExpiryWarnDays";
        public const string BlockOnExpired = "Documents.BlockOnExpired";

        public const string TrackIntervalMin = "Tracking.IntervalMin";
        public const string MapTileUrl = "Map.TileUrl";
        public const string NeshanKey = "Map.NeshanKey";

        public const string SmsProvider = "Sms.Provider";
        public const string SmsApiKey = "Sms.ApiKey";
        public const string SmsLine = "Sms.Line";
        public const string SmsUrl = "Sms.Url";
        public const string SmsUsername = "Sms.Username";

        // کمپین پیامکی بانک مخاطبان (الگوی کارکور)
        public const string SmsCampaignEnabled = "Sms.CampaignEnabled";
        public const string SmsCampaignDailyCap = "Sms.CampaignDailyCap";
        public const string SmsCampaignPriceRial = "Sms.CampaignPriceRial";
        /// <summary>پیش‌نویس متن کمپین — عمداً در Defaults نیست تا در صفحهٔ تنظیمات دیده نشود.</summary>
        public const string SmsCampaignDraft = "Sms.CampaignDraft";

        public const string GatewayProvider = "Gateway.Provider";
        public const string GatewayMerchant = "Gateway.MerchantId";
        public const string GatewaySandbox = "Gateway.Sandbox";

        public const string WaybillPrefix = "Waybill.Prefix";
        public const string WaybillIssuer = "Waybill.IssuerName";

        public const string AppLatestVersion = "App.LatestVersion";
        public const string AppMinVersion = "App.MinVersion";
        public const string SupportPhone = "Site.SupportPhone";
    }

    public sealed record Def(string Default, string Label, string Group);

    public static readonly IReadOnlyDictionary<string, Def> Defaults = new Dictionary<string, Def>
    {
        // دو نرخ جدا — مثل نرخ مصوب راهداری (۸٪ پایانه، ۱۰٪ بیرون): سهم پلتفرم از
        // رانندهٔ مستقل و از شرکت حمل‌ونقل می‌تواند متفاوت باشد
        [Keys.CommissionPercentDriver] = new("10", "درصد کمیسیون بارگو از رانندهٔ مستقل", "finance"),
        [Keys.CommissionPercentCompany] = new("8", "درصد کمیسیون بارگو از شرکت حمل‌ونقل", "finance"),
        [Keys.CommissionMinRial] = new("500000", "حداقل کمیسیون هر سفر (ریال)", "finance"),
        [Keys.MinPayoutRial] = new("1000000", "حداقل مبلغ درخواست برداشت (ریال)", "finance"),
        [Keys.VatEnabled] = new("false", "مالیات بر ارزش افزوده اعمال شود", "finance"),
        [Keys.VatPercent] = new("10", "درصد مالیات بر ارزش افزوده", "finance"),
        [Keys.LoadingFeeRial] = new("0", "هزینهٔ بارگیری هر سفر (ریال)", "finance"),
        [Keys.UnloadingFeeRial] = new("0", "هزینهٔ تخلیه هر سفر (ریال)", "finance"),
        [Keys.WaybillFeeRial] = new("0", "هزینهٔ صدور بارنامه هر سفر (ریال)", "finance"),

        [Keys.DeliveryOtp] = new("true", "تحویل فقط با کد یکبارمصرف گیرنده", "waybill"),
        [Keys.RequireWaybill] = new("true", "ثبت بارنامه پیش از شروع سفر الزامی است", "waybill"),
        [Keys.OfferExpiryHours] = new("48", "مهلت اعتبار پیشنهاد (ساعت)", "waybill"),
        [Keys.WaybillPrefix] = new("BG", "پیشوند شماره بارنامه داخلی", "waybill"),
        [Keys.WaybillIssuer] = new("", "نام صادرکنندهٔ بارنامه", "waybill"),

        [Keys.ExpiryWarnDays] = new("30", "هشدار انقضای مدارک از چند روز قبل", "documents"),
        [Keys.BlockOnExpired] = new("true", "مدرک منقضی، پذیرش بار را می‌بندد", "documents"),

        [Keys.TrackIntervalMin] = new("5", "بازهٔ ارسال موقعیت (دقیقه)", "map"),
        [Keys.MapTileUrl] = new("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", "آدرس کاشی نقشه", "map"),
        [Keys.NeshanKey] = new("", "کلید نقشهٔ نشان (اختیاری)", "map"),

        [Keys.SmsProvider] = new("log", "سرویس پیامک (log = فقط ثبت | ictx = ارسال واقعی)", "sms"),
        [Keys.SmsApiKey] = new("", "کلید API پیامک", "sms"),
        [Keys.SmsLine] = new("", "شماره خط ارسال", "sms"),
        [Keys.SmsUrl] = new("https://sms.ictx.ir/api/rest/sms/send", "آدرس وب‌سرویس پیامک", "sms"),
        [Keys.SmsUsername] = new("", "نام کاربری پنل پیامک (اختیاری)", "sms"),
        // پیش‌فرض کمپین عمداً خاموش است: گیرندهٔ این پیامک‌ها خودش چیزی نخواسته؛
        // روشن‌کردنش باید تصمیم آگاهانهٔ یک انسان باشد.
        [Keys.SmsCampaignEnabled] = new("false", "کمپین پیامکی بانک مخاطبان فعال باشد", "sms"),
        [Keys.SmsCampaignDailyCap] = new("5000", "سقف روزانه پیامک کمپین (۰ = بی‌سقف)", "sms"),
        [Keys.SmsCampaignPriceRial] = new("1500", "تعرفه هر بخش پیامک برای برآورد هزینه (ریال)", "sms"),

        [Keys.GatewayProvider] = new("demo", "درگاه پرداخت (demo = آزمایشی | zarinpal = زرین‌پال)", "gateway"),
        [Keys.GatewayMerchant] = new("", "شناسهٔ پذیرنده (Merchant ID)", "gateway"),
        [Keys.GatewaySandbox] = new("true", "حالت آزمایشی درگاه", "gateway"),

        [Keys.AppLatestVersion] = new("0.1.0", "آخرین نسخهٔ اپ", "version"),
        [Keys.AppMinVersion] = new("0.1.0", "حداقل نسخهٔ مجاز اپ", "version"),
        [Keys.SupportPhone] = new("021-00000000", "تلفن پشتیبانی", "version"),
    };

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out Dictionary<string, string>? hit) && hit is not null) return hit;
        var rows = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        cache.Set(CacheKey, rows, TimeSpan.FromMinutes(5));
        return rows;
    }

    public async Task<string> GetAsync(string key, CancellationToken ct = default)
    {
        var rows = await LoadAsync(ct);
        if (rows.TryGetValue(key, out var v)) return v;
        return Defaults.TryGetValue(key, out var d) ? d.Default : "";
    }

    public async Task<decimal> GetDecimalAsync(string key, CancellationToken ct = default) =>
        decimal.TryParse(Fa.Latin(await GetAsync(key, ct)), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public async Task<long> GetLongAsync(string key, CancellationToken ct = default) =>
        long.TryParse(Fa.Latin(await GetAsync(key, ct)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public async Task<int> GetIntAsync(string key, CancellationToken ct = default) =>
        int.TryParse(Fa.Latin(await GetAsync(key, ct)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public async Task<bool> GetBoolAsync(string key, CancellationToken ct = default) =>
        (await GetAsync(key, ct)).Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(new Setting { Key = key, Value = value ?? "" });
        else { row.Value = value ?? ""; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
    }

    /// <summary>همهٔ تنظیمات یک گروه با مقدار فعلی — برای صفحه‌های «تنظیمات سیستم».</summary>
    public async Task<List<(string Key, string Label, string Value)>> GroupAsync(string group, CancellationToken ct = default)
    {
        var rows = await LoadAsync(ct);
        return Defaults.Where(d => d.Value.Group == group)
            .Select(d => (d.Key, d.Value.Label, rows.TryGetValue(d.Key, out var v) ? v : d.Value.Default))
            .ToList();
    }
}
