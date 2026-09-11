namespace Bargo.Web.Models.ViewModels;

/// <summary>یک کاشی آمار داشبورد.</summary>
public class Stat
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Unit { get; set; }
    public string Icon { get; set; } = "bi-graph-up";
    /// <summary>p / t / i خنثی (هم‌خانوادهٔ پوسته) — s / w / r معنادار (انجام‌شده / منتظر اقدام / مشکل)</summary>
    public string Kind { get; set; } = "p";
    public string? Href { get; set; }
}

public class DashboardVm
{
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
    public List<Stat> Stats { get; set; } = [];
    /// <summary>پیام بالای داشبورد (مثلاً «حساب شما در انتظار تأیید است»).</summary>
    public string? Note { get; set; }
}

/// <summary>یک ردیف کار در انتظار روی داشبورد: «۳ مدرک در انتظار بررسی».</summary>
public sealed record TodoItem(string Text, string Href, string Icon, string Tone = "wait");
