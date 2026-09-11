using System.Globalization;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  پشتیبان پنل شرکت (بخش ب): مشتریان، مالی، گزارش‌ها، کاربران، حساب شرکت
//
//  ردیف‌ها کلاس با init هستند نه record موقعیتی: EF Core عضوِ «new X { A = … }»
//  را در کوئری می‌شناسد و مرتب‌سازی/شمارش پس از projection هم ترجمه می‌شود.
// ---------------------------------------------------------------------------

/// <summary>برچسب‌های فارسیِ مقادیرِ رشته‌ایِ پنل شرکت که در Entities برچسب ندارند.</summary>
public static class CompanyLabels
{
    // ---- کاربران شرکت ----
    public const string Owner = "owner";
    public static readonly string[] UserTitles = [Owner, "dispatcher", "accountant", "viewer"];
    /// <summary>عنوان‌هایی که برای کاربر تازه قابل انتخاب‌اند (مدیر شرکت فقط یکی است).</summary>
    public static readonly string[] AssignableTitles = ["dispatcher", "accountant", "viewer"];

    public static string UserTitle(string? t) => t switch
    {
        Owner => "مدیر شرکت",
        "dispatcher" => "کارشناس تخصیص و عملیات",
        "accountant" => "حسابدار",
        "viewer" => "ناظر (فقط مشاهده)",
        _ => t ?? ""
    };

    /// <summary>همهٔ پرچم‌های دسترسی به ترتیب نمایش — بدون None و All.</summary>
    public static readonly CompanyPermission[] Permissions =
    [
        CompanyPermission.Loads, CompanyPermission.Dispatch, CompanyPermission.Drivers, CompanyPermission.Fleet,
        CompanyPermission.Finance, CompanyPermission.Reports, CompanyPermission.Customers, CompanyPermission.Users
    ];

    public static string Permission(CompanyPermission p) => p switch
    {
        CompanyPermission.Loads => "بارها",
        CompanyPermission.Dispatch => "تخصیص و کنترل سفر",
        CompanyPermission.Drivers => "رانندگان",
        CompanyPermission.Fleet => "ناوگان",
        CompanyPermission.Finance => "مالی",
        CompanyPermission.Reports => "گزارش‌ها",
        CompanyPermission.Customers => "مشتریان",
        CompanyPermission.Users => "کاربران",
        _ => p.ToString()
    };

    /// <summary>دسترسی پیشنهادی هر عنوان — فقط پیش‌فرضِ فرم؛ مدیر می‌تواند تغییرش دهد.</summary>
    public static CompanyPermission DefaultFor(string? title) => title switch
    {
        Owner => CompanyPermission.All,
        "dispatcher" => CompanyPermission.Loads | CompanyPermission.Dispatch | CompanyPermission.Drivers | CompanyPermission.Fleet,
        "accountant" => CompanyPermission.Finance | CompanyPermission.Reports | CompanyPermission.Customers,
        "viewer" => CompanyPermission.Reports,
        _ => CompanyPermission.None
    };

    // ---- مشتریان ----
    public const string Person = "person";
    public const string Corporate = "corporate";
    public static string CustomerKind(string? k) => k == Corporate ? "شرکتی (حقوقی)" : "حقیقی";

    // ---- قرارداد ----
    public static readonly string[] ContractStatuses = ["draft", "active", "expired", "terminated"];
    public static string ContractStatus(string s) => s switch
    {
        "draft" => "پیش‌نویس",
        "active" => "فعال",
        "expired" => "منقضی",
        "terminated" => "فسخ‌شده",
        _ => s
    };
    public static string ContractTone(string s) => s switch { "active" => "ok", "draft" => "wait", "terminated" => "no", _ => "mut" };

    // ---- هزینه ----
    public static readonly string[] ExpenseKinds = ["fuel", "toll", "repair", "salary", "other"];
    public static string ExpenseKind(string k) => k switch
    {
        "fuel" => "سوخت",
        "toll" => "عوارض و پارکینگ",
        "repair" => "تعمیر و نگهداری",
        "salary" => "حقوق و دستمزد",
        _ => "سایر"
    };
    public static string ExpenseIcon(string k) => k switch
    {
        "fuel" => "bi-fuel-pump",
        "toll" => "bi-sign-stop",
        "repair" => "bi-tools",
        "salary" => "bi-people",
        _ => "bi-three-dots"
    };

