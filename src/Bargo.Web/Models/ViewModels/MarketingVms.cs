using Bargo.Web.Models.Entities;
using Bargo.Web.Services;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  مدل‌های نمایش کمپین پیامکی و بانک مخاطبان (الگوی کارکور، به سبک VMهای بارگو)
// ---------------------------------------------------------------------------

/// <summary>یک گروه (صنف) و تعداد مخاطبانش.</summary>
public sealed record ContactCategoryCount(string? Name, int Count);

/// <summary>«به کجا رفت» — استان/شهر با شمار، برای فهرست و پروندهٔ کمپین.</summary>
public sealed record CampaignPlaceCount(string? Province, string? City, int Count);

/// <summary>یک ردیف گیرنده در جریان ساخت کمپین.</summary>
public sealed record CampaignTarget(int Id, string Mobile, string? Name, string? Category, string? Province, string? City);

/// <summary>تاریخچهٔ یک گروه: چند کمپین گرفته، روی هم چند ارسال، آخری کِی.</summary>
public sealed record CampaignGroupHistory(string? Category, string? Province, string? City,
    int Campaigns, int TotalSent, DateTime? LastAt);

/// <summary>یک ردیف گیرنده در پروندهٔ کمپین، با وضعیتش.</summary>
public sealed record CampaignRecipientRow(string Mobile, string Status, string? Error,
    string? Name, string? Province, string? City);

/// <summary>داشبورد پیامک — تصویر کامل بانک پیش از هر کاری.</summary>
public sealed class MarketingDashboardVm
{
    public int Total { get; init; }
    public int OptedOut { get; init; }
    public int Touched { get; init; }
    public int Untouched { get; init; }
    public int Sendable { get; init; }
    public int Drivers { get; init; }
    public int Shippers { get; init; }
    public int Companies { get; init; }
    public List<ContactCategoryCount> Categories { get; init; } = [];
    public required CampaignGuards.DailyBudget Budget { get; init; }
    public long PriceRial { get; init; }
    /// <summary>سرویس پیامک واقعی است (ictx) یا فقط لاگ می‌شود.</summary>
    public bool SmsReal { get; init; }
    public bool CampaignOn { get; init; }
    public List<SmsCampaign> Campaigns { get; init; } = [];
}

public sealed class MarketingContactsVm
{
    public required PageVm<MarketingContact> Page { get; init; }
    public List<string> Categories { get; init; } = [];
    public List<string> Provinces { get; init; } = [];
    public string? Q { get; init; }
    public string? Cat { get; init; }
    public string? Province { get; init; }
    public string? State { get; init; }
    public int All { get; init; }
    public int OptedOut { get; init; }
}

public sealed class MarketingCategoriesVm
{
    public List<ContactCategoryCount> Rows { get; init; } = [];
    public int Total { get; init; }
}

/// <summary>فهرست کمپین‌ها.</summary>
public sealed class CampaignListVm
{
    public List<SmsCampaign> Rows { get; init; } = [];
    public bool SmsReal { get; init; }
    public bool CampaignOn { get; init; }
    public int OptOutCount { get; init; }
    /// <summary>اجراکننده — شناسه به نام؛ «مدیر ۳» به هیچ‌کس چیزی نمی‌گوید.</summary>
    public Dictionary<int, string> By { get; init; } = [];
    public Dictionary<int, List<CampaignPlaceCount>> Where { get; init; } = [];
}

/// <summary>فیلتر و وضعیت مشترک گام‌های ساخت کمپین.</summary>
public sealed class CampaignFilterVm
{
    public string? Cat { get; init; }
    public string? Province { get; init; }
    public string? City { get; init; }
    public bool ExcludeSent { get; init; } = true;
    public List<string> Categories { get; init; } = [];
    public List<string> Provinces { get; init; } = [];
    public List<string> Cities { get; init; } = [];
    public required CampaignGuards.DailyBudget Budget { get; init; }
    public int Cap { get; init; } = SmsCampaign.DefaultCap;
    public long PriceRial { get; init; }
    public bool SmsReal { get; init; }
    public bool CampaignOn { get; init; }
}

/// <summary>گام ۱ — فیلتر و «چند نفر واقعاً پیامک می‌گیرند».</summary>
public sealed class CampaignSmsVm
{
    public required CampaignFilterVm F { get; init; }
    public int Count { get; init; }
    public int ScopeTotal { get; init; }
    public int ScopeOptOut { get; init; }
    public int ScopeAlreadySent { get; init; }
    public int PriorRounds { get; init; }
    public List<CampaignGroupHistory> SentGroups { get; init; } = [];
}

/// <summary>گام ۲ — نوشتن متن.</summary>
public sealed class CampaignComposeVm
{
    public required CampaignFilterVm F { get; init; }
    public int Count { get; init; }
    public string Text { get; init; } = "";
    public List<CampaignTemplates.Template> Templates { get; init; } = [];
    public int Reserve { get; init; }
    public CampaignTarget? Sample { get; init; }
}

/// <summary>گام ۳ — فهرست دقیق گیرندگان و تأیید.</summary>
public sealed class CampaignConfirmVm
{
    public required CampaignFilterVm F { get; init; }
    public string Text { get; init; } = "";
    public int Count { get; init; }
    public int WillSend { get; init; }
    public int Parts { get; init; }
    public int MaxParts { get; init; }
    public int More { get; init; }
    public long TotalParts { get; init; }
    public bool Unicode { get; init; }
    public bool HasTokens { get; init; }
    public List<CampaignTarget> Recipients { get; init; } = [];
    public List<CampaignGuards.Duplicate> Duplicates { get; init; } = [];
}

/// <summary>فرم ساخت کمپین پیش‌نویس (مسیر جدا از جریان چهارگامی).</summary>
public sealed class CampaignCreateVm
{
    public List<string> Categories { get; init; } = [];
    public List<string> Provinces { get; init; } = [];
    public List<string> Cities { get; init; } = [];
    public List<CampaignTemplates.Template> Templates { get; init; } = [];
    public int All { get; init; }
    public long PriceRial { get; init; }
    public int Reserve { get; init; }
    public string? Cat { get; init; }
}

/// <summary>پروندهٔ یک کمپین.</summary>
public sealed class CampaignDetailsVm
{
    public required SmsCampaign C { get; init; }
    public int Pending { get; init; }
    public int Skipped { get; init; }
    public List<SmsCampaignRecipient> Failures { get; init; } = [];
    public bool SmsReal { get; init; }
    public bool CampaignOn { get; init; }
    public long PriceRial { get; init; }
    public required CampaignGuards.DailyBudget Budget { get; init; }
    public List<CampaignGuards.Duplicate> Duplicates { get; init; } = [];
    public List<CampaignPlaceCount> Places { get; init; } = [];
    public string? By { get; init; }
    public List<CampaignRecipientRow> Recipients { get; init; } = [];
}
