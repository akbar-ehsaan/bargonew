using Bargo.Web.Models.Entities;
using Bargo.Web.Services;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  پنل شرکت حمل‌ونقل — بخش عملیات (داشبورد، بار، راننده، ناوگان، تخصیص، سفر،
//  رهگیری، بارنامه). همهٔ نام‌ها پیشوند Ops دارند تا با مدل‌های بخش مالی/مشتریان
//  همان پنل برخورد نکنند.
//
//  فیلدهای عددیِ فرم (وزن، ظرفیت، سال) رشته‌اند نه decimal: کاربر ایرانی «۲۲» را با
//  ارقام فارسی تایپ می‌کند و بایندر پیش‌فرض آن را نمی‌خواند؛ تبدیل در کنترلر با
//  Fa.Latin انجام می‌شود. رشته‌ها nullable‌اند تا [Required] ضمنیِ انگلیسی ساخته نشود.
// ---------------------------------------------------------------------------

public sealed record OpsOption(int Id, string Label);
public sealed record OpsCityOption(int CityId, string Name, string Province);

/// <summary>سفری که یک راننده یا خودرو را درگیر کرده است.</summary>
public sealed record OpsBusy(int TripId, string Code, string Status, string? Driver = null)
{
    /// <summary>راننده/خودرو واقعاً در راه است یا مأموریت را پذیرفته (نه فقط «در انتظار قبول»).</summary>
    public bool Hard => Status != TripStatus.Assigned;
}

/// <summary>یک نشانگر نقشه — همان قالبی که wwwroot/js/map.js می‌خواند.</summary>
public sealed record OpsMarker(double Lat, double Lng, string Kind, string Label, string? Href);

/// <summary>مدرک (یا تاریخ ثبت‌شده روی خودرو/راننده) که منقضی شده یا رو به انقضاست.</summary>
public sealed record OpsExpiryAlert(string SubjectKind, int SubjectId, string Subject, string Kind, DateTime ExpiresAt,
    bool FromDocument, string? DocStatus, string Href)
{
    public bool IsExpired => ExpiresAt < DateTime.UtcNow;
    public string KindLabel => DocumentKind.Label(Kind);
    public string SubjectLabel => SubjectKind == OwnerKind.Driver ? "راننده" : "خودرو";
}

public static class OpsLabels
{
    /// <summary>تناژ بدون اعشارِ زائد: «۲۴» و «۲.۵».</summary>
    public static string Ton(decimal v) => Fa.N(v, v % 1 == 0 ? 0 : 1);

    public static string Hours(int? h) => h is int v ? $"{Fa.N(v)} ساعت" : "—";

    public static string WaybillStatus(string s) => s switch { "verified" => "تأییدشده", "void" => "باطل", _ => "ثبت‌شده" };
    public static string WaybillTone(string s) => s switch { "verified" => "ok", "void" => "no", _ => "inf" };

    public static bool IsOnline(DateTime? seen) => seen is DateTime s && s > DateTime.UtcNow.AddMinutes(-30);

    public static string LiveKind(bool inLiveTrip, DateTime? seen) =>
        inLiveTrip ? "truck" : seen is DateTime s && s > DateTime.UtcNow.AddHours(-2) ? "idle" : "stale";

    /// <summary>«آماده / در سفر / غیرفعال» برای یک رانندهٔ شرکت.</summary>
    public static (string Label, string Tone) Availability(Driver d, OpsBusy? busy) =>
        d.Status != AccountStatus.Approved ? (AccountStatus.Label(d.Status), AccountStatus.Tone(d.Status))
        : busy is { Hard: true } ? ($"در سفر {busy.Code}", "inf")
        : busy is not null ? ("در انتظار قبول مأموریت", "wait")
        : d.IsAvailable ? ("آماده بار", "ok")
        : ("خارج از خدمت", "mut");
}

// ============================ داشبورد ============================

public sealed class OpsDashboardVm
{
    public string CompanyName { get; set; } = "";
    public string CompanyStatus { get; set; } = AccountStatus.Pending;
    public string? StatusReason { get; set; }
    public string? Gate { get; set; }
    public bool CanLoads { get; set; }
    public bool CanDispatch { get; set; }
    public bool CanDrivers { get; set; }
    public bool CanFleet { get; set; }
    public bool CanFinance { get; set; }

    public List<Stat> Stats { get; set; } = [];
    public List<Trip> Queue { get; set; } = [];
    public int QueueTotal { get; set; }
    public List<Trip> Live { get; set; } = [];
    public int LiveTotal { get; set; }
    public int DirectRequests { get; set; }
    public int PendingOffers { get; set; }
    public int MarketLoads { get; set; }
    public int PendingDrivers { get; set; }
    public List<OpsExpiryAlert> Alerts { get; set; } = [];
}

// ============================ بار ============================

public sealed class OpsLoadForm
{
    /// <summary>own = حمل با ناوگان خود شرکت | market = انتشار در بازار بارگو</summary>
    public string? Mode { get; set; } = "own";

    public int? CustomerId { get; set; }
    public string? NewCustomerName { get; set; }
    public string? NewCustomerMobile { get; set; }
    public string? NewCustomerKind { get; set; } = "corporate";

    public string? Title { get; set; }
    public string? CargoType { get; set; }
    public string? Packaging { get; set; }

    public int? OriginCityId { get; set; }
    public string? OriginAddress { get; set; }
    public int? DestCityId { get; set; }
    public string? DestAddress { get; set; }

    public DateTime? LoadingDate { get; set; }
    public int LoadingHour { get; set; } = 8;

    public int? VehicleTypeId { get; set; }
    public string? WeightTon { get; set; }
    public string? VolumeM3 { get; set; }

    public string? PriceMode { get; set; } = Entities.PriceMode.Negotiable;
    public string? PriceToman { get; set; }
    public string? DeclaredValueToman { get; set; }
    public bool InsuranceRequested { get; set; }

    public string? ReceiverName { get; set; }
    public string? ReceiverMobile { get; set; }
    public string? Description { get; set; }
}

/// <summary>کارت بار در بازار و درخواست‌های مستقیم.</summary>
public sealed class OpsLoadCard
{
    public int LoadId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string CargoType { get; set; } = "";
    public string Origin { get; set; } = "";
    public string OriginProvince { get; set; } = "";
    public string Dest { get; set; } = "";
    public string DestProvince { get; set; } = "";
    public decimal WeightTon { get; set; }
    public string? VehicleType { get; set; }
    public DateTime LoadingFrom { get; set; }
    public string PriceMode { get; set; } = "";
    public long? Price { get; set; }
    public double? DistanceKm { get; set; }
    public string OwnerName { get; set; } = "";
    public double OwnerRating { get; set; }
    public int OwnerRatingCount { get; set; }
    public int PendingOffers { get; set; }
    public long? MyOfferAmount { get; set; }
    public bool InsuranceRequested { get; set; }
    public bool IsDirect { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public sealed class OpsLoadTripsVm
{
    public string Tab { get; set; } = "unassigned";
    public Dictionary<string, int> Counts { get; set; } = [];
    public PageVm<Trip>? Trips { get; set; }
    public PageVm<Load>? Loads { get; set; }
    public Dictionary<int, CompanyCustomer> Customers { get; set; } = [];
    public Dictionary<int, int> OfferCounts { get; set; } = [];
}

public sealed class OpsIncomingVm
{
    public List<OpsLoadCard> Requests { get; set; } = [];
    public PageVm<Offer> Offers { get; set; } = new();
    public string StatusFilter { get; set; } = OfferStatus.Pending;
    public Dictionary<string, int> OfferCounts { get; set; } = [];
    public Dictionary<int, int> TripOfOffer { get; set; } = [];
}

public sealed class OpsMarketVm
{
    public PageVm<OpsLoadCard> Page { get; set; } = new();
    public int? Origin { get; set; }
    public int? Dest { get; set; }
    public int? VehicleTypeId { get; set; }
    public DateTime? Date { get; set; }
    public List<Province> Provinces { get; set; } = [];
    public List<VehicleType> VehicleTypes { get; set; } = [];
    public int DirectCount { get; set; }
}

public sealed class OpsLoadDetailVm
{
    public Load Load { get; set; } = null!;
    public bool IsOwn { get; set; }
    public bool CanOffer { get; set; }
    public Offer? MyPendingOffer { get; set; }
    public List<Offer> MyOffers { get; set; } = [];
    public List<Offer> Offers { get; set; } = [];
    public List<Trip> Trips { get; set; } = [];
    public CompanyCustomer? Customer { get; set; }
    public List<Vehicle> Vehicles { get; set; } = [];
    public decimal CommissionPercent { get; set; }
    public bool CanDispatch { get; set; }
}

// ============================ رانندگان ============================

public sealed class OpsDriverForm
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Mobile { get; set; }
    public string? NationalCode { get; set; }
    public string? LicenseNo { get; set; }
    public DateTime? LicenseExpiresAt { get; set; }
    public string? SmartCardNo { get; set; }
    public DateTime? SmartCardExpiresAt { get; set; }
    public string? Password { get; set; }
    public int? CityId { get; set; }
}

public sealed class OpsDriverListVm
{
    public PageVm<Driver> Page { get; set; } = new();
    public Dictionary<int, OpsBusy> Busy { get; set; } = [];
    public Dictionary<int, List<DriverReadiness.Issue>> Issues { get; set; } = [];
    public string? Q { get; set; }
    public string? Status { get; set; }
    public Dictionary<string, int> Counts { get; set; } = [];
}

public sealed class OpsDriverDetailVm
{
    public Driver Driver { get; set; } = null!;
    public List<Document> Documents { get; set; } = [];
    public List<Trip> Trips { get; set; } = [];
    public int TripTotal { get; set; }
    public OpsBusy? Current { get; set; }
    public Trip? CurrentTrip { get; set; }
    public List<DriverReadiness.Issue> Issues { get; set; } = [];
    public List<Rating> Reviews { get; set; } = [];
    public int WarnDays { get; set; } = 30;
}

/// <summary>مدارک رانندگان یا خودروهای شرکت، با فرم بارگذاری.</summary>
public sealed class OpsDocumentsVm
{
    public PageVm<Document> Page { get; set; } = new();
    public Dictionary<int, string> Owners { get; set; } = [];
    public List<OpsOption> OwnerOptions { get; set; } = [];
    public int? OwnerId { get; set; }
    public string? Kind { get; set; }
    public string[] Kinds { get; set; } = [];
    public int WarnDays { get; set; } = 30;
}

public sealed class OpsRatingsVm
{
    public List<Driver> Drivers { get; set; } = [];
    public PageVm<Rating> Reviews { get; set; } = new();
    public Dictionary<int, string> DriverNames { get; set; } = [];
    /// <summary>فیلتر: فقط نظرات یک راننده.</summary>
    public int? DriverId { get; set; }
}

public sealed class OpsDriverTripsVm
{
    public PageVm<Trip> Page { get; set; } = new();
    public List<OpsOption> Drivers { get; set; } = [];
    public int? DriverId { get; set; }
}

public sealed class OpsOnlineVm
{
    public List<Driver> Drivers { get; set; } = [];
    public Dictionary<int, OpsBusy> Busy { get; set; } = [];
    public List<OpsMarker> Markers { get; set; } = [];
    public int Total { get; set; }
}

// ============================ ناوگان ============================

public sealed class OpsVehicleForm
{
    public int? VehicleId { get; set; }
    public int? VehicleTypeId { get; set; }
    public string? PlateNo { get; set; }
    public string? Brand { get; set; }
    public string? ModelName { get; set; }
    public string? Year { get; set; }
    public string? CapacityTon { get; set; }
    public string? VehicleCardNo { get; set; }
    public string? InsurancePolicyNo { get; set; }
    public DateTime? InsuranceExpiresAt { get; set; }
    public DateTime? InspectionExpiresAt { get; set; }

    // فقط برای نمایش در ویرایش
    public string? VerifyStatus { get; set; }
    public string? Status { get; set; }
}

public sealed class OpsFleetListVm
{
    public PageVm<Vehicle> Page { get; set; } = new();
    public Dictionary<int, OpsBusy> Busy { get; set; } = [];
    public string? Q { get; set; }
    public string? Status { get; set; }
    public int WarnDays { get; set; } = 30;
    public Dictionary<string, int> Counts { get; set; } = [];
    /// <summary>فیلتر نوع خودرو (از صفحهٔ «نوع خودرو»).</summary>
    public int? TypeId { get; set; }
    public List<VehicleType> Types { get; set; } = [];
}

public sealed record OpsTypeRow(VehicleType Type, int Total, int Active, decimal Capacity);

public sealed class OpsAlertsVm
{
    public List<OpsExpiryAlert> Alerts { get; set; } = [];
    public int WarnDays { get; set; } = 30;
    public bool BlockOnExpired { get; set; }
}

// ============================ تخصیص ============================

public sealed class OpsDriverOption
{
    public int DriverId { get; set; }
    public string Name { get; set; } = "";
    public string Mobile { get; set; } = "";
    public bool IsAvailable { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public OpsBusy? Busy { get; set; }
    public string? Blocking { get; set; }
    public string? Warning { get; set; }
    public double? DistanceKm { get; set; }
    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }
    public int TripCount { get; set; }

    /// <summary>در سفرِ دیگری است که آن را پذیرفته یا در راهش است — تخصیص ممکن نیست.</summary>
    public bool BusyElsewhere(int tripId) => Busy is { Hard: true } b && b.TripId != tripId;

    public bool Selectable(int tripId) => Blocking is null && !BusyElsewhere(tripId);

    public string LabelFor(int tripId)
    {
        var parts = new List<string> { Name };
        if (Blocking is not null) parts.Add("مسدود: " + Blocking);
        else if (BusyElsewhere(tripId)) parts.Add($"در سفر {Busy!.Code}");
        else
        {
            if (Busy is { } b && b.TripId != tripId) parts.Add($"منتظر قبول سفر {b.Code}");
            parts.Add(IsAvailable ? "آماده" : "خارج از خدمت");
            if (DistanceKm is double km) parts.Add($"{Fa.N(km)} کیلومتر تا مبدا");
            if (Warning is not null) parts.Add("هشدار: " + Warning);
        }
        return string.Join(" — ", parts);
    }
}

public sealed class OpsVehicleOption
{
    public int VehicleId { get; set; }
    public string Plate { get; set; } = "";
    public string TypeName { get; set; } = "";
    public int VehicleTypeId { get; set; }
    public decimal CapacityTon { get; set; }
    public OpsBusy? Busy { get; set; }
    public string? Warning { get; set; }

    public bool BusyElsewhere(int tripId) => Busy is { Hard: true } b && b.TripId != tripId;

    public string LabelFor(int tripId, decimal weightTon)
    {
        var parts = new List<string> { $"{Plate} · {TypeName} · {Fa.N(CapacityTon, 1)} تن" };
        if (BusyElsewhere(tripId)) parts.Add($"در سفر {Busy!.Code}");
        else if (Busy is { } b && b.TripId != tripId) parts.Add($"رزرو سفر {b.Code}");
        if (CapacityTon < weightTon) parts.Add("ظرفیت کمتر از وزن بار");
        if (Warning is not null) parts.Add(Warning);
        return string.Join(" — ", parts);
    }
}

public sealed class OpsAssignFormVm
{
    public Trip Trip { get; set; } = null!;
    public List<OpsDriverOption> Drivers { get; set; } = [];
    public List<OpsVehicleOption> Vehicles { get; set; } = [];
    /// <summary>index | assign | change | trip — بازگشت پس از ثبت.</summary>
    public string Back { get; set; } = "index";
    public bool FocusVehicle { get; set; }
    public int? SelectedDriverId { get; set; }
    public bool Compact { get; set; }
    public bool RequireReason => Trip.DriverId is not null;
}

public sealed class OpsDispatchVm
{
    public PageVm<Trip> Page { get; set; } = new();
    public List<OpsDriverOption> Drivers { get; set; } = [];
    public List<OpsVehicleOption> Vehicles { get; set; } = [];
    /// <summary>راننده‌ای که مأموریت را رد کرد (آخرین ردیف تخصیص از نوع «رد»).</summary>
    public Dictionary<int, TripAssignment> Rejections { get; set; } = [];
    public Dictionary<int, DateTime> AssignedAt { get; set; } = [];
    public bool FocusVehicle { get; set; }
}

public sealed class OpsAssignVm
{
    public OpsAssignFormVm Form { get; set; } = new();
    public List<OpsDriverOption> Suggestions { get; set; } = [];
    public List<TripAssignment> History { get; set; } = [];
    public TripAssignment? Rejection { get; set; }
    public CompanyCustomer? Customer { get; set; }
}

public sealed class OpsChangeVm
{
    public PageVm<Trip> Page { get; set; } = new();
    public List<OpsDriverOption> Drivers { get; set; } = [];
    public List<OpsVehicleOption> Vehicles { get; set; } = [];
    public Dictionary<int, List<TripAssignment>> History { get; set; } = [];
    public bool FocusVehicle { get; set; }
}

public sealed record OpsScheduleDay(string Title, string? Sub, string Tone, List<Trip> Trips);

public sealed class OpsScheduleVm
{
    public List<OpsScheduleDay> Days { get; set; } = [];
    public int Total { get; set; }
    public int Unassigned { get; set; }
    public int Days14 { get; set; } = 14;
}

// ============================ سفرها ============================

public sealed class OpsTripsVm
{
    public string Tab { get; set; } = "planned";
    public Dictionary<string, int> Counts { get; set; } = [];
    public PageVm<Trip> Page { get; set; } = new();
    public int CompanyId { get; set; }
}

public sealed class OpsTripDetailVm
{
    public Trip Trip { get; set; } = null!;
    public bool IsCarrier { get; set; }
    public bool IsLoadOwner { get; set; }
    public List<string> Steps { get; set; } = [];
    public bool CanCancel { get; set; }
    public bool DeliveryOtp { get; set; }
    public bool RequireWaybill { get; set; }
    public List<TripEvent> Events { get; set; } = [];
    public List<TripAssignment> Assignments { get; set; } = [];
    public List<TripDocument> Documents { get; set; } = [];
    public List<double[]> Line { get; set; } = [];
    public List<OpsMarker> Markers { get; set; } = [];
    public CompanyCustomer? Customer { get; set; }
    public long Expenses { get; set; }
    public bool CanWaybill { get; set; }
}

// ============================ رهگیری ============================

public sealed class OpsMonitorVm
{
    public string? Layer { get; set; }
    public List<OpsMarker> Markers { get; set; } = [];
    public List<Trip> LiveTrips { get; set; } = [];
    public int WithPosition { get; set; }
}

public sealed class OpsRouteVm
{
    public List<OpsOption> TripOptions { get; set; } = [];
    public Trip? Trip { get; set; }
    public List<double[]> Line { get; set; } = [];
    public List<OpsMarker> Markers { get; set; } = [];
    public int PointCount { get; set; }
    public double? MaxSpeed { get; set; }
    public DateTime? FirstPoint { get; set; }
    public DateTime? LastPoint { get; set; }
}

public sealed record OpsBoardColumn(string Status, List<Trip> Trips);

public sealed class OpsHistoryVm
{
    public List<OpsOption> Drivers { get; set; } = [];
    public List<OpsOption> Vehicles { get; set; } = [];
    public int? DriverId { get; set; }
    public int? VehicleId { get; set; }
    public DateTime Date { get; set; }
    public PageVm<TrackingPoint>? Page { get; set; }
    public List<double[]> Line { get; set; } = [];
    public List<OpsMarker> Markers { get; set; } = [];
    public int Count { get; set; }
    public double Km { get; set; }
    public double? MaxSpeed { get; set; }
    public DateTime? First { get; set; }
    public DateTime? Last { get; set; }
    public Dictionary<int, string> TripCodes { get; set; } = [];
}

// ============================ بارنامه و اسناد ============================

public sealed class OpsWaybillIssueVm
{
    public List<Trip> Trips { get; set; } = [];
    public bool RequireWaybill { get; set; }
    public int Registered { get; set; }
}

public sealed class OpsWaybillsVm
{
    public PageVm<Waybill> Page { get; set; } = new();
    public string? Q { get; set; }
}

public sealed class OpsTripDocsVm
{
    public PageVm<TripDocument> Page { get; set; } = new();
    public string? Kind { get; set; }
    public List<OpsOption> Trips { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
}
