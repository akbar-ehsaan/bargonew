using System.Globalization;
using System.Linq.Expressions;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  مدل‌های نمایشیِ پنل صاحب بار (Areas/Shipper)
//
//  ردیف‌های فهرست (ShipperLoadRow، ShipperTripRow) با Expression ساخته می‌شوند تا
//  همهٔ صفحه‌ها (داشبورد، بارهای من، سفارش‌ها، رهگیری، مالی) یک پرس‌وجوی یکسان
//  داشته باشند و فقط ستون‌های لازم از پایگاه‌داده خوانده شود.
// ---------------------------------------------------------------------------

/// <summary>یک شهر در فهرست کشویی با گروه‌بندی استان.</summary>
public sealed class ShipperCityOption
{
    public int CityId { get; init; }
    public string Name { get; init; } = "";
    public string Province { get; init; } = "";
    public double? Lat { get; init; }
    public double? Lng { get; init; }
}

/// <summary>ردیف بار در فهرست‌ها.</summary>
public sealed class ShipperLoadRow
{
    public int LoadId { get; init; }
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string CargoType { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public decimal WeightTon { get; init; }
    public string? VehicleType { get; init; }
    public DateTime LoadingFrom { get; init; }
    public string Status { get; init; } = "";
    public string PriceMode { get; init; } = "";
    public long? Price { get; init; }
    public int PendingOffers { get; init; }
    public long? LowestOffer { get; init; }
    public int? TripId { get; init; }
    public string? CurrentTripStatus { get; init; }
    public string? TargetCompany { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? PublishedAt { get; init; }

    public static readonly Expression<Func<Load, ShipperLoadRow>> FromLoad = l => new ShipperLoadRow
    {
        LoadId = l.LoadId,
        Code = l.Code,
        Title = l.Title,
        CargoType = l.CargoType,
        From = l.OriginCity!.Name,
        To = l.DestCity!.Name,
        WeightTon = l.WeightTon,
        VehicleType = l.VehicleType != null ? l.VehicleType.Name : null,
        LoadingFrom = l.LoadingFrom,
        Status = l.Status,
        PriceMode = l.PriceMode,
        Price = l.Price,
        PendingOffers = l.Offers.Count(o => o.Status == OfferStatus.Pending),
        LowestOffer = l.Offers.Where(o => o.Status == OfferStatus.Pending).Min(o => (long?)o.Amount),
        TripId = l.Trips.Where(t => t.Status != TripStatus.Cancelled).OrderByDescending(t => t.TripId).Select(t => (int?)t.TripId).FirstOrDefault(),
        CurrentTripStatus = l.Trips.Where(t => t.Status != TripStatus.Cancelled).OrderByDescending(t => t.TripId).Select(t => t.Status).FirstOrDefault(),
        TargetCompany = l.TargetCompany != null ? l.TargetCompany.Name : null,
        CreatedAt = l.CreatedAt,
        PublishedAt = l.PublishedAt
    };
}

/// <summary>ردیف سفر (سفارش) در فهرست‌ها.</summary>
public sealed class ShipperTripRow
{
    public int TripId { get; init; }
    public string Code { get; init; } = "";
    public int LoadId { get; init; }
    public string LoadCode { get; init; } = "";
    public string LoadTitle { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string CarrierKind { get; init; } = "";
    public int? DriverId { get; init; }
    public string? DriverName { get; init; }
    public string? CompanyName { get; init; }
    public string? PlateNo { get; init; }
    public string Status { get; init; } = "";
    public long Fare { get; init; }
    public bool IsPaid { get; init; }
    public DateTime? EtaAt { get; init; }
    public DateTime? LastPointAt { get; init; }
    public DateTime? ScheduledDepartureAt { get; init; }
    public DateTime? DeliveredAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? CancelReason { get; init; }
    public double TravelledKm { get; init; }
    public double? PlannedKm { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>«شرکت · راننده» یا فقط یکی از آن دو.</summary>
    public string Carrier =>
        !string.IsNullOrWhiteSpace(CompanyName)
            ? (string.IsNullOrWhiteSpace(DriverName) ? CompanyName! : $"{CompanyName} · {DriverName}")
            : string.IsNullOrWhiteSpace(DriverName) ? "—" : DriverName!;

    public static readonly Expression<Func<Trip, ShipperTripRow>> FromTrip = t => new ShipperTripRow
    {
        TripId = t.TripId,
        Code = t.Code,
        LoadId = t.LoadId,
        LoadCode = t.Load!.Code,
        LoadTitle = t.Load.Title,
        From = t.Load.OriginCity!.Name,
        To = t.Load.DestCity!.Name,
        CarrierKind = t.CarrierKind,
        DriverId = t.DriverId,
        DriverName = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null,
        CompanyName = t.Company != null ? t.Company.Name : null,
        PlateNo = t.Vehicle != null ? t.Vehicle.PlateNo : null,
        Status = t.Status,
        Fare = t.Fare,
        IsPaid = t.IsPaid,
        EtaAt = t.EtaAt,
        LastPointAt = t.LastPointAt,
        ScheduledDepartureAt = t.ScheduledDepartureAt,
        DeliveredAt = t.DeliveredAt,
        CancelledAt = t.CancelledAt,
        CancelReason = t.CancelReason,
        TravelledKm = t.TravelledKm,
        PlannedKm = t.PlannedKm,
        CreatedAt = t.CreatedAt
    };
}

// ------------------------------------------------------------------ داشبورد

public sealed class ShipperDashboardVm : DashboardVm
{
    public string AccountState { get; set; } = "";
    public string? StatusReason { get; set; }
    public string? VerifyStatus { get; set; }
    public string? Gate { get; set; }
    public long WalletBalance { get; set; }
    public List<TodoItem> Todos { get; set; } = [];
    public List<ShipperTripRow> ActiveTrips { get; set; } = [];
    public List<ShipperLoadRow> RecentLoads { get; set; } = [];
}

// ------------------------------------------------------------------ بار

/// <summary>فرم ثبت بار. عددها رشته‌اند تا ارقام فارسی و «۲/۵» هم پذیرفته شوند.</summary>
public sealed class ShipperLoadForm
{
    public static readonly string[] CargoTypes =
    [
        "مواد غذایی", "محصولات کشاورزی", "میوه و تره‌بار", "نهاده‌های دامی", "مصالح ساختمانی", "آهن‌آلات",
        "چوب و الوار", "لوازم خانگی", "لوازم الکترونیکی", "مبلمان", "اثاثیه منزل", "پوشاک و منسوجات",
        "دارو", "مواد شیمیایی", "فرآورده‌های نفتی", "قطعات صنعتی", "ماشین‌آلات", "کالای وارداتی", "کالای فله"
    ];

    public static readonly string[] Packagings =
        ["کارتن", "پالت", "کیسه", "فله", "بشکه", "جعبهٔ چوبی", "رول", "باندل", "کانتینر", "بدون بسته‌بندی"];

    public int? CopyOf { get; set; }

    // sec-cargo
    public string? Title { get; set; }
    public string? CargoType { get; set; }
    public string? CargoTypeOther { get; set; }
    public string? Packaging { get; set; }

    // sec-route
    public int? OriginCityId { get; set; }
    public string? OriginAddress { get; set; }
    public string? OriginLat { get; set; }
    public string? OriginLng { get; set; }
    public int? DestCityId { get; set; }
    public string? DestAddress { get; set; }
    public string? DestLat { get; set; }
    public string? DestLng { get; set; }

    // sec-time
    public DateTime? LoadingDate { get; set; }
    public int LoadingHour { get; set; } = 8;
    public DateTime? LoadingToDate { get; set; }

    // sec-vehicle
    public int? VehicleTypeId { get; set; }

    // sec-size
    public string? WeightTon { get; set; }
    public string? VolumeM3 { get; set; }
    public string? LengthM { get; set; }
    public string? WidthM { get; set; }
    public string? HeightM { get; set; }

    // sec-value
    public string PriceMode { get; set; } = "negotiable";
    public string? PriceToman { get; set; }
    public string? DeclaredValueToman { get; set; }

    // sec-media
    public string? Description { get; set; }

    // sec-insurance
    public bool InsuranceRequested { get; set; }

    // گیرنده و درخواست مستقیم
    public string? ReceiverName { get; set; }
    public string? ReceiverMobile { get; set; }
    public int? TargetCompanyId { get; set; }
}

public sealed class CompanyOption
{
    public int CompanyId { get; init; }
    public string Name { get; init; } = "";
    public string? City { get; init; }
    public double RatingAvg { get; init; }
    public int RatingCount { get; init; }
}

public sealed class ShipperLoadFormLists
{
    public List<ShipperCityOption> Cities { get; init; } = [];
    public List<VehicleType> VehicleTypes { get; init; } = [];
    public List<SavedAddress> Addresses { get; init; } = [];
    public List<CompanyOption> Companies { get; init; } = [];
    public decimal? InsurancePercent { get; init; }
}

public sealed class ShipperLoadDetailVm
{
    public Load Load { get; init; } = null!;
    public int PendingOffers { get; init; }
    public int TotalOffers { get; init; }
    public long? LowestOffer { get; init; }
    public Trip? Trip { get; init; }
    public List<ShipperTripRow> CancelledTrips { get; init; } = [];
    public decimal? InsurancePercent { get; init; }
    public long? InsuranceEstimate { get; init; }
    public List<string> PublishProblems { get; init; } = [];
    public string MapJson { get; init; } = "";
}

// ------------------------------------------------------------------ پیشنهاد

/// <summary>یک پیشنهاد با هر آنچه برای مقایسه لازم است.</summary>
public sealed class OfferCompareRow
{
    public int OfferId { get; set; }
    public int LoadId { get; set; }
    public string CarrierKind { get; set; } = "";
    public int? DriverId { get; set; }
    public int? CompanyId { get; set; }
    public string CarrierName { get; set; } = "";
    /// <summary>برای رانندهٔ عضو شرکت: نام شرکتش.</summary>
    public string? DriverCompany { get; set; }
    public string? City { get; set; }
    public DateTime MemberSince { get; set; }

    public long Amount { get; set; }
    public int? EtaHours { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public double RatingAvg { get; set; }
    public int RatingCount { get; set; }
    public int TripCount { get; set; }
    public int TripsWithMe { get; set; }
    public int CancelledByCarrier { get; set; }

    // خودرو: خودروی پیشنهاد، یا خودروی فعال رانندهٔ مستقل
    public bool VehicleFromOffer { get; set; }
    public string? VehicleType { get; set; }
    public string? BodyKind { get; set; }
    public string? PlateNo { get; set; }
    public decimal? CapacityTon { get; set; }
    public string? Brand { get; set; }
    public string? VehicleModel { get; set; }
    public int? Year { get; set; }
    public DateTime? InsuranceExpiresAt { get; set; }
    public DateTime? InspectionExpiresAt { get; set; }
    public string? VehicleVerify { get; set; }
    /// <summary>شرکت: شمار خودروهای فعال و تأییدشده.</summary>
    public int FleetSize { get; set; }

    public int VerifiedDocs { get; set; }
    public bool Ready { get; set; }
    public List<string> Issues { get; set; } = [];
    public bool IsLowest { get; set; }
    public bool IsFavorite { get; set; }
}

public sealed class OfferLoadGroup
{
    public int LoadId { get; init; }
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal WeightTon { get; init; }
    public DateTime LoadingFrom { get; init; }
    public string PriceMode { get; init; } = "";
    public long? Price { get; init; }
    public List<OfferCompareRow> Offers { get; set; } = [];
}

public sealed class OfferCompareVm
{
    public Load Load { get; init; } = null!;
    public List<OfferCompareRow> Rows { get; init; } = [];
    /// <summary>price | driver | vehicle | rating</summary>
    public string Mode { get; init; } = "price";
    /// <summary>بار در بازار است و پیشنهادها قابل پذیرش‌اند.</summary>
    public bool CanDecide { get; init; }
    public int? TripId { get; init; }
    public List<ShipperLoadRow> Switchable { get; init; } = [];
}

public sealed class DocBadge
{
    public string Kind { get; init; } = "";
    public string Subject { get; init; } = "";
    public DateTime? ExpiresAt { get; init; }
}

public sealed class ShipperDriverProfileVm
{
    public int DriverId { get; init; }
    public string Name { get; init; } = "";
    public string? City { get; init; }
    public string? CompanyName { get; init; }
    public string Status { get; init; } = "";
    public double RatingAvg { get; init; }
    public int RatingCount { get; init; }
    public int TripCount { get; init; }
    public DateTime MemberSince { get; init; }
    public bool IsFavorite { get; init; }
    public List<Vehicle> Vehicles { get; init; } = [];
    public List<DocBadge> Docs { get; init; } = [];
    public List<Rating> Reviews { get; init; } = [];
    public List<ShipperTripRow> TripsWithMe { get; init; } = [];
    public List<OfferCompareRow> OffersOnMyLoads { get; init; } = [];
}

// ------------------------------------------------------------------ رهگیری

public sealed class ShipperTrackingVm
{
    public Trip? Trip { get; init; }
    public List<ShipperTripRow> Picker { get; init; } = [];
    public List<TripEvent> Events { get; init; } = [];
    /// <summary>route | status | eta</summary>
    public string Mode { get; init; } = "";
    public string MapJson { get; init; } = "";
    public string ShareUrl { get; init; } = "";
    public double? RemainingKm { get; init; }
    public int PointCount { get; init; }
    public double? SpeedKmh { get; init; }
}

public sealed class ShipperTrackHistoryVm
{
    public ShipperTripRow? Trip { get; init; }
    public List<ShipperTripRow> Picker { get; init; } = [];
    public PageVm<TrackingPoint> Points { get; init; } = new();
    public DateTime? FirstAt { get; init; }
    public DateTime? LastAt { get; init; }
    public double? AvgSpeed { get; init; }
    public double? MaxSpeed { get; init; }
}

// ------------------------------------------------------------------ سفارش

public sealed class ShipperOrderVm
{
    public Trip Trip { get; init; } = null!;
    public List<TripEvent> Events { get; init; } = [];
    public List<TripDocument> Documents { get; init; } = [];
    public List<Payment> Payments { get; init; } = [];
    public List<Invoice> Invoices { get; init; } = [];
    public List<Refund> Refunds { get; init; } = [];
    public List<Complaint> Complaints { get; init; } = [];
    public Rating? MyRating { get; init; }
    public long WalletBalance { get; init; }
    public bool CanCancel { get; init; }
    public bool CanRate { get; init; }
    public string RateTarget { get; init; } = "";
    public int PointCount { get; init; }
    public int AssignmentChanges { get; init; }
}

// ------------------------------------------------------------------ اسناد

public sealed class ShipperDocRow
{
    public int TripId { get; init; }
    public string TripCode { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    /// <summary>waybill یا یکی از TripDocumentKind</summary>
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Number { get; init; }
    public string? Status { get; init; }
    public string? FilePath { get; init; }
    public string ByKind { get; init; } = "";
    public DateTime At { get; init; }
}

public sealed class ShipperDocGroup
{
    public ShipperTripRow Trip { get; init; } = null!;
    public List<ShipperDocRow> Docs { get; init; } = [];
}

// ------------------------------------------------------------------ مالی

public sealed class ShipperFinanceVm
{
    public long Balance { get; init; }
    public long UnpaidTotal { get; init; }
    public int UnpaidCount { get; init; }
    public long PaidThisMonth { get; init; }
    public long RefundedTotal { get; init; }
    public bool DemoGateway { get; init; }
    public List<WalletTransaction> Recent { get; init; } = [];
    public List<Payment> Charges { get; init; } = [];
}

public sealed class ShipperPayVm
{
    public long Balance { get; init; }
    public List<ShipperTripRow> Unpaid { get; init; } = [];
    public List<Payment> PendingPayments { get; init; } = [];
    public List<Payment> FailedPayments { get; init; } = [];
    public long Total => Unpaid.Sum(t => t.Fare);
}

public sealed class ShipperRefundRow
{
    public int RefundId { get; init; }
    public int? TripId { get; init; }
    public string? TripCode { get; init; }
    public long Amount { get; init; }
    public string Reason { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? DoneAt { get; init; }
}

// ------------------------------------------------------------------ رانندگان

public sealed class ShipperDriverRow
{
    public int DriverId { get; init; }
    public string Name { get; init; } = "";
    public string? City { get; init; }
    public string? CompanyName { get; init; }
    public double RatingAvg { get; init; }
    public int RatingCount { get; init; }
    public int TripCount { get; init; }
    public int TripsWithMe { get; init; }
    public int DoneWithMe { get; init; }
    public DateTime? LastTripAt { get; init; }
    public int? LastTripId { get; init; }
    public string? LastTripCode { get; init; }
    public bool IsFavorite { get; init; }
    public DateTime? FavoritedAt { get; init; }
}

public sealed class ShipperRatingRow
{
    public int TripId { get; init; }
    public string TripCode { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string ToKind { get; init; } = "";
    public string TargetName { get; init; } = "";
    public int Score { get; init; }
    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class ShipperRateVm
{
    public List<ShipperTripRow> ToRate { get; init; } = [];
    public List<ShipperRatingRow> Given { get; init; } = [];
}

// ------------------------------------------------------------------ حساب

public sealed class ShipperProfileForm
{
    public string Kind { get; set; } = "person";
    public string? FullName { get; set; }
    public string? BusinessName { get; set; }
    public string? NationalCode { get; set; }
    public string? NationalId { get; set; }
    public string? EconomicCode { get; set; }
    public int? CityId { get; set; }
    public string? Address { get; set; }
    public string? Email { get; set; }
}

public sealed class ShipperVerificationVm
{
    public string Kind { get; init; } = "person";
    public string? VerifyStatus { get; init; }
    public DateTime? VerifiedAt { get; init; }
    public bool ProfileComplete { get; init; }
    public List<string> MissingProfile { get; init; } = [];
    public List<Document> Docs { get; init; } = [];
    public string[] RequiredKinds { get; init; } = [];
}

// ------------------------------------------------------------------ کمکی‌های نمایش و ورودی

public static class ShipperUi
{
    /// <summary>«۲۲ تن» / «۲٫۵ تن»</summary>
    public static string Ton(decimal v) => Fa.N(v, v % 1 == 0 ? 0 : 1) + " تن";

    public static string Hour(int h) => Fa.Digits($"{h:00}:00");

    /// <summary>ساعت بارگیری به وقت تهران.</summary>
    public static int TehranHour(DateTime utc) => Fa.ToTehran(utc).Hour;

    public static string WaybillStatusLabel(string s) => s switch
    {
        "verified" => "تأییدشده",
        "void" => "باطل",
        _ => "ثبت‌شده"
    };

    public static string WaybillStatusTone(string s) => s switch { "verified" => "ok", "void" => "no", _ => "inf" };

    public static string RefundLabel(string s) => s switch { "done" => "واریزشده", "rejected" => "ردشده", _ => "در انتظار بررسی" };
    public static string RefundTone(string s) => s switch { "done" => "ok", "rejected" => "no", _ => "wait" };

    public static string InvoiceKindLabel(string k) => k switch
    {
        "freight" => "کرایهٔ حمل",
        "commission" => "کمیسیون",
        "subscription" => "اشتراک",
        _ => k
    };

    public static string VerifyLabel(string? s) => s switch
    {
        null or "" => "ارسال نشده",
        AccountStatus.Pending => "در انتظار بررسی",
        AccountStatus.Approved => "احراز شده",
        AccountStatus.Rejected => "رد شده",
        _ => s
    };

    public static string VerifyTone(string? s) => s switch
    {
        AccountStatus.Approved => "ok",
        AccountStatus.Pending => "wait",
        AccountStatus.Rejected => "no",
        _ => "mut"
    };

    /// <summary>«۰۹۱۲***۴۵۶۷»</summary>
    public static string MaskMobile(string? m) =>
        string.IsNullOrEmpty(m) || m.Length < 8 ? Fa.Digits(m) : Fa.Digits(m[..4] + "***" + m[^4..]);

    public static string MaskSheba(string? s) =>
        string.IsNullOrEmpty(s) || s.Length < 10 ? (s ?? "") : s[..4] + " •••• •••• " + s[^6..];

    /// <summary>عدد اعشاری از ورودی کاربر: ارقام فارسی، «٫» و «/» به‌جای نقطه.</summary>
    public static decimal? ParseDecimal(string? input)
    {
        var s = Fa.Latin(input).Replace('٫', '.').Replace('/', '.').Replace(" ", "");
        if (s.Length == 0) return null;
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static double? ParseDouble(string? input)
    {
        var s = Fa.Latin(input).Replace('٫', '.').Replace('/', '.').Replace(" ", "");
        if (s.Length == 0) return null;
        return double.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>مقدار کادر ورودی برای عدد اعشاری (با نقطهٔ لاتین).</summary>
    public static string Box(decimal? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    public static string Box(double? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";

    /// <summary>مبلغ ریالی به عدد تومان برای کادر ورودی.</summary>
    public static string TomanBox(long? rial) => rial is long r ? (r / 10).ToString(CultureInfo.InvariantCulture) : "";
}
