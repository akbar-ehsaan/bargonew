using System.ComponentModel.DataAnnotations;

namespace Bargo.Web.Models.Entities;

public static class TicketStatus
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string Waiting = "waiting";   // پاسخ کاربر رسیده، منتظر اپراتور
    public const string Closed = "closed";

    public static string Label(string s) => s switch
    {
        Open => "باز",
        Answered => "پاسخ داده شد",
        Waiting => "در انتظار پاسخ پشتیبانی",
        Closed => "بسته",
        _ => s
    };

    public static string Tone(string s) => s switch { Answered => "ok", Open or Waiting => "wait", _ => "mut" };
}

public static class TicketCategory
{
    public const string Support = "support";
    public const string Complaint = "complaint";
    public const string CargoIssue = "cargo_issue";
    public const string Financial = "financial";
    public const string Technical = "technical";

    public static string Label(string c) => c switch
    {
        Support => "پشتیبانی",
        Complaint => "شکایت",
        CargoIssue => "مشکل بار",
        Financial => "مالی",
        Technical => "فنی",
        _ => c
    };

    public static readonly string[] All = [Support, Complaint, CargoIssue, Financial, Technical];
}

public static class TicketPriority
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Urgent = "urgent";

    public static string Label(string p) => p switch { Low => "کم", High => "زیاد", Urgent => "فوری", _ => "عادی" };
    public static string Tone(string p) => p switch { Urgent => "no", High => "wait", _ => "mut" };
}

public class Ticket : IOwned
{
    public int TicketId { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    [MaxLength(100)] public string OwnerName { get; set; } = "";
    [MaxLength(16)] public string Category { get; set; } = TicketCategory.Support;
    [MaxLength(200)] public string Subject { get; set; } = "";
    public int? TripId { get; set; }
    [MaxLength(10)] public string Priority { get; set; } = TicketPriority.Normal;
    [MaxLength(12)] public string Status { get; set; } = TicketStatus.Open;
    public int? AssignedAdminId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<TicketMessage> Messages { get; set; } = [];
}

public class TicketMessage
{
    public int TicketMessageId { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    [MaxLength(12)] public string SenderKind { get; set; } = "";
    public int SenderId { get; set; }
    [MaxLength(100)] public string SenderName { get; set; } = "";
    public string Body { get; set; } = "";
    public string? AttachmentPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class ComplaintStatus
{
    public const string New = "new";
    public const string Reviewing = "reviewing";
    public const string Resolved = "resolved";
    public const string Rejected = "rejected";

    public static readonly string[] Open = [New, Reviewing];

    public static string Label(string s) => s switch
    {
        New => "جدید",
        Reviewing => "در حال رسیدگی",
        Resolved => "رسیدگی‌شده",
        Rejected => "ردشده",
        _ => s
    };

    public static string Tone(string s) => s switch { New => "wait", Reviewing => "inf", Resolved => "ok", Rejected => "no", _ => "mut" };
}

public static class ComplaintKind
{
    public const string Service = "service";
    public const string Financial = "financial";
    public const string Damage = "damage";
    public const string Delay = "delay";
    public const string Violation = "violation";
    public const string Other = "other";

    public static string Label(string k) => k switch
    {
        Service => "کیفیت خدمات",
        Financial => "اختلاف مالی",
        Damage => "خسارت / کسری بار",
        Delay => "تأخیر",
        Violation => "تخلف",
        _ => "سایر"
    };

    public static readonly string[] All = [Service, Financial, Damage, Delay, Violation, Other];
}

/// <summary>شکایت یا اختلاف — همیشه تا حد امکان به یک سفر گره می‌خورد تا پرونده کامل باشد.</summary>
public class Complaint
{
    public int ComplaintId { get; set; }
    [MaxLength(16)] public string Code { get; set; } = "";
    public int? TripId { get; set; }
    public Trip? Trip { get; set; }
    [MaxLength(12)] public string FromKind { get; set; } = "";
    public int FromId { get; set; }
    [MaxLength(100)] public string FromName { get; set; } = "";
    [MaxLength(12)] public string? AgainstKind { get; set; }
    public int? AgainstId { get; set; }
    [MaxLength(12)] public string Kind { get; set; } = ComplaintKind.Service;
    [MaxLength(200)] public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    [MaxLength(12)] public string Status { get; set; } = ComplaintStatus.New;
    public string? Resolution { get; set; }
    public long? CompensationAmount { get; set; }
    public int? ResolvedByAdminId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>تخلف ثبت‌شده توسط مدیر.</summary>
public class Violation
{
    public int ViolationId { get; set; }
    public int? DriverId { get; set; }
    public Driver? Driver { get; set; }
    public int? CompanyId { get; set; }
    public int? TripId { get; set; }
    public int? ComplaintId { get; set; }
    [MaxLength(60)] public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>warning | fine | suspension</summary>
    [MaxLength(12)] public string Penalty { get; set; } = "warning";
    public long? FineAmount { get; set; }
    public DateTime? SuspendedUntil { get; set; }
    public int CreatedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Notification : IOwned
{
    public long NotificationId { get; set; }
    [MaxLength(12)] public string OwnerKind { get; set; } = "";
    public int OwnerId { get; set; }
    [MaxLength(150)] public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    /// <summary>info | load | offer | trip | finance | document | system</summary>
    [MaxLength(12)] public string Kind { get; set; } = "info";
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// پیام درون‌برنامه‌ای. ThreadKey گفتگو را یکتا می‌کند:
/// «load:{loadId}:driver:{driverId}» پیش از قطعی شدن، «trip:{tripId}» پس از آن.
/// </summary>
public class Message
{
    public long MessageId { get; set; }
    [MaxLength(60)] public string ThreadKey { get; set; } = "";
    public int? LoadId { get; set; }
    public int? TripId { get; set; }
    [MaxLength(12)] public string FromKind { get; set; } = "";
    public int FromId { get; set; }
    [MaxLength(100)] public string FromName { get; set; } = "";
    [MaxLength(12)] public string ToKind { get; set; } = "";
    public int ToId { get; set; }
    public string Body { get; set; } = "";
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>اعلان/پیامک گروهی مدیر.</summary>
public class Broadcast
{
    public int BroadcastId { get; set; }
    /// <summary>all | drivers | shippers | companies</summary>
    [MaxLength(12)] public string Audience { get; set; } = "all";
    /// <summary>notification | sms</summary>
    [MaxLength(12)] public string Channel { get; set; } = "notification";
    [MaxLength(150)] public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int SentCount { get; set; }
    public int CreatedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SmsLog
{
    public long SmsLogId { get; set; }
    [MaxLength(11)] public string Mobile { get; set; } = "";
    public string Text { get; set; } = "";
    [MaxLength(20)] public string Purpose { get; set; } = "";
    public bool Success { get; set; }
    public string? Response { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