    // ---- فاکتور ----
    public static string InvoiceKind(string k) => k switch
    {
        "freight" => "کرایهٔ حمل",
        "commission" => "کمیسیون بارگو",
        "subscription" => "اشتراک",
        _ => k
    };

    /// <summary>نوع‌های گردش کیف پول که برای شرکت معنا دارند (فیلتر «تراکنش‌ها»).</summary>
    public static readonly string[] CompanyTxnKinds =
    [
        WalletTxnKind.Charge, WalletTxnKind.FareIncome, WalletTxnKind.FarePayment, WalletTxnKind.DriverShare,
        WalletTxnKind.Payout, WalletTxnKind.Refund, WalletTxnKind.Subscription, WalletTxnKind.Adjustment
    ];

    /// <summary>درصد برای عرض نوار CSS — همیشه بین ۰ و ۱۰۰ و با نقطهٔ لاتین.</summary>
    public static string Pct(double value, double max) =>
        (max <= 0 ? 0 : Math.Clamp(value / max * 100, 0, 100)).ToString("0.#", CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------
//  ماه شمسی
// ---------------------------------------------------------------------------

public sealed record PersianMonth(int Year, int Month, DateTime StartUtc, DateTime EndUtc)
{
    public string Name => PersianMonths.Names[Month];
    /// <summary>«شهریور ۱۴۰۵»</summary>
    public string Label => $"{Name} {Fa.Digits(Year.ToString(CultureInfo.InvariantCulture))}";
    public bool Contains(DateTime utc) => utc >= StartUtc && utc < EndUtc;
}

public static class PersianMonths
{
    private static readonly PersianCalendar Pc = new();

    public static readonly string[] Names =
        ["", "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];

    public static (int Year, int Month) Of(DateTime utc)
    {
        var t = Fa.ToTehran(utc);
        return (Pc.GetYear(t), Pc.GetMonth(t));
    }

    public static PersianMonth Make(int year, int month)
    {
        var start = Fa.ToUtc(Pc.ToDateTime(year, month, 1, 0, 0, 0, 0));
        var (ny, nm) = month == 12 ? (year + 1, 1) : (year, month + 1);
        var end = Fa.ToUtc(Pc.ToDateTime(ny, nm, 1, 0, 0, 0, 0));
        return new PersianMonth(year, month, start, end);
    }

    /// <summary>n ماه شمسی اخیر (ماه جاری آخرین ردیف).</summary>
    public static List<PersianMonth> Last(int n)
    {
        var (y, m) = Of(DateTime.UtcNow);
        var list = new List<PersianMonth>(n);
        for (var i = n - 1; i >= 0; i--)
        {
            var yy = y; var mm = m - i;
            while (mm < 1) { mm += 12; yy--; }
            list.Add(Make(yy, mm));
        }
        return list;
    }

    /// <summary>ماه‌هایی که با بازهٔ [from, to) هم‌پوشانی دارند (حداکثر ۶۰ ماه).</summary>
    public static List<PersianMonth> Between(DateTime fromUtc, DateTime toUtc)
    {
        var (y, m) = Of(fromUtc);
        var list = new List<PersianMonth>();
        while (list.Count < 60)
        {
            var pm = Make(y, m);
            if (pm.StartUtc >= toUtc) break;
            list.Add(pm);
            if (++m > 12) { m = 1; y++; }
        }
        return list;
    }
}

/// <summary>بازهٔ تاریخ گزارش: از ابتدای روزِ «از» تا پایان روزِ «تا» به وقت تهران.</summary>
public sealed class ReportRange
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public string FromBox { get; init; } = "";
    public string ToBox { get; init; } = "";
    public int Days => Math.Max(1, (int)Math.Round((ToUtc - FromUtc).TotalDays));

    public static ReportRange From(DateTime? from, DateTime? to, int defaultDays = 30)
    {
        var toLocal = Fa.ToTehran(to ?? DateTime.UtcNow).Date;
        var fromLocal = from is DateTime f ? Fa.ToTehran(f).Date : toLocal.AddDays(-(defaultDays - 1));
        if (fromLocal > toLocal) (fromLocal, toLocal) = (toLocal, fromLocal);
        var fromUtc = Fa.ToUtc(fromLocal);
        var toUtc = Fa.ToUtc(toLocal.AddDays(1));
        return new ReportRange
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            FromBox = Fa.DateBox(Fa.ToUtc(fromLocal.AddHours(12))),
            ToBox = Fa.DateBox(Fa.ToUtc(toLocal.AddHours(12)))
        };
    }
}

public sealed class SelectItem
{
    public int Id { get; init; }
    public string Text { get; init; } = "";
}

// ---------------------------------------------------------------------------
//  مشتریان
// ---------------------------------------------------------------------------

public sealed class ShipperAggRow
{
    public int ShipperId { get; init; }
    public int Trips { get; init; }
    public int Done { get; init; }
    public int Cancelled { get; init; }
    public long Fare { get; init; }
    public DateTime FirstTripAt { get; init; }
    public DateTime LastTripAt { get; init; }
}

public sealed class ShipperInfo
{
    public int ShipperId { get; init; }
    public string Name { get; init; } = "";
    public string Mobile { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? City { get; init; }
    public double RatingAvg { get; init; }
    public int RatingCount { get; init; }
}

public sealed class CustomerRow
{
    public int CompanyCustomerId { get; init; }
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? Mobile { get; init; }
    public string? NationalId { get; init; }
    public string? Note { get; init; }
    public int? ShipperId { get; init; }
    public DateTime CreatedAt { get; init; }
    public int Contracts { get; init; }
    public int ActiveContracts { get; init; }
}

public sealed class ContractRow
{
    public int ContractId { get; init; }
    public string Title { get; init; } = "";
    public int? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime? EndsAt { get; init; }
    public long? Amount { get; init; }
    public string? FilePath { get; init; }
    public string Status { get; init; } = "";
    public string? Note { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>فعال است ولی تاریخ پایانش گذشته — باید «منقضی» شود.</summary>
    public bool Overdue => Status == "active" && EndsAt is DateTime e && e < DateTime.UtcNow;
}

/// <summary>یک سفر در فهرست‌های مالی و سوابق.</summary>
public sealed class CompanyTripRow
{
    public int TripId { get; init; }
    public string Code { get; init; } = "";
    public string? LoadCode { get; init; }
    public string Origin { get; init; } = "";
    public string Dest { get; init; } = "";
    public string CargoTitle { get; init; } = "";
    public string Status { get; init; } = "";
    public long Fare { get; init; }
    /// <summary>کل مبلغ پرداختی صاحب بار = کرایه + هزینه‌ها + ارزش افزوده.</summary>
    public long Total { get; init; }
    public long Commission { get; init; }
    public long CarrierShare { get; init; }
    public long? DriverShare { get; init; }
    public bool IsPaid { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DeliveredAt { get; init; }
    public DateTime? SettledAt { get; init; }
    public DateTime? DriverSharePaidAt { get; init; }
    /// <summary>طرف حساب: صاحب بار (سفرهای حمل‌شده) یا حمل‌کننده (بارهای شرکت).</summary>
    public string Party { get; init; } = "";
    public int? DriverId { get; init; }
    public string? DriverName { get; init; }
}

// ---------------------------------------------------------------------------
//  مالی
// ---------------------------------------------------------------------------

public sealed class TxnRow
{
    public long Id { get; init; }
    public string Kind { get; init; } = "";
    public long Amount { get; init; }
    public long BalanceAfter { get; init; }
    public int? TripId { get; init; }
    public string? TripCode { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class ExpenseRow
{
    public int Id { get; init; }
    public string Kind { get; init; } = "";
    public long Amount { get; init; }
    public string? Note { get; init; }
    public DateTime SpentAt { get; init; }
    public int? TripId { get; init; }
    public string? TripCode { get; init; }
    public string? Plate { get; init; }
    public string? DriverName { get; init; }
}

public sealed class InvoiceRow
{
    public int InvoiceId { get; init; }
    public string No { get; init; } = "";
    public string Kind { get; init; } = "";
    public long Amount { get; init; }
    public long Tax { get; init; }
    public long Total { get; init; }
    public string Status { get; init; } = "";
    public DateTime IssuedAt { get; init; }
    public int? TripId { get; init; }
    public string? TripCode { get; init; }
}

public sealed class DriverDueRow
{
    public int DriverId { get; init; }
    public string Name { get; init; } = "";
    public string Mobile { get; init; } = "";
    public int Trips { get; init; }
    public long Amount { get; init; }
    public bool InCompany { get; init; }
}

/// <summary>یک ماه در دفتر مالی شرکت (گزارش مالی و درآمد دوره‌ای).</summary>
public sealed class LedgerMonth
{
    public required PersianMonth Month { get; init; }
    /// <summary>سهم کرایهٔ واریزی به کیف پول (سفرهای بازار).</summary>
    public long Income { get; set; }
    /// <summary>کرایهٔ بارهای مشتریان خود شرکت که بیرون از بارگو دریافت می‌شود.</summary>
    public long Offline { get; set; }
    /// <summary>کمیسیون بارگو از سفرهای تسویه‌شدهٔ این ماه.</summary>
    public long Commission { get; set; }
    /// <summary>کرایهٔ پرداختی شرکت برای بارهایش به حمل‌کنندگان دیگر (پس از کسر استرداد).</summary>
    public long FarePaid { get; set; }
    public long DriverShares { get; set; }
    public long Expenses { get; set; }
    public int TripsDone { get; set; }

    public long Revenue => Income + Offline;
    public long Costs => FarePaid + DriverShares + Expenses;
    public long Net => Revenue - Costs;
}

// ---------------------------------------------------------------------------
//  گزارش‌ها
// ---------------------------------------------------------------------------

public sealed class DriverPerfRow
{
    public int DriverId { get; init; }
    public string Name { get; set; } = "";
    public string Mobile { get; set; } = "";
    public bool InCompany { get; set; }
    public int Trips { get; init; }
    public int Done { get; init; }
    public int Cancelled { get; init; }
    public double Km { get; init; }
    public long Revenue { get; init; }
    public long DriverShare { get; init; }
    public double? PeriodRating { get; set; }
    public int PeriodRatingCount { get; set; }
    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }
}

public sealed class VehiclePerfRow
{
    public int VehicleId { get; init; }
    public string Plate { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public bool InCompany { get; set; }
    public int Trips { get; set; }
    public int Done { get; set; }
    public double Km { get; set; }
    public long Revenue { get; set; }
    public double TrackedKm { get; set; }
    public DateTime? LastTripAt { get; set; }
    public int? IdleDays => LastTripAt is DateTime d ? Math.Max(0, (int)(DateTime.UtcNow - d).TotalDays) : null;
}

public sealed class CargoRow
{
    public string CargoType { get; init; } = "";
    public int Trips { get; init; }
    public int Done { get; init; }
    public decimal Tons { get; init; }
    public long Fare { get; init; }
}

public sealed class RouteRow
{
    public string Origin { get; init; } = "";
    public string Dest { get; init; } = "";
    public int Trips { get; init; }
    public int Done { get; init; }
    public long AvgFare { get; init; }
    public double? AvgKm { get; init; }
    public decimal Tons { get; init; }
}

public sealed class LabelCount
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public long Amount { get; init; }
}

// ---------------------------------------------------------------------------
//  کاربران و حساب شرکت
// ---------------------------------------------------------------------------

public sealed class CompanyUserRow
{
    public int CompanyUserId { get; init; }
    public string Name { get; init; } = "";
    public string Mobile { get; init; } = "";
    public string Title { get; init; } = "";
    public CompanyPermission Permissions { get; init; }
    public bool IsOwner { get; init; }
    public bool IsActive { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>وضعیت یک مدرکِ الزامی در فهرست تأیید حساب شرکت.</summary>
public sealed class CompanyDocCheck
{
    public string Kind { get; init; } = "";
    public string Label => DocumentKind.Label(Kind);
    /// <summary>missing | pending | approved | rejected | expired</summary>
    public string State { get; init; } = "missing";
    public Document? Doc { get; init; }

    public string StateLabel => State switch
    {
        "approved" => "تأییدشده",
        "pending" => "در انتظار بررسی",
        "rejected" => "ردشده — دوباره بارگذاری کنید",
        "expired" => "منقضی — نسخهٔ تازه بارگذاری کنید",
        _ => "بارگذاری نشده"
    };

    public string Tone => State switch { "approved" => "ok", "pending" => "wait", "rejected" or "expired" => "no", _ => "mut" };

    public static CompanyDocCheck For(string kind, Document? latest) => new()
    {
        Kind = kind,
        Doc = latest,
        State = latest is null ? "missing"
            : latest.Status == AccountStatus.Approved && latest.IsExpired ? "expired"
            : latest.Status
    };
}
