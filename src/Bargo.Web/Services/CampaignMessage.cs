namespace Bargo.Web.Services;

/// <summary>
/// جای‌نشان‌های متن کمپین (آورده‌شده از کارکور).
///
/// «{نام} گرامی…» برای گیرنده یعنی این پیام مال اوست، نه بمباران انبوه.
/// جایگزینی هنگام ارسال و برای هر گیرنده جدا انجام می‌شود؛ متن ذخیره‌شدهٔ
/// کمپین همان قالب خام می‌ماند تا معلوم باشد چه نوشته شده بود.
/// </summary>
public static class CampaignMessage
{
    public const string TokenName = "{نام}";
    public const string TokenCity = "{شهر}";
    public const string TokenProvince = "{استان}";

    public static readonly IReadOnlyList<(string Token, string Label)> Tokens =
    [
        (TokenName, "نام گیرنده"),
        (TokenCity, "شهر"),
        (TokenProvince, "استان"),
    ];

    public static bool HasTokens(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (text.Contains(TokenName, StringComparison.Ordinal) ||
         text.Contains(TokenCity, StringComparison.Ordinal) ||
         text.Contains(TokenProvince, StringComparison.Ordinal));

    /// <summary>
    /// جایگزینی جای‌نشان‌ها برای یک گیرنده. جای‌نشانِ بی‌مقدار حذف می‌شود و
    /// فاصلهٔ دوتاییِ به‌جامانده جمع می‌شود — «سلامِ  گرامی» نصف اعتماد را می‌برد.
    /// </summary>
    public static string Render(string? text, string? name, string? city, string? province)
    {
        var s = text ?? "";
        if (s.Length == 0) return s;

        s = s.Replace(TokenName, (name ?? "").Trim(), StringComparison.Ordinal)
             .Replace(TokenCity, (city ?? "").Trim(), StringComparison.Ordinal)
             .Replace(TokenProvince, (province ?? "").Trim(), StringComparison.Ordinal);

        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);

        return s.Trim();
    }
}
