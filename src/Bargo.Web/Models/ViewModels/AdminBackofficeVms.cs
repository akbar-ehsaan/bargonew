using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  مدل‌های نمایشِ بخشِ «پشت‌صحنه»ی پنل مدیر: مالی، تعرفه، شکایت، پشتیبانی، پیام،
//  گزارش، محتوا، مناطق و تنظیمات. همه در یک فایل تا فهرست پوشهٔ ViewModels با
//  ده‌ها فایل کوچک پر نشود؛ هر بخش با یک سربرگ جدا شده است.
// ---------------------------------------------------------------------------

/// <summary>
/// نام و موبایلِ صاحبان (راننده/صاحب بار/شرکت) برای ردیف‌های همان صفحه. جدول‌های
/// مشترک فقط (OwnerKind, OwnerId) دارند؛ به‌جای JOIN چهارگانه، نام‌ها یک‌بار برای
/// ردیف‌های صفحهٔ جاری خوانده می‌شوند.
/// </summary>
public sealed class NameMap
{
    private readonly Dictionary<(string Kind, int Id), (string Name, string Mobile)> _m = new();

    public void Set(string kind, int id, string name, string mobile) => _m[(kind, id)] = (name, mobile);

    public bool Has(string? kind, int? id) => kind is not null && id is not null && _m.ContainsKey((kind, id.Value));

    public string Name(string? kind, int? id)
    {
        if (kind is null || id is null) return "—";
        if (kind == OwnerKind.Platform) return "بارگو";
        return _m.TryGetValue((kind, id.Value), out var v) && v.Name.Length > 0
            ? v.Name
            : $"{OwnerKind.Label(kind)} #{Fa.N(id.Value)}";
    }

    public string Mobile(string? kind, int? id) =>
        kind is not null && id is not null && _m.TryGetValue((kind, id.Value), out var v) ? v.Mobile : "";
}

public static class BackofficeFormat
{
    /// <summary>«۲ ساعت و ۱۵ دقیقه» / «۳ روز و ۴ ساعت».</summary>
    public static string Duration(double? minutes)
    {
        if (minutes is null) return "—";
        var m = (long)Math.Round(minutes.Value);
        if (m < 1) return "کمتر از یک دقیقه";
        if (m < 60) return $"{Fa.N(m)} دقیقه";
        if (m < 1440)
        {
            var h = m / 60;
            var r = m % 60;
            return r == 0 ? $"{Fa.N(h)} ساعت" : $"{Fa.N(h)} ساعت و {Fa.N(r)} دقیقه";
        }
        var d = m / 1440;
        var hh = m % 1440 / 60;
        return hh == 0 ? $"{Fa.N(d)} روز" : $"{Fa.N(d)} روز و {Fa.N(hh)} ساعت";
    }

    /// <summary>درصدِ پهنای نوار نمودار؛ مقدار مثبتِ کوچک دست‌کم ۱٪ تا دیده شود.</summary>
    public static int Bar(long value, long max) =>
        max <= 0 || value <= 0 ? 0 : (int)Math.Clamp(Math.Round(value * 100.0 / max), 1, 100);

    public static int Bar(double value, double max) =>
        max <= 0 || value <= 0 ? 0 : (int)Math.Clamp(Math.Round(value * 100.0 / max), 1, 100);

    /// <summary>فقط چهار نویسهٔ آخر: «••••۱۲۳۴». کلید کوتاه‌تر هیچ بخشی نشان داده نمی‌شود.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "" : "••••" + (value.Length <= 4 ? "" : value[^4..]);

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>JSON جزئیات حسابرسی، تورفته و با فارسیِ خوانا (نه \u06XX).</summary>
    public static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Pretty);
        }
        catch (JsonException) { return json; }
    }

    /// <summary>تعداد بخش پیامک: فارسی ۷۰ نویسه، لاتینِ خالص ۱۶۰.</summary>
    public static int SmsSegments(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var per = text.All(c => c <= 0x7F) ? 160 : 70;
        return (int)Math.Ceiling(text.Length / (double)per);
    }

    public static string AudienceLabel(string a) => a switch
    {
        "drivers" or OwnerKind.Driver => "رانندگان",
        "shippers" or OwnerKind.Shipper => "صاحبان بار",
        "companies" or OwnerKind.Company => "شرکت‌ها",
        _ => "همهٔ کاربران"
    };
}

