using System.Text.Encodings.Web;
using Bargo.Web.Models.Entities;
using Microsoft.AspNetCore.Html;

namespace Bargo.Web.Services;

/// <summary>تکه‌های کوچک و پرتکرار مارک‌آپ — تا برچسب و رنگ وضعیت‌ها در چهار پنل یکی بماند.</summary>
public static class Ui
{
    public static IHtmlContent Pill(string label, string tone) =>
        new HtmlString($"<span class=\"pill {tone}\">{HtmlEncoder.Default.Encode(label)}</span>");

    public static IHtmlContent LoadPill(string status) => Pill(LoadStatus.Label(status), LoadStatus.Tone(status));
    public static IHtmlContent TripPill(string status) => Pill(TripStatus.Label(status), TripStatus.Tone(status));
    public static IHtmlContent OfferPill(string status) => Pill(OfferStatus.Label(status), OfferStatus.Tone(status));
    public static IHtmlContent AccountPill(string status) => Pill(AccountStatus.Label(status), AccountStatus.Tone(status));
    public static IHtmlContent PaymentPill(string status) => Pill(PaymentStatus.Label(status), PaymentStatus.Tone(status));
    public static IHtmlContent PayoutPill(string status) => Pill(PayoutStatus.Label(status), PayoutStatus.Tone(status));
    public static IHtmlContent TicketPill(string status) => Pill(TicketStatus.Label(status), TicketStatus.Tone(status));
    public static IHtmlContent ComplaintPill(string status) => Pill(ComplaintStatus.Label(status), ComplaintStatus.Tone(status));
    public static IHtmlContent VehiclePill(string status) => Pill(VehicleStatus.Label(status), VehicleStatus.Tone(status));

    /// <summary>ستاره‌های امتیاز: «★ ۴٫۶ (۲۳)»</summary>
    public static IHtmlContent Stars(double avg, int count) =>
        count == 0
            ? new HtmlString("<span class=\"muted\">بدون امتیاز</span>")
            : new HtmlString($"<span class=\"stars\"><i class=\"bi bi-star-fill\"></i> {Fa.N(avg, 1)} <small>({Fa.N(count)})</small></span>");

    /// <summary>مسیر «تهران ← اصفهان»</summary>
    public static IHtmlContent Route(string? from, string? to) =>
        new HtmlString($"<span class=\"route\"><b>{HtmlEncoder.Default.Encode(from ?? "—")}</b><i class=\"bi bi-arrow-left\"></i><b>{HtmlEncoder.Default.Encode(to ?? "—")}</b></span>");

    /// <summary>مدرک با تاریخ انقضا: سبز / کهربایی (نزدیک انقضا) / قرمز (منقضی).</summary>
    public static IHtmlContent Expiry(DateTime? utc, int warnDays = 30)
    {
        if (utc is null) return new HtmlString("<span class=\"muted\">—</span>");
        var tone = utc.Value < DateTime.UtcNow ? "no" : utc.Value < DateTime.UtcNow.AddDays(warnDays) ? "wait" : "ok";
        return Pill(Fa.Date(utc), tone);
    }
}
