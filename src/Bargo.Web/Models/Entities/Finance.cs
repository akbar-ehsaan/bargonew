using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

public static class WalletTxnKind
{
    public const string Charge = "charge";              // شارژ کیف پول
    public const string FarePayment = "fare_payment";   // پرداخت کرایه توسط صاحب بار (−)
    public const string FareIncome = "fare_income";     // سهم حمل‌کننده (+)
    public const string DriverShare = "driver_share";   // سهم رانندهٔ شرکت (+ راننده، − شرکت)
    public const string Commission = "commission";      // کمیسیون بارگو (+ پلتفرم)
    public const string Fees = "fees";                  // هزینه‌های جانبی: بارگیری/تخلیه (+ حمل‌کننده)، بارنامه و مالیات (+ پلتفرم)
    public const string Payout = "payout";              // برداشت به حساب بانکی (−)
    public const string Refund = "refund";              // استرداد (+)
    public const string Subscription = "subscription";  // خرید اشتراک (−)
    public const string Adjustment = "adjustment";      // اصلاح دستی مدیر

    public static string Label(string k) => k switch
    {
        Charge => "شارژ کیف پول",
        FarePayment => "پرداخت کرایه",
        FareIncome => "درآمد کرایه",
        DriverShare => "سهم راننده",
        Commission => "کمیسیون بارگو",
        Fees => "هزینه‌های جانبی",
        Payout => "برداشت",
        Refund => "استرداد",
        Subscription => "اشتراک",
        Adjustment => "اصلاح دستی",
        _ => k
    };
}

/// <summary>
/// گردش کیف پول. موجودیِ روی حساب (مثلاً Driver.WalletBalance) فقط کشِ جمعِ همین
/// ردیف‌هاست و تنها از <c>WalletService</c> تغییر می‌کند؛ BalanceAfter در هر ردیف
/// اجازه می‌دهد مغایرت را بی‌درنگ پیدا کنید.
/// </summary>
public class WalletTransaction : IOwned
{
    public long WalletTransactionId { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    public long Amount { get; set; }
    public long BalanceAfter { get; set; }
    [MaxLength(20)] public string Kind { get; set; } = "";
    public int? TripId { get; set; }
    public int? PaymentId { get; set; }
    public int? PayoutRequestId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class PaymentStatus
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string Failed = "failed";
    public const string Refunded = "refunded";

    public static string Label(string s) => s switch
    {
        Pending => "در انتظار پرداخت",
        Paid => "پرداخت‌شده",
        Failed => "ناموفق",
        Refunded => "مسترد‌شده",
        _ => s
    };

    public static string Tone(string s) => s switch { Paid => "ok", Pending => "wait", Failed => "no", _ => "mut" };
}

/// <summary>پرداخت (درگاه یا کیف پول).</summary>
public class Payment
{
    public int PaymentId { get; set; }
    [MaxLength(12)] public string PayerKind { get; set; } = "";
    public int PayerId { get; set; }
    public int? TripId { get; set; }
    public Trip? Trip { get; set; }
    public long Amount { get; set; }
    /// <summary>wallet | gateway | manual</summary>
    [MaxLength(12)] public string Method { get; set; } = "wallet";
    /// <summary>charge (شارژ کیف پول) | fare (کرایه سفر) | subscription</summary>
    [MaxLength(16)] public string Purpose { get; set; } = "fare";
    [MaxLength(12)] public string Status { get; set; } = PaymentStatus.Pending;
    [MaxLength(30)] public string? Gateway { get; set; }
    [MaxLength(60)] public string? Authority { get; set; }
    [MaxLength(40)] public string? RefId { get; set; }
    public string? FailReason { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class PayoutStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Paid = "paid";
    public const string Rejected = "rejected";

    public static string Label(string s) => s switch
    {
        Pending => "در انتظار بررسی",
        Approved => "تأییدشده",
        Paid => "واریزشده",
        Rejected => "ردشده",
        _ => s
    };

    public static string Tone(string s) => s switch { Paid => "ok", Approved => "inf", Pending => "wait", Rejected => "no", _ => "mut" };
}

/// <summary>درخواست برداشت / تسویه با راننده، شرکت یا صاحب بار.</summary>
public class PayoutRequest : IOwned
{
    public int PayoutRequestId { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    [MaxLength(100)] public string OwnerName { get; set; } = "";
    public long Amount { get; set; }
    [MaxLength(26)] public string Sheba { get; set; } = "";
    [MaxLength(12)] public string Status { get; set; } = PayoutStatus.Pending;
    public string? ReviewNote { get; set; }
    public int? ReviewedByAdminId { get; set; }
    [MaxLength(40)] public string? BankTrackingNo { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Invoice : IOwned
{
    public int InvoiceId { get; set; }
    [MaxLength(20)] public string No { get; set; } = "";
    public int? TripId { get; set; }
    public Trip? Trip { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    /// <summary>freight (کرایه برای صاحب بار) | commission (کمیسیون برای حمل‌کننده) | subscription</summary>
    [MaxLength(16)] public string Kind { get; set; } = "freight";
    public long Amount { get; set; }
    public long Tax { get; set; }
    public long Total { get; set; }
    [MaxLength(10)] public string Status { get; set; } = "paid";
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
}

public class Refund : IOwned
{
    public int RefundId { get; set; }
    public int? PaymentId { get; set; }
    public int? TripId { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    public long Amount { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>pending | done | rejected</summary>
    [MaxLength(10)] public string Status { get; set; } = "pending";
    public int? ReviewedByAdminId { get; set; }
    public DateTime? DoneAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>هزینه‌های شرکت — «مالی و حسابداری ← هزینه».</summary>
public class CompanyExpense
{
    public int CompanyExpenseId { get; set; }
    public int CompanyId { get; set; }
    public int? TripId { get; set; }
    public int? VehicleId { get; set; }
    public int? DriverId { get; set; }
    /// <summary>fuel | toll | repair | salary | other</summary>
    [MaxLength(12)] public string Kind { get; set; } = "other";
    public long Amount { get; set; }
    public string? Note { get; set; }
    public DateTime SpentAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SubscriptionPlan
{
    public int SubscriptionPlanId { get; set; }
    [MaxLength(80)] public string Title { get; set; } = "";
    /// <summary>company | driver | shipper</summary>
    [MaxLength(10)] public string Audience { get; set; } = "company";
    public int DurationDays { get; set; } = 30;
    public long Price { get; set; }
    public int? MaxDrivers { get; set; }
    public int? MaxVehicles { get; set; }
    public string? Features { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class Subscription : IOwned
{
    public int SubscriptionId { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    public int SubscriptionPlanId { get; set; }
    public SubscriptionPlan? Plan { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public long PaidAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DiscountCode
{
    public int DiscountCodeId { get; set; }
    [MaxLength(30)] public string Code { get; set; } = "";
    public int? Percent { get; set; }
    public long? Amount { get; set; }
    public int MaxUses { get; set; } = 1;
    public int Used { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>تعرفهٔ خدمات (بیمه بار، هزینهٔ عضویت، …) — «تعرفه و کمیسیون».</summary>
public class Tariff
{
    public int TariffId { get; set; }
    [MaxLength(40)] public string Key { get; set; } = "";
    [MaxLength(100)] public string Title { get; set; } = "";
    public long? Amount { get; set; }
    public decimal? Percent { get; set; }
    public bool IsActive { get; set; } = true;
}
