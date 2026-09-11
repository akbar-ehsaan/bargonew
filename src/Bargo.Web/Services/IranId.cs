namespace Bargo.Web.Services;

/// <summary>اعتبارسنجی شناسه‌های ایرانی — کد ملی، شناسهٔ ملی شرکت، شبا.</summary>
public static class IranId
{
    /// <summary>کد ملی ۱۰ رقمی با رقم کنترل.</summary>
    public static bool IsNationalCode(string? input)
    {
        var s = Fa.Latin(input);
        if (s.Length != 10 || !s.All(char.IsAsciiDigit) || s.Distinct().Count() == 1) return false;
        var check = s[9] - '0';
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (s[i] - '0') * (10 - i);
        var r = sum % 11;
        return r < 2 ? check == r : check == 11 - r;
    }

    /// <summary>شناسهٔ ملی اشخاص حقوقی — ۱۱ رقم با رقم کنترل.</summary>
    public static bool IsCompanyNationalId(string? input)
    {
        var s = Fa.Latin(input);
        if (s.Length != 11 || !s.All(char.IsAsciiDigit)) return false;
        int[] coef = [29, 27, 23, 19, 17, 29, 27, 23, 19, 17];
        var d = (s[9] - '0') + 2;
        var sum = 0;
        for (var i = 0; i < 10; i++) sum += ((s[i] - '0') + d) * coef[i];
        var r = sum % 11;
        if (r == 10) r = 0;
        return r == s[10] - '0';
    }

    /// <summary>شبا: IR + ۲۴ رقم، با بررسی mod 97.</summary>
    public static bool IsSheba(string? input)
    {
        var s = Fa.Latin(input).Replace(" ", "").ToUpperInvariant();
        if (!s.StartsWith("IR")) s = "IR" + s;
        if (s.Length != 26 || !s[2..].All(char.IsAsciiDigit)) return false;
        var rearranged = s[4..] + "1827" + s[2..4]; // I=18, R=27
        var mod = 0;
        foreach (var ch in rearranged) mod = (mod * 10 + (ch - '0')) % 97;
        return mod == 1;
    }

    public static string NormSheba(string? input)
    {
        var s = Fa.Latin(input).Replace(" ", "").ToUpperInvariant();
        return s.StartsWith("IR") ? s : "IR" + s;
    }
}
