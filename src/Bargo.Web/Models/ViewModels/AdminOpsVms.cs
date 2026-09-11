using System.Text.Encodings.Web;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Html;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  مدل‌های نمایشِ پنل مدیر — بخش عملیات (کاربران، بار، سفر، ناوگان، مدارک، کنترل زنده)
//
//  ردیف‌های فهرست کلاس با setter‌اند نه record: EF Core پرس‌وجوی «new X { A = … }»
//  را کامل به SQL ترجمه می‌کند و شمارش/برشِ PageVm روی همان کوئری کار می‌کند؛
//  سازندهٔ record فقط در آخرین Select سمت کلاینت اجرا می‌شود.
// ---------------------------------------------------------------------------

/// <summary>یک نشانگر نقشه (قالب data-map در wwwroot/js/map.js).</summary>
public sealed record MapMarker(double Lat, double Lng, string Kind, string Label, string? Href = null);

/// <summary>صاحب یک ردیف مشترک (مدرک، تراکنش) به زبان آدمی: نام، توضیح کوتاه، پیوند پرونده.</summary>
public sealed record OwnerRef(string Name, string? Sub = null, string? Href = null);

public sealed record SelectOption(int Id, string Text);

public sealed class AdminDashboardVm
{
    public List<Stat> People { get; set; } = [];
    public List<Stat> Ops { get; set; } = [];
    public List<TodoItem> Todo { get; set; } = [];
    public List<TripRow> RecentTrips { get; set; } = [];
    public List<MapMarker> Markers { get; set; } = [];
    public long PlatformBalance { get; set; }
}