public static class AdminPermissionLabels
{
    public static readonly (AdminPermission Flag, string Label)[] All =
    [
        (AdminPermission.Users, "کاربران"),
        (AdminPermission.Verification, "احراز مدارک"),
        (AdminPermission.Operations, "عملیات"),
        (AdminPermission.Finance, "مالی"),
        (AdminPermission.Support, "پشتیبانی"),
        (AdminPermission.Reports, "گزارش‌ها"),
        (AdminPermission.Content, "محتوا"),
        (AdminPermission.Settings, "تنظیمات"),
    ];
}

// =====================================================================
//  مالی
// =====================================================================

public sealed class FinanceTxnListVm
{
    public required PageVm<WalletTransaction> Page { get; init; }
    public required NameMap Names { get; init; }
    public Dictionary<int, string> TripCodes { get; init; } = [];
    public long SumIn { get; init; }
    public long SumOut { get; init; }
    public string? Owner { get; init; }
    public string? Kind { get; init; }
    public string? Trip { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

public sealed class CommissionRowVm
{
    public long Id { get; init; }
    public long Amount { get; init; }
    public int? TripId { get; init; }
    public string? TripCode { get; init; }
    public long? Fare { get; init; }
    public decimal? Percent { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class CommissionVm
{
    public required PageVm<CommissionRowVm> Page { get; init; }
    public long TodaySum { get; init; }
    public long MonthSum { get; init; }
    public int MonthCount { get; init; }
    public long AllSum { get; init; }
    public long PlatformBalance { get; init; }
}

public sealed class WalletRowVm
{
    public string Kind { get; init; } = "";
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Mobile { get; init; } = "";
    public long Balance { get; init; }
    public string Status { get; init; } = "";
}

public sealed class WalletsVm
{
    public required PageVm<WalletRowVm> Page { get; init; }
    public string Tab { get; init; } = "drivers";
    public string? Q { get; init; }
    public long DriversTotal { get; init; }
    public long ShippersTotal { get; init; }
    public long CompaniesTotal { get; init; }
    public long PlatformBalance { get; init; }
    public WalletRowVm? Adjust { get; init; }
}

public sealed class PayoutsVm
{
    public required PageVm<PayoutRequest> Page { get; init; }
    public string? Owner { get; init; }
    public string Status { get; init; } = "open";
    public Dictionary<string, int> Counts { get; init; } = [];
    public Dictionary<(string, int), long> Balances { get; init; } = [];
    public Dictionary<int, string> Reviewers { get; init; } = [];
    public long OpenSum { get; init; }
}

public sealed class FailedPaymentsVm
{
    public required PageVm<Payment> Page { get; init; }
    public required NameMap Names { get; init; }
    public Dictionary<int, string> TripCodes { get; init; } = [];
    public string? Purpose { get; init; }
    public int MonthCount { get; init; }
    public long MonthSum { get; init; }
    public int AllCount { get; init; }
}

public sealed class RefundsVm
{
    public required PageVm<Refund> Page { get; init; }
    public required NameMap Names { get; init; }
    public Dictionary<int, string> TripCodes { get; init; } = [];
    public Dictionary<int, string> Reviewers { get; init; } = [];
    public string Status { get; init; } = "pending";
    public Dictionary<string, int> Counts { get; init; } = [];
    public long PendingSum { get; init; }
}

public sealed class RevenueRowVm
{
    public string Label { get; init; } = "";
    public long Fare { get; init; }
    public long Commission { get; init; }
    public int Trips { get; init; }
    public double AvgPercent => Fare == 0 ? 0 : Commission * 100.0 / Fare;
}

public sealed class RevenueVm
{
    public List<RevenueRowVm> Rows { get; init; } = [];
    public long MaxFare => Rows.Count == 0 ? 0 : Rows.Max(r => r.Fare);
    public long MaxCommission => Rows.Count == 0 ? 0 : Rows.Max(r => r.Commission);
    public long TotalFare => Rows.Sum(r => r.Fare);
    public long TotalCommission => Rows.Sum(r => r.Commission);
    public int TotalTrips => Rows.Sum(r => r.Trips);
}

// =====================================================================
//  تعرفه و کمیسیون
// =====================================================================

public sealed record LockedTripVm(int TripId, string Code, long Fare, decimal Percent, long Commission, DateTime CreatedAt);

public sealed class PricingVm
{
    /// <summary>کمیسیون رانندهٔ مستقل ↔ پلتفرم.</summary>
    public decimal DriverPercent { get; init; }
    /// <summary>کمیسیون شرکت حمل‌ونقل ↔ پلتفرم.</summary>
    public decimal CompanyPercent { get; init; }
    public long CommissionMinRial { get; init; }
    public decimal VatPercent { get; init; }
    public List<LockedTripVm> Recent { get; init; } = [];
    /// <summary>نمونهٔ محاسبه برای کرایهٔ ۱۰ میلیون تومانی با نرخ فعلی.</summary>
    public long SampleFare => 100_000_000;
    public long SampleCommissionDriver => Math.Min(SampleFare, Math.Max(CommissionMinRial, (long)Math.Round(SampleFare * DriverPercent / 100m)));
    public long SampleCommissionCompany => Math.Min(SampleFare, Math.Max(CommissionMinRial, (long)Math.Round(SampleFare * CompanyPercent / 100m)));
    public long SampleTax => (long)Math.Round(SampleCommissionDriver * VatPercent / 100m);
}

public sealed class TariffsVm
{
    public List<Tariff> Items { get; init; } = [];
    public Tariff? Edit { get; init; }
    public IReadOnlySet<string> SystemKeys { get; init; } = new HashSet<string>();
}

public sealed class MembershipVm
{
    public Tariff? Fee { get; init; }
    public required PageVm<Subscription> Page { get; init; }
    public required NameMap Names { get; init; }
    public string Tab { get; init; } = "active";
    public int ActiveCount { get; init; }
    public int ExpiredCount { get; init; }
    public long MonthRevenue { get; init; }
}

public sealed class PlansVm
{
    public List<SubscriptionPlan> Items { get; init; } = [];
    public Dictionary<int, int> Subscribers { get; init; } = [];
    public SubscriptionPlan? Edit { get; init; }
}

public sealed class DiscountsVm
{
    public required PageVm<DiscountCode> Page { get; init; }
    public DiscountCode? Edit { get; init; }
    public int ActiveCount { get; init; }
}

// =====================================================================
//  شکایات و تخلفات
// =====================================================================

public sealed class ComplaintsVm
{
    public required PageVm<Complaint> Page { get; init; }
    public required NameMap Names { get; init; }
    public Dictionary<int, string> TripCodes { get; init; } = [];
    public string? From { get; init; }
    public string? Kind { get; init; }
    public string Status { get; init; } = "open";
    public Dictionary<string, int> Counts { get; init; } = [];
}

public sealed class TripSummaryVm
{
    public int TripId { get; init; }
    public string Code { get; init; } = "";
    public string Status { get; init; } = "";
    public string? From { get; init; }
    public string? To { get; init; }
    public string CargoTitle { get; init; } = "";
    public decimal WeightTon { get; init; }
    public long Fare { get; init; }
    public long Commission { get; init; }
    public string CarrierKind { get; init; } = "";
    public string? DriverName { get; init; }
    public string? CompanyName { get; init; }
    public string? ShipperName { get; init; }
    public bool IsPaid { get; init; }
    public bool IsProblem { get; init; }
    public string? ProblemNote { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LoadedAt { get; init; }
    public DateTime? DeliveredAt { get; init; }
    public DateTime? SettledAt { get; init; }
    public DateTime? CancelledAt { get; init; }
}

public sealed class ComplaintDetailVm
{
    public required Complaint C { get; init; }
    public required NameMap Names { get; init; }
    public TripSummaryVm? Trip { get; init; }
    public List<Complaint> Related { get; init; } = [];
    public int AgainstViolations { get; init; }
    public string? ResolvedBy { get; init; }
}

public sealed class ViolationRowVm
{
    public int Id { get; init; }
    public int? DriverId { get; init; }
    public string? DriverName { get; init; }
    public string? DriverMobile { get; init; }
    public string? CompanyName { get; init; }
    public int? TripId { get; init; }
    public string? TripCode { get; init; }
    public int? ComplaintId { get; init; }
    public string? ComplaintCode { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string Penalty { get; init; } = "";
    public long? FineAmount { get; init; }
    public DateTime? SuspendedUntil { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class ViolationsVm
{
    public required PageVm<ViolationRowVm> Page { get; init; }
    public string? Penalty { get; init; }
    public int Total { get; init; }
    public int Month { get; init; }
    public Dictionary<string, int> ByPenalty { get; init; } = [];
}

public static class PenaltyLabel
{
    public static string Of(string p) => p switch { "fine" => "جریمه", "suspension" => "تعلیق", _ => "اخطار" };
    public static string Tone(string p) => p switch { "fine" => "wait", "suspension" => "no", _ => "mut" };
}

// =====================================================================
//  پشتیبانی
// =====================================================================

public sealed class TicketRowVm
{
    public int Id { get; init; }
    public string Subject { get; init; } = "";
    public string Category { get; init; } = "";
    public string Priority { get; init; } = "";
    public string Status { get; init; } = "";
    public string OwnerKind { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public int? AssignedAdminId { get; init; }
    public string? AssignedName { get; init; }
    public int? TripId { get; init; }
    public int Messages { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class SupportQueueVm
{
    public required PageVm<TicketRowVm> Page { get; init; }
    public string Status { get; init; } = "open";
    public string Sort { get; init; } = "";
    public string? Category { get; init; }
    public bool Mine { get; init; }
    public Dictionary<string, int> Counts { get; init; } = [];
    public int UrgentOpen { get; init; }
    public int UnassignedOpen { get; init; }
    public int MineOpen { get; init; }
    public int MeId { get; init; }
}

public sealed class TicketThreadVm
{
    public required Ticket T { get; init; }
    public string OwnerMobile { get; init; } = "";
    public string? AssignedName { get; init; }
    public string? TripCode { get; init; }
    public bool AssignedToMe { get; init; }
    public List<Ticket> OtherTickets { get; init; } = [];
}

public sealed class OperatorRowVm
{
    public int AdminId { get; init; }
    public string Name { get; init; } = "";
    public string Mobile { get; init; } = "";
    public bool IsSuper { get; init; }
    public bool IsActive { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public int OpenAssigned { get; init; }
    public int Answered7d { get; init; }
}

public sealed class OperatorsVm
{
    public List<OperatorRowVm> Rows { get; init; } = [];
    public bool CanAdd { get; init; }
    public int UnassignedOpen { get; init; }
}

public sealed class HistoryRowVm
{
    public int Id { get; init; }
    public string Subject { get; init; } = "";
    public string Category { get; init; } = "";
    public string Status { get; init; } = "";
    public string OwnerKind { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public string? AssignedName { get; init; }
    public int Replies { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? FirstReplyAt { get; init; }
    public double? FirstReplyMinutes => FirstReplyAt is null ? null : (FirstReplyAt.Value - CreatedAt).TotalMinutes;
}

public sealed class SupportHistoryVm
{
    public required PageVm<HistoryRowVm> Page { get; init; }
    public string? Q { get; init; }
    public double? AvgFirstReplyMinutes30d { get; init; }
    public int Tickets30d { get; init; }
    public int Unanswered30d { get; init; }
}

// =====================================================================
//  پیام‌ها و اعلان‌ها
// =====================================================================

public sealed class BroadcastRowVm
{
    public int Id { get; init; }
    public string Audience { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public int SentCount { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class BroadcastVm
{
    public string Audience { get; init; } = "all";
    public Dictionary<string, int> Reach { get; init; } = [];
    public required PageVm<BroadcastRowVm> History { get; init; }
}

public sealed class SmsBroadcastVm
{
    public string Audience { get; init; } = "all";
    public string Text { get; init; } = "";
    public bool Confirming { get; init; }
    public int Recipients { get; init; }
    public Dictionary<string, int> Reach { get; init; } = [];
    public List<SmsLog> Logs { get; init; } = [];
    public List<BroadcastRowVm> History { get; init; } = [];
}

// =====================================================================
//  گزارش‌ها
// =====================================================================

public sealed class ReportRangeVm
{
    public DateTime FromUtc { get; init; }
    /// <summary>ابتدای روزِ پس از آخرین روز بازه (انحصاری).</summary>
    public DateTime ToUtc { get; init; }
    public bool Monthly { get; init; }
    public string Action { get; init; } = "";
    public int Days => (int)Math.Round((ToUtc - FromUtc).TotalDays);
}

public sealed record SeriesVm(string Title, long[] Values, bool Money = false);

public sealed class TimeSeriesVm
{
    public List<string> Labels { get; init; } = [];
    public List<SeriesVm> Series { get; init; } = [];
    public string FirstColumn { get; init; } = "دوره";
}

public sealed class ReportFinanceVm
{
    public required ReportRangeVm Range { get; init; }
    public required TimeSeriesVm Series { get; init; }
    public long Fares { get; init; }
    public long Commission { get; init; }
    public long Payouts { get; init; }
    public long Refunds { get; init; }
    public long Charges { get; init; }
    public int SettledTrips { get; init; }
}

public sealed class ReportUsersVm
{
    public required ReportRangeVm Range { get; init; }
    public required TimeSeriesVm Series { get; init; }
    public Dictionary<string, int> DriverStatus { get; init; } = [];
    public Dictionary<string, int> CompanyStatus { get; init; } = [];
    public Dictionary<string, int> ShipperVerify { get; init; } = [];
    public int NewDrivers { get; init; }
    public int NewShippers { get; init; }
    public int NewCompanies { get; init; }
}

public sealed record CargoRowVm(string Cargo, int Count, int Booked, decimal WeightTon);

public sealed class ReportLoadsVm
{
    public required ReportRangeVm Range { get; init; }
    public required TimeSeriesVm Series { get; init; }
    public int Created { get; init; }
    public int Booked { get; init; }
    public int Cancelled { get; init; }
    public int Expired { get; init; }
    public List<CargoRowVm> ByCargo { get; init; } = [];
}

public sealed class ReportTripsVm
{
    public required ReportRangeVm Range { get; init; }
    public required TimeSeriesVm Series { get; init; }
    public int Created { get; init; }
    public int Delivered { get; init; }
    public int Cancelled { get; init; }
    public double? AvgDeliveryMinutes { get; init; }
    public double? AvgKm { get; init; }
    public long AvgFare { get; init; }
    public Dictionary<string, int> ByStatus { get; init; } = [];
}

public sealed class RankRowVm
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public int Trips { get; init; }
    public long Fare { get; init; }
    public long Income { get; init; }
    public long Commission { get; init; }
    public double Rating { get; init; }
    public int RatingCount { get; init; }
    public int Extra { get; init; }
}

public sealed class ReportRankVm
{
    public required ReportRangeVm Range { get; init; }
    public List<RankRowVm> Rows { get; init; } = [];
    public string By { get; init; } = "trips";
    public int Active { get; init; }
    public double? AvgRating { get; init; }
    public double? RangeRatingAvg { get; init; }
    public int RangeRatingCount { get; init; }
}

public sealed record RegionRowVm(string Name, int Count, decimal WeightTon);

public sealed class ReportRegionsVm
{
    public required ReportRangeVm Range { get; init; }
    public List<RegionRowVm> Origins { get; init; } = [];
    public List<RegionRowVm> Dests { get; init; } = [];
    public List<RegionRowVm> TopCities { get; init; } = [];
    public int Total { get; init; }
}

public sealed class RouteRowVm
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public int Trips { get; init; }
    public long AvgFare { get; init; }
    public double? AvgKm { get; init; }
    public double? LaneKm { get; init; }
    public long? LaneRatePerTon { get; init; }
}

public sealed class ReportRoutesVm
{
    public required ReportRangeVm Range { get; init; }
    public List<RouteRowVm> Rows { get; init; } = [];
}

// =====================================================================
//  محتوا و مناطق
// =====================================================================

public sealed class ContentVm
{
    public string Kind { get; init; } = ContentKind.Banner;
    public List<ContentItem> Items { get; init; } = [];
    public Dictionary<string, int> Counts { get; init; } = [];
    public ContentItem? Edit { get; init; }
    public bool ShowForm { get; init; }
}

public sealed class ProvinceRowVm
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Cities { get; init; }
    public int Active { get; init; }
    public int NoCoords { get; init; }
    public int Loads { get; init; }
}

public sealed class CityRowVm
{
    public int CityId { get; init; }
    public int ProvinceId { get; init; }
    public string ProvinceName { get; init; } = "";
    public string Name { get; init; } = "";
    public double? Lat { get; init; }
    public double? Lng { get; init; }
    public bool IsActive { get; init; }
    public int Loads { get; init; }
}

public sealed record CityOption(int Id, string Name, string Province);

public sealed class CitiesVm
{
    public required PageVm<CityRowVm> Page { get; init; }
    public List<Province> Provinces { get; init; } = [];
    public int? ProvinceId { get; init; }
    public string? Q { get; init; }
    public string? Filter { get; init; }
}

public sealed class TerminalRowVm
{
    public int Id { get; init; }
    public int CityId { get; init; }
    public string City { get; init; } = "";
    public string Province { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Address { get; init; }
    public double? Lat { get; init; }
    public double? Lng { get; init; }
    public bool IsActive { get; init; }
}

public sealed class TerminalsVm
{
    public List<TerminalRowVm> Rows { get; init; } = [];
    public Terminal? Edit { get; init; }
    public List<CityOption> Cities { get; init; } = [];
}

public sealed class LaneRowVm
{
    public int Id { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public double DistanceKm { get; init; }
    public long? BaseRatePerTon { get; init; }
    public bool IsActive { get; init; }
    public int Loads { get; init; }
}

public sealed class LanesVm
{
    public List<LaneRowVm> Rows { get; init; } = [];
    public RouteLane? Edit { get; init; }
    public List<CityOption> Cities { get; init; } = [];
}

public sealed class GeofencesVm
{
    public List<Geofence> Items { get; init; } = [];
    public Geofence? Edit { get; init; }
}

public static class GeofenceKind
{
    public static readonly string[] All = ["restricted", "terminal", "port", "custom"];
    public static string Label(string k) => k switch
    {
        "restricted" => "منطقهٔ ممنوعه",
        "terminal" => "پایانه",
        "port" => "بندر",
        _ => "سفارشی"
    };
}

// =====================================================================
//  تنظیمات سیستم
// =====================================================================

public sealed class AdminRowVm
{
    public int AdminId { get; init; }
    public string Name { get; init; } = "";
    public string Mobile { get; init; } = "";
    public bool IsSuper { get; init; }
    public AdminPermission Permissions { get; init; }
    public bool IsActive { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class RolesVm
{
    public List<AdminRowVm> Rows { get; init; } = [];
    public AdminRowVm? Edit { get; init; }
    public int MeId { get; init; }
    public bool MeSuper { get; init; }
    public int ActiveSupers { get; init; }
}

public sealed class SettingFieldVm
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    /// <summary>bool | number | money | text</summary>
    public string Type { get; init; } = "text";
    public bool Secret { get; init; }
}

public sealed class SettingsGroupVm
{
    public string Group { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "bi-sliders";
    public string? Hint { get; init; }
    public string Back { get; init; } = "";
    public List<SettingFieldVm> Fields { get; init; } = [];
}

public sealed class SettingsPageVm
{
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "bi-sliders";
    public string? Subtitle { get; init; }
    public List<SettingsGroupVm> Groups { get; init; } = [];
}

public sealed class LogsVm
{
    public string Tab { get; init; } = "audit";
    public PageVm<AuditLog>? Audit { get; init; }
    public PageVm<SmsLog>? Sms { get; init; }
    public List<string> Entities { get; init; } = [];
    public string? Entity { get; init; }
    public string? ActionName { get; init; }
    public string? Actor { get; init; }
    public string? Mobile { get; init; }
    public string? Purpose { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

public sealed class VersionVm
{
    public string AppVersion { get; init; } = "";
    public string? InformationalVersion { get; init; }
    /// <summary>تاریخ و ساعت نسخه‌گذاری — زمان ساخت فایل برنامهٔ در حال اجرا (UTC).</summary>
    public DateTime? BuiltAt { get; init; }
    public string EnvironmentName { get; init; } = "";
    public string Runtime { get; init; } = "";
    public string Os { get; init; } = "";
    public string? DatabaseName { get; init; }
    public List<string> Applied { get; init; } = [];
    public List<string> Pending { get; init; } = [];
    public required SettingsGroupVm Form { get; init; }
}
