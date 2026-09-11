namespace Bargo.Web.Services;

/// <summary>
/// فاصله و زمان تقریبی رسیدن — بدون سرویس مسیریابی بیرونی (همان روش بارگوی فعلی):
/// فاصلهٔ هوایی Haversine ضرب در ضریب پیچش جاده.
/// </summary>
public static class Geo
{
    /// <summary>جادهٔ بین‌شهری ایران به‌طور میانگین حدود ۲۵٪ از خط مستقیم بلندتر است.</summary>
    public const double RoadFactor = 1.25;

    /// <summary>سرعت میانگین کامیون در محور بین‌شهری، با احتساب توقف.</summary>
    public const double TruckAvgKmh = 55;

    public static double Km(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double? Km(double? lat1, double? lng1, double? lat2, double? lng2) =>
        lat1 is null || lng1 is null || lat2 is null || lng2 is null ? null : Km(lat1.Value, lng1.Value, lat2.Value, lng2.Value);

    public static double? RoadKm(double? lat1, double? lng1, double? lat2, double? lng2) =>
        Km(lat1, lng1, lat2, lng2) is double km ? Math.Round(km * RoadFactor, 1) : null;

    public static DateTime EtaUtc(double remainingKm, double avgKmh = TruckAvgKmh) =>
        DateTime.UtcNow.AddHours(Math.Max(0, remainingKm) / Math.Max(10, avgKmh));
}