public sealed class DriverRow
{
    public int DriverId { get; set; }
    public string FullName { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string NationalCode { get; set; } = "";
    public string? City { get; set; }
    public int? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string Status { get; set; } = "";
    public string? StatusReason { get; set; }
    public int TripCount { get; set; }
    public int Cancelled { get; set; }
    public int Violations { get; set; }
    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>فقط صفحهٔ «تعلیق حساب»: زمان آخرین تعلیق (از ردّ حسابرسی) و پایانِ تعلیقِ اعلام‌شده در تخلف.</summary>
    public DateTime? SuspendedAt { get; set; }
    public DateTime? SuspendedUntil { get; set; }
}

public sealed class ShipperRow
{
    public int ShipperId { get; set; }
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string? NationalCode { get; set; }
    public string? NationalId { get; set; }
    public string? City { get; set; }
    public string Status { get; set; } = "";
    public string? VerifyStatus { get; set; }
    public long WalletBalance { get; set; }
    public int Loads { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CompanyRow
{
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string NationalId { get; set; } = "";
    public string ManagerName { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string? City { get; set; }
    public string Status { get; set; } = "";
    public string? StatusReason { get; set; }
    public int Drivers { get; set; }
    public int Vehicles { get; set; }
    public int ActiveTrips { get; set; }
    public int Trips30 { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? LicenseNo { get; set; }
    public DateTime? LicenseExpiresAt { get; set; }
    public long WalletBalance { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>ردیف «کاربران مسدودشده» — راننده، صاحب بار و شرکت در یک جدول.</summary>
public sealed class BlockedRow
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Href => Kind switch
    {
        OwnerKind.Driver => $"/Admin/Users/Driver/{Id}",
        OwnerKind.Shipper => $"/Admin/Users/Shipper/{Id}",
        _ => $"/Admin/Users/Company/{Id}"
    };
}

public sealed class LoadRow
{
    public int LoadId { get; set; }
    public string Code { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public string Title { get; set; } = "";
    public string CargoType { get; set; } = "";
    public decimal WeightTon { get; set; }
    public string OwnerName { get; set; } = "";
    public int? ShipperId { get; set; }
    public int? CompanyId { get; set; }
    public long? Price { get; set; }
    public string PriceMode { get; set; } = "";
    public DateTime LoadingFrom { get; set; }
    public string Status { get; set; } = "";
    public int PendingOffers { get; set; }
    public int? TripId { get; set; }
    public string? TripCode { get; set; }
    public string? TripStatus { get; set; }
    public bool IsReported { get; set; }
    public string? ReportNote { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class TripRow
{
    public int TripId { get; set; }
    public string Code { get; set; } = "";
    public int LoadId { get; set; }
    public string LoadCode { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public string CarrierKind { get; set; } = "";
    public int? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public int? DriverId { get; set; }
    public string? DriverName { get; set; }
    public string? DriverMobile { get; set; }
    public string? Plate { get; set; }
    public string Status { get; set; } = "";
    public long Fare { get; set; }
    public bool IsPaid { get; set; }
    public bool IsProblem { get; set; }
    public string? ProblemNote { get; set; }
    public double TravelledKm { get; set; }
    public double? PlannedKm { get; set; }
    public DateTime? EtaAt { get; set; }
    public DateTime? LastPointAt { get; set; }
    public DateTime? ScheduledDepartureAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public string? LoadTitle { get; set; }
    public decimal WeightTon { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>برای صفحهٔ «سفرهای مشکل‌دار» — در حافظه از روی همین ستون‌ها پر می‌شود.</summary>
    public List<string> Reasons { get; set; } = [];

    public string CarrierName => CarrierKind == Entities.CarrierKind.Company
        ? (CompanyName ?? "—") + (DriverName is null ? "" : " · " + DriverName)
        : DriverName ?? "—";
}

public sealed class VehicleRow
{
    public int VehicleId { get; set; }
    public string PlateNo { get; set; } = "";
    public string? TypeName { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public int? Year { get; set; }
    public decimal CapacityTon { get; set; }
    public int? DriverId { get; set; }
    public string? DriverName { get; set; }
    public string? DriverMobile { get; set; }
    public int? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public DateTime? InsuranceExpiresAt { get; set; }
    public DateTime? InspectionExpiresAt { get; set; }
    public string Status { get; set; } = "";
    public string VerifyStatus { get; set; } = "";
    public int PendingDocs { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public double? LastLat { get; set; }
    public double? LastLng { get; set; }
    public int? LiveTripId { get; set; }
    public string? LiveTripCode { get; set; }
    public DateTime CreatedAt { get; set; }

    public string OwnerName => CompanyName ?? DriverName ?? "—";
    public string? OwnerHref => CompanyId is int c ? $"/Admin/Users/Company/{c}" : DriverId is int d ? $"/Admin/Users/Driver/{d}" : null;
}

/// <summary>یک ردیف چک‌لیست مدارک: آخرین مدرکِ غیرِردشدهٔ یک نوع (یا آخرین ردشده، اگر فقط همان هست).</summary>
public sealed record DocCheck(string Kind, Document? Doc)
{
    public string Tone => Doc is null ? "no" : Doc.Status == AccountStatus.Approved ? (Doc.IsExpired ? "no" : "ok") : Doc.Status == AccountStatus.Pending ? "wait" : "no";
    public string Text => Doc is null ? "بارگذاری نشده" : Doc.Status == AccountStatus.Approved && Doc.IsExpired ? "منقضی" : AccountStatus.Label(Doc.Status);

    public static List<DocCheck> For(IEnumerable<string> kinds, IEnumerable<Document> docs)
    {
        var list = docs.ToList();
        return kinds.Select(k => new DocCheck(k,
            list.Where(d => d.Kind == k && d.Status != AccountStatus.Rejected).OrderByDescending(d => d.UploadedAt).FirstOrDefault()
            ?? list.Where(d => d.Kind == k).OrderByDescending(d => d.UploadedAt).FirstOrDefault())).ToList();
    }
}

public sealed class AccountActionVm
{
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? StatusReason { get; set; }
    public string? ReturnUrl { get; set; }
}

/// <summary>فرم تغییر نام کاربری (موبایل) و گذرواژه در پروندهٔ کاربر — Users/_LoginEdit.</summary>
public sealed class LoginEditVm
{
    /// <summary>driver | shipper | companyuser</summary>
    public string Kind { get; set; } = "";
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public sealed class DriverDetailVm
{
    public Driver Driver { get; set; } = null!;
    public List<Document> Documents { get; set; } = [];
    public List<DocCheck> Checklist { get; set; } = [];
    public List<Vehicle> Vehicles { get; set; } = [];
    public List<Document> VehicleDocuments { get; set; } = [];
    public List<DriverReadiness.Issue> Issues { get; set; } = [];
    public List<TripRow> Trips { get; set; } = [];
    public int TripTotal { get; set; }
    public int TripDone { get; set; }
    public int TripCancelled { get; set; }
    public List<WalletTransaction> Txns { get; set; } = [];
    public List<PayoutRequest> Payouts { get; set; } = [];
    public List<Rating> Ratings { get; set; } = [];
    public List<Violation> Violations { get; set; } = [];
    public List<Complaint> Complaints { get; set; } = [];
    public Dictionary<int, string> AdminNames { get; set; } = [];
    public int WarnDays { get; set; } = 30;
}

public sealed class ShipperDetailVm
{
    public Shipper Shipper { get; set; } = null!;
    public List<Document> Documents { get; set; } = [];
    public List<LoadRow> Loads { get; set; } = [];
    public int LoadTotal { get; set; }
    public int TripTotal { get; set; }
    public List<WalletTransaction> Txns { get; set; } = [];
    public List<Rating> Ratings { get; set; } = [];
    public List<Complaint> Complaints { get; set; } = [];
    public Dictionary<int, string> AdminNames { get; set; } = [];
    public int WarnDays { get; set; } = 30;
}

public sealed class CompanyDetailVm
{
    public Company Company { get; set; } = null!;
    public List<Document> Documents { get; set; } = [];
    public List<DocCheck> Checklist { get; set; } = [];
    public List<CompanyUser> Users { get; set; } = [];
    public List<DriverRow> Drivers { get; set; } = [];
    public List<Vehicle> Vehicles { get; set; } = [];
    public List<TripRow> Trips { get; set; } = [];
    public int TripTotal { get; set; }
    public int ActiveTrips { get; set; }
    public List<WalletTransaction> Txns { get; set; } = [];
    public List<Rating> Ratings { get; set; } = [];
    public List<Violation> Violations { get; set; } = [];
    public List<Complaint> Complaints { get; set; } = [];
    public List<Contract> Contracts { get; set; } = [];
    public Dictionary<int, string> AdminNames { get; set; } = [];
    public int WarnDays { get; set; } = 30;
}

/// <summary>درخواست عضویت شرکت با چک‌لیست مدارک.</summary>
public sealed class CompanyRequestVm
{
    public Company Company { get; set; } = null!;
    public CompanyUser? Owner { get; set; }
    public List<DocCheck> Checklist { get; set; } = [];
    public int OtherDocs { get; set; }
}

/// <summary>رانندهٔ در انتظار تأیید با نتیجهٔ DriverReadiness.</summary>
public sealed class PendingDriverVm
{
    public Driver Driver { get; set; } = null!;
    public List<DocCheck> Checklist { get; set; } = [];
    public Vehicle? Vehicle { get; set; }
    public List<DocCheck> VehicleChecklist { get; set; } = [];
    public List<DriverReadiness.Issue> Issues { get; set; } = [];
}

public sealed class ShipperVerifyVm
{
    public Shipper Shipper { get; set; } = null!;
    public List<Document> Documents { get; set; } = [];
}

/// <summary>ردیف صف مدارک با صاحبِ حل‌شده.</summary>
public sealed class DocumentRowVm
{
    public Document Doc { get; set; } = null!;
    public OwnerRef Owner { get; set; } = new("—");
    public string? ReviewerName { get; set; }
    public int LiveTrips { get; set; }
    /// <summary>نسخهٔ تمدیدشدهٔ همین مدرک بارگذاری شده و در صف بررسی است.</summary>
    public bool RenewalPending { get; set; }
    /// <summary>فقط صفحهٔ «هشدارها»: آخرین یادآوری‌ای که مدیر برای این مدرک فرستاده (از ردّ حسابرسی).</summary>
    public DateTime? LastRemindAt { get; set; }
}

/// <summary>ردیف «مجوزهای شرکت»: مجوز ثبت‌شده روی حساب + آخرین مدرک مجوز بارگذاری‌شده.</summary>
public sealed class CompanyLicenseRow
{
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? LicenseNo { get; set; }
    public DateTime? LicenseExpiresAt { get; set; }
    public int? DocId { get; set; }
    public string? DocStatus { get; set; }
    public string? DocNumber { get; set; }
    public string? DocPath { get; set; }
    public DateTime? DocExpiresAt { get; set; }
    public DateTime? DocUploadedAt { get; set; }

    /// <summary>انقضای مؤثر: تاریخِ ثبت‌شده روی حساب، وگرنه تاریخ روی آخرین مدرک مجوز.</summary>
    public DateTime? EffectiveExpiry => LicenseExpiresAt ?? DocExpiresAt;
}

public sealed class TripCaseVm
{
    public Trip Trip { get; set; } = null!;
    public Offer? Offer { get; set; }
    public List<string> Reasons { get; set; } = [];
    public List<TripEvent> Events { get; set; } = [];
    public TripEvent? DeliveryEvent { get; set; }
    public TripEvent? LastEvent { get; set; }
    public List<TripAssignment> Assignments { get; set; } = [];
    public Dictionary<int, string> DriverNames { get; set; } = [];
    public Dictionary<int, string> VehiclePlates { get; set; } = [];
    public List<MapMarker> Markers { get; set; } = [];
    public List<double[]> Line { get; set; } = [];
    public int PointCount { get; set; }
    public DateTime? FirstPointAt { get; set; }
    public DateTime? LastPointAt { get; set; }
    public double? MaxSpeed { get; set; }
    public List<Message> Messages { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<WalletTransaction> Txns { get; set; } = [];
    public List<Invoice> Invoices { get; set; } = [];
    public List<Refund> Refunds { get; set; } = [];
    public Dictionary<string, OwnerRef> Owners { get; set; } = [];
    public List<TripDocument> Documents { get; set; } = [];
    public List<Complaint> Complaints { get; set; } = [];
    public List<Ticket> Tickets { get; set; } = [];
    public List<Rating> Ratings { get; set; } = [];
    public List<Violation> Violations { get; set; } = [];
    public IReadOnlyList<string> NextSteps { get; set; } = [];
    public bool CanCancel { get; set; }
    public bool CanSettle { get; set; }
    public bool CanAssign { get; set; }
    public List<SelectOption> CompanyDrivers { get; set; } = [];
    public List<SelectOption> CompanyVehicles { get; set; } = [];
    public string LoadOwnerName { get; set; } = "";
    public string? LoadOwnerMobile { get; set; }
    public string? LoadOwnerHref { get; set; }
    public bool DeliveryOtpRequired { get; set; }
}

public sealed class LiveAlertsVm
{
    public List<TripRow> NoGps { get; set; } = [];
    public List<TripRow> PastEta { get; set; } = [];
    public List<(TripRow Trip, string What, DateTime ExpiredAt)> ExpiredDocs { get; set; } = [];
    public List<Payment> FailedPayments { get; set; } = [];
    public Dictionary<string, OwnerRef> PayerNames { get; set; } = [];
    public List<PayoutRequest> StalePayouts { get; set; } = [];

    public int Total => NoGps.Count + PastEta.Count + ExpiredDocs.Count + FailedPayments.Count + StalePayouts.Count;
}

/// <summary>پروندهٔ بار در پنل مدیر: خودِ بار، صاحبش، پیشنهادها و سفرهایش.</summary>
public sealed class LoadCaseVm
{
    public Load Load { get; set; } = null!;
    public OwnerRef Owner { get; set; } = new("—");
    public List<Offer> Offers { get; set; } = [];
    public List<TripRow> Trips { get; set; } = [];
    public List<MapMarker> Markers { get; set; } = [];
    /// <summary>گفتگوهای پیش از قطعی شدن (load:{id}:driver:{did}) — فقط شمار، برای نشان دادنِ «چند راننده پرسیده‌اند».</summary>
    public int MessageCount { get; set; }

    /// <summary>بارِ قطعی‌نشده از همین‌جا لغو می‌شود؛ بارِ قطعی‌شده از پروندهٔ سفرش.</summary>
    public bool CanCancel => Load.Status is LoadStatus.Draft or LoadStatus.Open or LoadStatus.Offering;
}

/// <summary>نتیجهٔ «رهگیری سفر» با کد: سفر، نشانگرها و مسیر طی‌شده.</summary>
public sealed class TripTrackVm
{
    public string Code { get; set; } = "";
    public Trip? Trip { get; set; }
    public List<MapMarker> Markers { get; set; } = [];
    public List<double[]> Line { get; set; } = [];
    public int PointCount { get; set; }
    public DateTime? FirstPointAt { get; set; }
    public List<TripEvent> Events { get; set; } = [];
    /// <summary>پیام وقتی کد وارد شده ولی سفری پیدا نشده.</summary>
    public string? NotFound { get; set; }
}

/// <summary>نقشهٔ کشور با شمارنده‌های کنار آن.</summary>
public sealed class LiveBoardVm
{
    public List<MapMarker> Markers { get; set; } = [];
    public Dictionary<string, int> ByStatus { get; set; } = [];
    public int LiveTrips { get; set; }
    public int Upcoming { get; set; }
    public int IdleVehicles { get; set; }
    public int StaleVehicles { get; set; }
    public int NoGps { get; set; }
    public int PastEta { get; set; }
    public int LoadsInTransit { get; set; }
    public int OnlineDrivers { get; set; }
    public int Alerts { get; set; }
    public List<TripRow> Recent { get; set; } = [];
}

/// <summary>تکه‌های مارک‌آپ پنل مدیر که در چند صفحه تکرار می‌شوند.</summary>
public static class OpsUi
{
    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");

    public static bool IsImage(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";

    /// <summary>پیش‌نمایش فایل: تصویر بندانگشتی، PDF پیوند، نبودنِ فایل «بدون فایل».</summary>
    public static IHtmlContent File(string? path, bool large = false)
    {
        if (string.IsNullOrEmpty(path)) return new HtmlString("<span class=\"muted small\">بدون فایل</span>");
        var url = E(DocumentStorage.Url(path));
        if (IsImage(path))
            return new HtmlString($"<a href=\"{url}\" target=\"_blank\" rel=\"noopener\" title=\"نمایش در اندازهٔ کامل\"><img src=\"{url}\" alt=\"پیش‌نمایش مدرک\" loading=\"lazy\" style=\"max-height:{(large ? 160 : 56)}px;max-width:{(large ? 260 : 96)}px;border-radius:8px;border:1px solid var(--line);vertical-align:middle\" /></a>");
        return new HtmlString($"<a class=\"lnk nowrap\" href=\"{url}\" target=\"_blank\" rel=\"noopener\"><i class=\"bi bi-file-earmark-pdf\"></i> مشاهدهٔ فایل</a>");
    }

    /// <summary>به رندر مشترک Plate.Html می‌سپارد تا نشان پلاک در پنل مدیر با بقیهٔ پنل‌ها (partial ‏_Plate) یک‌شکل باشد.</summary>
    public static IHtmlContent Plate(string? plate) => Services.Plate.Html(plate);

    public static IHtmlContent Owner(OwnerRef? o)
    {
        if (o is null) return new HtmlString("<span class=\"muted\">—</span>");
        var name = o.Href is null ? $"<b>{E(o.Name)}</b>" : $"<a class=\"lnk\" href=\"{E(o.Href)}\">{E(o.Name)}</a>";
        return new HtmlString(o.Sub is null ? name : $"{name}<span class=\"sub\">{E(o.Sub)}</span>");
    }

    /// <summary>آخرین GPS: بیش از دو ساعت یا هرگز → قرمز.</summary>
    public static IHtmlContent Gps(DateTime? at, double staleHours = 2)
    {
        if (at is null) return Ui.Pill("بدون GPS", "no");
        return Ui.Pill(Fa.Ago(at.Value), at.Value < DateTime.UtcNow.AddHours(-staleHours) ? "no" : "ok");
    }

    public static string PenaltyLabel(string p) => p switch
    {
        "fine" => "جریمه",
        "suspension" => "تعلیق حساب",
        _ => "اخطار"
    };

    public static string PenaltyTone(string p) => p switch { "suspension" => "no", "fine" => "wait", _ => "mut" };

    public static string WaybillLabel(string s) => s switch
    {
        "verified" => "تأییدشده",
        "void" => "باطل",
        _ => "ثبت‌شده"
    };

    public static string WaybillTone(string s) => s switch { "verified" => "ok", "void" => "no", _ => "wait" };

    public static string ContractLabel(string s) => s switch
    {
        "draft" => "پیش‌نویس",
        "expired" => "منقضی",
        "terminated" => "فسخ‌شده",
        _ => "فعال"
    };

    public static string ContractTone(string s) => s switch { "active" => "ok", "draft" => "wait", "terminated" => "no", _ => "mut" };

    public static string RefundLabel(string s) => s switch { "done" => "انجام‌شده", "rejected" => "ردشده", _ => "در انتظار" };
    public static string RefundTone(string s) => s switch { "done" => "ok", "rejected" => "no", _ => "wait" };

    /// <summary>مبلغ علامت‌دار کیف پول: «+۱۲,۰۰۰ تومان» سبز / «−…» قرمز.</summary>
    public static IHtmlContent Signed(long rial) =>
        new HtmlString($"<b class=\"num nowrap\" style=\"color:var(--{(rial < 0 ? "no" : "ok")})\">{(rial < 0 ? "−" : "+")}{E(Fa.Toman(Math.Abs(rial)))}</b>");

    /// <summary>پیلِ یک ردیف چک‌لیست مدارک (DocCheck) با برچسب نوع مدرک.</summary>
    public static IHtmlContent Check(DocCheck c) =>
        new HtmlString($"<span class=\"pill {c.Tone}\" title=\"{E(c.Text)}\"><i class=\"bi {(c.Tone == "ok" ? "bi-check2-circle" : c.Tone == "wait" ? "bi-hourglass-split" : "bi-x-circle")}\"></i> {E(DocumentKind.Label(c.Kind))}: {E(c.Text)}</span>");
}

// ---------------------------------------------------------------------------
//  شرکت‌ها، رانندگان، ناوگان، بارنامه — مدل‌های صفحه‌های اختصاصی
// ---------------------------------------------------------------------------

/// <summary>ردیف «بارنامه‌ها»ی مدیر: بارنامه + سفر و حمل‌کننده‌اش.</summary>
public sealed class WaybillRow
{
    public int WaybillId { get; set; }
    public string Number { get; set; } = "";
    public string Status { get; set; } = "";
    public int TripId { get; set; }
    public string TripCode { get; set; } = "";
    public string TripStatus { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public string CarrierKind { get; set; } = "";
    public string? CompanyName { get; set; }
    public string? DriverName { get; set; }
    public string? Plate { get; set; }
    public string IssuedByKind { get; set; } = "";
    public int IssuedById { get; set; }
    public string? FilePath { get; set; }
    public DateTime IssuedAt { get; set; }

    public string CarrierName => CarrierKind == Entities.CarrierKind.Company
        ? (CompanyName ?? "—") + (DriverName is null ? "" : " · " + DriverName)
        : DriverName ?? "—";
}

/// <summary>ردیف «اسناد سفرها»: سند + سفر و بارگذارنده.</summary>
public sealed class TripDocRow
{
    public int TripDocumentId { get; set; }
    public int TripId { get; set; }
    public string TripCode { get; set; } = "";
    public string TripStatus { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? FilePath { get; set; }
    public string UploadedByKind { get; set; } = "";
    public int UploadedById { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>صفحهٔ «امتیاز رانندگان»: رتبه‌بندی + تازه‌ترین امتیازهای پایین.</summary>
public sealed class DriverRatingsVm
{
    public PageVm<DriverRow> Page { get; set; } = new();
    public List<Rating> Low { get; set; } = [];
    public Dictionary<int, string> DriverNames { get; set; } = [];
    public Dictionary<int, string> TripCodes { get; set; } = [];
    public Dictionary<string, OwnerRef> Raters { get; set; } = [];
    public int RatedDrivers { get; set; }
    public double OverallAvg { get; set; }
    public int LowCount30 { get; set; }
    public string Sort { get; set; } = "top";
    public string? Q { get; set; }
}

/// <summary>صفحهٔ «تخلفات» در مدیریت رانندگان: فهرست + فرم ثبت با راننده‌ای که با موبایل پیدا شده.</summary>
public sealed class DriverViolationsVm
{
    public PageVm<Violation> Page { get; set; } = new();
    public Dictionary<int, string> AdminNames { get; set; } = [];
    public Dictionary<int, string> TripCodes { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
    public string? Penalty { get; set; }
    public string? Q { get; set; }
    /// <summary>شمارهٔ موبایلی که در فرم جستجو شد (برای نمایش دوباره).</summary>
    public string? Mobile { get; set; }
    /// <summary>راننده‌ای که با موبایل یا شناسه پیدا شد — فرم ثبت روی او باز می‌شود.</summary>
    public Driver? Found { get; set; }
    public List<TripRow> FoundTrips { get; set; } = [];
    public int FoundViolations { get; set; }
}

/// <summary>صفحهٔ «انواع خودرو»: فهرست + شمار خودروها و بارهای هر نوع.</summary>
public sealed class VehicleTypesVm
{
    public List<VehicleType> Items { get; set; } = [];
    public Dictionary<int, int> Vehicles { get; set; } = [];
    public Dictionary<int, int> Loads { get; set; } = [];
    public VehicleType? Edit { get; set; }
    public static readonly string[] BodyKinds = ["کفی", "مسقف", "چادری", "یخچالی", "بغل‌باز", "کمپرسی", "تانکر", "کانتینری"];
}
