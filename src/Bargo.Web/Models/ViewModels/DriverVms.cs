using System.Globalization;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  مدل‌های نمایشِ پنل راننده
//
//  ویوهای راننده به‌جای ViewBag و نوع بی‌نام، مدلِ نوع‌دار می‌گیرند: بیشترِ این
//  صفحه‌ها از چند پرس‌وجو ساخته می‌شوند و غلطِ املاییِ یک کلید ViewBag فقط در
//  زمان اجرا دیده می‌شد.
// ---------------------------------------------------------------------------

/// <summary>ماه شمسی با مرزهای UTC — برای گروه‌بندی درآمد و سوابق عملکرد.</summary>
public sealed record DriverMonth(int Year, int Month, DateTime FromUtc, DateTime ToUtc)
{
    private static readonly PersianCalendar Pc = new();
    private static readonly string[] Names =
        ["", "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];

    public string Label => $"{Names[Month]} {Fa.Digits(Year.ToString(CultureInfo.InvariantCulture))}";

    public static DriverMonth Make(int year, int month)
    {
        var from = Fa.ToUtc(Pc.ToDateTime(year, month, 1, 0, 0, 0, 0));
        var (ny, nm) = month == 12 ? (year + 1, 1) : (year, month + 1);
        var to = Fa.ToUtc(Pc.ToDateTime(ny, nm, 1, 0, 0, 0, 0));
        return new DriverMonth(year, month, from, to);
    }

    public static DriverMonth Of(DateTime utc)
    {
        var t = Fa.ToTehran(utc);
        return Make(Pc.GetYear(t), Pc.GetMonth(t));
    }

    /// <summary>n ماه اخیر، از ماه جاری به عقب.</summary>
    public static List<DriverMonth> Last(int n)
    {
        var cur = Of(DateTime.UtcNow);
        var (y, m) = (cur.Year, cur.Month);
        var list = new List<DriverMonth>(n);
        for (var i = 0; i < n; i++)
        {
            list.Add(Make(y, m));
            if (--m == 0) { m = 12; y--; }
        }
        return list;
    }
}

public sealed record CityOpt(int CityId, string Name, int ProvinceId);

// ============================================================ بار

/// <summary>کارت بار در بازار — هر فیلدی که روی کارت دیده می‌شود، و فقط همان.</summary>
public sealed class LoadCardVm
{
    public int LoadId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string CargoType { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int FromProvinceId { get; set; }
    public decimal WeightTon { get; set; }
    public int? VehicleTypeId { get; set; }
    public string? VehicleType { get; set; }
    public DateTime LoadingFrom { get; set; }
    public DateTime? LoadingTo { get; set; }
    public string PriceMode { get; set; } = "";
    public long? Price { get; set; }
    public double? DistanceKm { get; set; }
    public double? OriginLat { get; set; }
    public double? OriginLng { get; set; }
    public string Status { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public bool InsuranceRequested { get; set; }
    public int OfferCount { get; set; }
    public bool Saved { get; set; }
    public bool HasMyOffer { get; set; }

    /// <summary>فاصلهٔ هوایی از موقعیت راننده تا مبدا (در حافظه محاسبه می‌شود).</summary>
    public double? KmFromMe { get; set; }
    /// <summary>دلیل‌های پیشنهادِ این بار به راننده — «بارهای پیشنهادی».</summary>
    public List<string> Reasons { get; set; } = [];
    public int Score { get; set; }

    public bool IsFixed => PriceMode == Entities.PriceMode.Fixed && Price is not null;
    public bool InMarket => LoadStatus.Market.Contains(Status);
}

/// <summary>فیلترهای بازار بار — از کوئری‌استرینگ بایند می‌شود.</summary>
public sealed class LoadFilterVm
{
    public int? OriginProvinceId { get; set; }
    public int? OriginCityId { get; set; }
    public int? DestProvinceId { get; set; }
    public int? DestCityId { get; set; }
    public int? VehicleTypeId { get; set; }
    public decimal? MaxWeight { get; set; }
    public DateTime? FromDate { get; set; }
    /// <summary>fixed | negotiable</summary>
    public string? PriceMode { get; set; }
    /// <summary>new | loading | price</summary>
    public string? Sort { get; set; }

    public bool HasAny => OriginProvinceId is not null || OriginCityId is not null || DestProvinceId is not null ||
                          DestCityId is not null || VehicleTypeId is not null || MaxWeight is not null ||
                          FromDate is not null || !string.IsNullOrEmpty(PriceMode);
}

public sealed class LoadListVm
{
    /// <summary>all | search | nearby | suggested | saved</summary>
    public string Mode { get; set; } = "all";
    public PageVm<LoadCardVm> Page { get; set; } = new();
    public LoadFilterVm Filter { get; set; } = new();
    public List<Province> Provinces { get; set; } = [];
    public List<CityOpt> Cities { get; set; } = [];
    public List<VehicleType> VehicleTypes { get; set; } = [];
    public List<DriverReadiness.Issue> Blocking { get; set; } = [];

    public bool HasPosition { get; set; }
    /// <summary>موقعیت از GPS آمده (true) یا از مختصات شهر راننده (false).</summary>
    public bool LivePosition { get; set; }
    public int? Radius { get; set; }

    public string? VehicleLabel { get; set; }
    public decimal? VehicleCapacity { get; set; }
    public string? Note { get; set; }
}

public sealed class LoadDetailVm
{
    public Load Load { get; set; } = null!;
    public string OwnerName { get; set; } = "";
    public bool OwnerIsCompany { get; set; }
    public double OwnerRating { get; set; }
    public int OwnerRatingCount { get; set; }
    public int PendingOffers { get; set; }
    public bool Saved { get; set; }
    public List<Offer> MyOffers { get; set; } = [];
    public Dictionary<int, int> TripByOffer { get; set; } = new();
    public List<Vehicle> Vehicles { get; set; } = [];
    public List<DriverReadiness.Issue> Issues { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public double? KmFromMe { get; set; }

    /// <summary>همان شرط <see cref="Scopes.Market"/>: هنوز پیشنهاد می‌پذیرد، منقضی نشده و درخواستِ مستقیمِ شرکت نیست.</summary>
    public bool InMarket => LoadStatus.Market.Contains(Load.Status) && Load.TargetCompanyId is null &&
                            (Load.ExpiresAt is null || Load.ExpiresAt > DateTime.UtcNow);
    public bool IsFixed => Load.PriceMode == PriceMode.Fixed && Load.Price is not null;
    public bool Blocked => Issues.Any(i => i.Blocking);
    public Offer? PendingOffer => MyOffers.FirstOrDefault(o => o.Status == OfferStatus.Pending);
    public int? DefaultVehicleId => (PendingOffer?.VehicleId) ?? Vehicles.FirstOrDefault(v => v.Status == VehicleStatus.Active)?.VehicleId;
}

// ============================================================ پیشنهاد

public sealed class OfferRowVm
{
    public int OfferId { get; set; }
    public int LoadId { get; set; }
    public string LoadCode { get; set; } = "";
    public string Title { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string PriceMode { get; set; } = "";
    public long? LoadPrice { get; set; }
    public string LoadStatus { get; set; } = "";
    public long Amount { get; set; }
    public int? EtaHours { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = "";
    public string? Plate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public int? TripId { get; set; }
}

public sealed class OffersVm
{
    public string? Status { get; set; }
    public PageVm<OfferRowVm> Page { get; set; } = new();
    public int CountAll { get; set; }
    public int CountPending { get; set; }
    public int CountAccepted { get; set; }
    public int CountRejected { get; set; }
}

public sealed class OfferCreateVm
{
    public PageVm<LoadCardVm> Page { get; set; } = new();
    public List<Province> Provinces { get; set; } = [];
    public List<VehicleType> VehicleTypes { get; set; } = [];
    public int? OriginProvinceId { get; set; }
    public int? VehicleTypeId { get; set; }
    public List<DriverReadiness.Issue> Blocking { get; set; } = [];
}

// ============================================================ سفر

public sealed class TripRowVm
{
    public int TripId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Status { get; set; } = "";
    public string CarrierKind { get; set; } = "";
    public string? CompanyName { get; set; }
    public long Fare { get; set; }
    public long CarrierShare { get; set; }
    public long? DriverShare { get; set; }
    public string? Plate { get; set; }
    public double TravelledKm { get; set; }
    public double? PlannedKm { get; set; }
    public DateTime? ScheduledDepartureAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>سهم خودِ راننده: کل سهم حمل‌کننده برای رانندهٔ مستقل، سهم راننده برای سفر شرکتی.</summary>
    public long? MyShare => CarrierKind == Entities.CarrierKind.Company ? DriverShare : CarrierShare;
}

public sealed class TripsVm
{
    /// <summary>upcoming | live | done | cancelled</summary>
    public string Tab { get; set; } = "upcoming";
    public PageVm<TripRowVm> Page { get; set; } = new();
    public int CountUpcoming { get; set; }
    public int CountLive { get; set; }
    public int CountDone { get; set; }
    public int CountCancelled { get; set; }
}

public sealed class TripHistoryVm
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public PageVm<TripRowVm> Page { get; set; } = new();
    public int TotalTrips { get; set; }
    public int DoneTrips { get; set; }
    public double TotalKm { get; set; }
    public long TotalIncome { get; set; }
}

public sealed class TripDetailVm
{
    public Trip Trip { get; set; } = null!;
    public List<TripEvent> Events { get; set; } = [];
    public List<TripDocument> Documents { get; set; } = [];
    public List<double[]> Line { get; set; } = [];

    public string OwnerName { get; set; } = "";
    public string? OwnerMobile { get; set; }
    public string? OwnerPhone { get; set; }
    public bool OwnerIsCompany { get; set; }

    public IReadOnlyList<string> Next { get; set; } = [];
    public bool CanCancel { get; set; }
    public bool OtpRequired { get; set; }
    public bool WaybillRequired { get; set; }
    public bool CanRate { get; set; }
    public bool Rated { get; set; }
    public string TrackUrl { get; set; } = "";

    public bool IsCompanyTrip => Trip.CarrierKind == CarrierKind.Company;
    public long? MyShare => IsCompanyTrip ? Trip.DriverShare : Trip.CarrierShare;

    /// <summary>ثبت بارنامه از «حضور در مبدا» به بعد معنا دارد و پس از تسویه یا لغو بسته است.</summary>
    public bool CanRegisterWaybill => Trip.Status is TripStatus.Accepted or TripStatus.ToOrigin or TripStatus.AtOrigin or TripStatus.Loaded
        or TripStatus.InTransit or TripStatus.Arrived or TripStatus.Unloaded or TripStatus.Delivered;
    public bool ShowWaybillForm => Trip.Waybill is null
        ? Trip.Status is TripStatus.AtOrigin or TripStatus.Loaded or TripStatus.InTransit or TripStatus.Arrived or TripStatus.Unloaded or TripStatus.Delivered
        : false;
    public bool CanUploadDocs => Trip.Status is not (TripStatus.AwaitingAssignment or TripStatus.Assigned or TripStatus.Cancelled);
}

public sealed class OperationsVm
{
    public string? Step { get; set; }
    public TripDetailVm? Detail { get; set; }
    public int LiveCount { get; set; }
}

public sealed record TrackRowVm(DateTime RecordedAt, double Lat, double Lng, double? SpeedKmh, double? AccuracyM, string? TripCode);

public sealed class LocationVm
{
    public bool IsAvailable { get; set; }
    public double? LastLat { get; set; }
    public double? LastLng { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public int IntervalMin { get; set; }
    public string? LiveTripCode { get; set; }
    public int? LiveTripId { get; set; }
    public List<TrackRowVm> Recent { get; set; } = [];
    public int PointsToday { get; set; }
}

// ============================================================ بارنامه و اسناد

public sealed class WaybillRowVm
{
    public int WaybillId { get; set; }
    public string Number { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FilePath { get; set; }
    public DateTime IssuedAt { get; set; }
    public int TripId { get; set; }
    public string TripCode { get; set; } = "";
    public string TripStatus { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Title { get; set; } = "";

    public static string StatusLabel(string s) => s switch { "verified" => "تأییدشده", "void" => "باطل", _ => "ثبت‌شده" };
    public static string StatusTone(string s) => s switch { "verified" => "ok", "void" => "no", _ => "inf" };
}

public sealed class TripDocRowVm
{
    public int TripDocumentId { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? FilePath { get; set; }
    public string UploadedByKind { get; set; } = "";
    public int UploadedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TripId { get; set; }
    public string TripCode { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class TripDocsVm
{
    public string? Kind { get; set; }
    public PageVm<TripDocRowVm> Page { get; set; } = new();
}

public sealed class ReceiptRowVm
{
    public int TripId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ReceiverName { get; set; }
    public string? ReceiverMobile { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string? WaybillNo { get; set; }
    public List<TripDocRowVm> Receipts { get; set; } = [];
}

// ============================================================ مالی

public sealed class TxnRowVm
{
    public long WalletTransactionId { get; set; }
    public long Amount { get; set; }
    public long BalanceAfter { get; set; }
    public string Kind { get; set; } = "";
    public int? TripId { get; set; }
    public string? TripCode { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class WalletVm
{
    public long Balance { get; set; }
    /// <summary>برداشت‌های در صف (در انتظار یا تأییدشده ولی هنوز واریزنشده).</summary>
    public long Reserved { get; set; }
    public long Available => Math.Max(0, Balance - Reserved);
    public long MonthIncome { get; set; }
    public long TotalIncome { get; set; }
    public long MinPayout { get; set; }
    public string? Sheba { get; set; }
    public bool HasValidSheba { get; set; }
    public List<TxnRowVm> Recent { get; set; } = [];
    public List<PayoutRequest> OpenPayouts { get; set; } = [];
    public int UnpaidCompanyShares { get; set; }
}

public sealed record MonthIncomeRow(string Label, int Count, long Amount);

public sealed class EarningsVm
{
    public List<MonthIncomeRow> Months { get; set; } = [];
    public PageVm<TxnRowVm> Page { get; set; } = new();
    public long Total { get; set; }
    public long ThisMonth { get; set; }
    public int TripCount { get; set; }
}

public sealed class TransactionsVm
{
    public string? Kind { get; set; }
    public PageVm<TxnRowVm> Page { get; set; } = new();
    public List<string> Kinds { get; set; } = [];
}

/// <summary>صفحهٔ «تسویه‌حساب»: درخواست‌های برداشت من و جمع‌های بالای صفحه.</summary>
public sealed class SettlementsVm
{
    public PageVm<PayoutRequest> Page { get; set; } = new();
    public long Balance { get; set; }
    public long OpenSum { get; set; }
    public long PaidSum { get; set; }
    public long MinPayout { get; set; }
    public bool HasValidSheba { get; set; }
    public long Available => Math.Max(0, Balance - OpenSum);
}

public sealed class InvoiceRowVm
{
    public int InvoiceId { get; set; }
    public string No { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Amount { get; set; }
    public long Tax { get; set; }
    public long Total { get; set; }
    public string Status { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public int? TripId { get; set; }
    public string? TripCode { get; set; }

    public static string KindLabel(string k) => k switch
    {
        "commission" => "کمیسیون بارگو",
        "freight" => "کرایهٔ حمل",
        "subscription" => "اشتراک",
        _ => k
    };
}

// ============================================================ خودرو و مدارک

public sealed class VehicleFormVm
{
    public int? VehicleId { get; set; }
    public int VehicleTypeId { get; set; }
    public string? PlateNo { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public int? Year { get; set; }
    public decimal? CapacityTon { get; set; }
    public string? VehicleCardNo { get; set; }
    public string? InsurancePolicyNo { get; set; }
    public DateTime? InsuranceExpiresAt { get; set; }
    public DateTime? InspectionExpiresAt { get; set; }

    public static VehicleFormVm From(Vehicle v) => new()
    {
        VehicleId = v.VehicleId, VehicleTypeId = v.VehicleTypeId, PlateNo = v.PlateNo, Brand = v.Brand, Model = v.Model,
        Year = v.Year, CapacityTon = v.CapacityTon, VehicleCardNo = v.VehicleCardNo, InsurancePolicyNo = v.InsurancePolicyNo,
        InsuranceExpiresAt = v.InsuranceExpiresAt, InspectionExpiresAt = v.InspectionExpiresAt
    };
}

public sealed class VehicleRowVm
{
    public Vehicle Vehicle { get; set; } = null!;
    public int ApprovedDocs { get; set; }
    public int PendingDocs { get; set; }
    public bool InLiveTrip { get; set; }
}

public sealed class VehiclePageVm
{
    public List<VehicleRowVm> Vehicles { get; set; } = [];
    public VehicleFormVm Form { get; set; } = new();
    public List<VehicleType> Types { get; set; } = [];
    public string? CompanyName { get; set; }
    public int WarnDays { get; set; } = 30;
    public bool Editing => Form.VehicleId is not null;
}

public sealed class DocRowVm
{
    public Document Doc { get; set; } = null!;
    public string OwnerLabel { get; set; } = "";
    /// <summary>جدیدترین مدرکِ غیرردشدهٔ همین نوع برای همین صاحب — مبنای سنجش آمادگی.</summary>
    public bool IsCurrent { get; set; }
}

public sealed class DocumentsVm
{
    public string? Kind { get; set; }
    public int? VehicleId { get; set; }
    public List<DocRowVm> Rows { get; set; } = [];
    public List<Vehicle> Vehicles { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = new();
    public int WarnDays { get; set; } = 30;

    public static readonly string[] Kinds = [.. DocumentKind.ForDriver, .. DocumentKind.ForVehicle];
    public static bool IsVehicleKind(string? k) => k is not null && DocumentKind.ForVehicle.Contains(k);
    public static bool IsExpiringKind(string? k) => k is not null && DocumentKind.Expiring.Contains(k);
}

/// <summary>تاریخ انقضایی که روی خودِ پروفایل یا خودرو ثبت شده (نه روی مدرک بارگذاری‌شده).</summary>
public sealed record ExpiryFieldVm(string Label, DateTime? ExpiresAt, string Link);

public sealed class AlertsVm
{
    public List<DriverReadiness.Issue> Issues { get; set; } = [];
    public List<DocRowVm> Expired { get; set; } = [];
    public List<DocRowVm> Expiring { get; set; } = [];
    public List<ExpiryFieldVm> Fields { get; set; } = [];
    public int WarnDays { get; set; } = 30;
    public bool BlockOnExpired { get; set; }
    public bool AllClear => Issues.Count == 0 && Expired.Count == 0 && Expiring.Count == 0;
}

// ============================================================ امتیاز

public sealed class ReviewRowVm
{
    public int RatingId { get; set; }
    public int Score { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TripId { get; set; }
    public string TripCode { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string FromKind { get; set; } = "";
    public string? FromName { get; set; }
}

public sealed class RatingsVm
{
    public double Avg { get; set; }
    public int Count { get; set; }
    /// <summary>شمار امتیازهای ۱ تا ۵ — اندیس ۰ بی‌استفاده است.</summary>
    public int[] Dist { get; set; } = new int[6];
    public List<ReviewRowVm> Recent { get; set; } = [];
    public int WithComment { get; set; }
    public int GivenByMe { get; set; }
    public int AwaitingMyRating { get; set; }
}

/// <summary>صفحهٔ «نظرات صاحبان بار» — فهرست صفحه‌بندی‌شده با فیلتر امتیاز.</summary>
public sealed class ReviewsVm
{
    public int? Score { get; set; }
    public PageVm<ReviewRowVm> Page { get; set; } = new();
    public int Count { get; set; }
    public int[] Dist { get; set; } = new int[6];
}

public sealed record PerfMonthRow(string Label, int Delivered, int Cancelled, double Km, long Income, double? RatingAvg, int Ratings);

public sealed class PerformanceVm
{
    public int Done { get; set; }
    public int Cancelled { get; set; }
    public int Live { get; set; }
    public double Km { get; set; }
    public long Income { get; set; }
    public int OffersTotal { get; set; }
    public int OffersAccepted { get; set; }
    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }
    public int Complaints { get; set; }
    public List<PerfMonthRow> Months { get; set; } = [];
    public DateTime? MemberSince { get; set; }
}

// ============================================================ حساب

public sealed class ProfileFormVm
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? NationalCode { get; set; }
    public DateTime? BirthDate { get; set; }
    public int? CityId { get; set; }
    public string? Address { get; set; }
    public string? LicenseNo { get; set; }
    public DateTime? LicenseExpiresAt { get; set; }
    public string? SmartCardNo { get; set; }
    public DateTime? SmartCardExpiresAt { get; set; }
    public string? Sheba { get; set; }
}

public sealed class ProfileVm
{
    public Driver Driver { get; set; } = null!;
    public ProfileFormVm Form { get; set; } = new();
    public List<CityOpt> Cities { get; set; } = [];
    public List<Province> Provinces { get; set; } = [];
    public string? CompanyName { get; set; }
    public int IntervalMin { get; set; }
    public bool IdentityLocked => Driver.Status == AccountStatus.Approved;
}

// ============================================================ داشبورد

public sealed class DriverDashboardVm
{
    public Driver Driver { get; set; } = null!;
    public string? Gate { get; set; }
    public List<Stat> Stats { get; set; } = [];
    public List<DriverReadiness.Issue> Issues { get; set; } = [];
    public List<TodoItem> Todos { get; set; } = [];
    public Trip? CurrentTrip { get; set; }
    public IReadOnlyList<string> CurrentNext { get; set; } = [];
    public List<LoadCardVm> Nearby { get; set; } = [];
    public bool HasPosition { get; set; }
    public bool Approved => Driver.Status == AccountStatus.Approved;
}
