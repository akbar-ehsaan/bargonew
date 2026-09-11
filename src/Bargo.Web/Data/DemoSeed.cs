using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Data;

/// <summary>
/// دادهٔ نمایشی — فقط برای توسعه (Seed:Demo=true). همهٔ حساب‌ها با گذرواژهٔ 1234.
/// چرخهٔ کامل را پوشش می‌دهد تا هر پنل از همان ورود اول چیزی برای نشان دادن داشته
/// باشد: بار باز با پیشنهاد، سفر در حال حمل با مسیر GPS، سفر تسویه‌شده، مدرک در
/// انتظار بررسی، شرکت با راننده و خودرو.
/// </summary>
public static class DemoSeed
{
    private const string Pass = "1234";

    public static async Task RunAsync(BargoDbContext db)
    {
        var hash = PasswordHasher.Hash(Pass);
        var now = DateTime.UtcNow;

        async Task<City> CityAsync(string name) => await db.Cities.FirstAsync(c => c.Name == name);
        var tehran = await CityAsync("تهران");
        var isfahan = await CityAsync("اصفهان");
        var mashhad = await CityAsync("مشهد");
        var tabriz = await CityAsync("تبریز");
        var shiraz = await CityAsync("شیراز");
        var bandar = await CityAsync("بندرعباس");
        var types = await db.VehicleTypes.ToListAsync();
        VehicleType T(string name) => types.First(t => t.Name == name);

        // ---------------- صاحبان بار ----------------
        var s1 = new Shipper { Mobile = "09121111111", PassHash = hash, Kind = "business", FullName = "مریم احمدی", BusinessName = "صنایع غذایی آوند", NationalId = "10320000001", CityId = tehran.CityId, VerifyStatus = AccountStatus.Approved, VerifiedAt = now.AddDays(-40), WalletBalance = 0 };
        var s2 = new Shipper { Mobile = "09122222222", PassHash = hash, Kind = "person", FullName = "رضا کریمی", NationalCode = "0012345678", CityId = isfahan.CityId, VerifyStatus = AccountStatus.Pending };
        db.Shippers.AddRange(s1, s2);

        // ---------------- شرکت ----------------
        var co = new Company
        {
            Name = "حمل‌ونقل سپهر بار", NationalId = "10103456789", RegistrationNo = "45821", LicenseNo = "ح-۱۲۳۴۵",
            LicenseExpiresAt = now.AddDays(200), ManagerName = "حسین نوری", ManagerNationalCode = "0071234567",
            Mobile = "09124444444", Phone = "021-66554433", CityId = tehran.CityId, Address = "تهران، جادهٔ ساوه، پایانهٔ بار",
            Sheba = "IR120120000000001234567890", BankName = "ملت", Status = AccountStatus.Approved, ApprovedAt = now.AddDays(-60),
            DriverSharePercent = 70
        };
        co.Users.Add(new CompanyUser { Name = "حسین نوری", Mobile = "09124444444", PassHash = hash, Title = "owner", IsOwner = true, Permissions = CompanyPermission.All });
        co.Users.Add(new CompanyUser { Name = "سارا توکلی", Mobile = "09124444445", PassHash = hash, Title = "dispatcher", Permissions = CompanyPermission.Loads | CompanyPermission.Dispatch | CompanyPermission.Drivers | CompanyPermission.Fleet });
        var coPending = new Company
        {
            Name = "باربری آسمان آبی", NationalId = "10109876543", ManagerName = "کامران شریفی", Mobile = "09126666666",
            CityId = tabriz.CityId, Status = AccountStatus.Pending
        };
        coPending.Users.Add(new CompanyUser { Name = "کامران شریفی", Mobile = "09126666666", PassHash = hash, IsOwner = true, Permissions = CompanyPermission.All });
        db.Companies.AddRange(co, coPending);
        await db.SaveChangesAsync();

        // ---------------- رانندگان ----------------
        Driver NewDriver(string mobile, string first, string last, string nc, City city, string status, int? companyId = null, double? lat = null, double? lng = null) => new()
        {
            Mobile = mobile, PassHash = hash, FirstName = first, LastName = last, NationalCode = nc, CityId = city.CityId,
            LicenseNo = "LN" + nc[..6], LicenseExpiresAt = now.AddDays(400), SmartCardNo = "SC" + nc[4..], SmartCardExpiresAt = now.AddDays(150),
            Status = status, ApprovedAt = status == AccountStatus.Approved ? now.AddDays(-30) : null, CompanyId = companyId,
            LastLat = lat, LastLng = lng, LastSeenAt = lat is null ? null : now.AddMinutes(-4),
            Sheba = "IR5401700000000" + nc[..10] + "1"
        };

        var d1 = NewDriver("09123333333", "علی", "رضایی", "0023456781", tehran, AccountStatus.Approved, lat: 35.66, lng: 51.30);
        d1.RatingAvg = 4.6; d1.RatingCount = 23; d1.TripCount = 41;
        var d2 = NewDriver("09125555555", "محمد", "حسینی", "0034567812", isfahan, AccountStatus.Approved, lat: 34.10, lng: 51.25);
        d2.RatingAvg = 4.2; d2.RatingCount = 9; d2.TripCount = 12;
        var d3 = NewDriver("09127777777", "جواد", "مرادی", "0045678123", mashhad, AccountStatus.Pending);
        var cd1 = NewDriver("09128888881", "بهروز", "صادقی", "0056781234", tehran, AccountStatus.Approved, co.CompanyId, 35.70, 51.42);
        cd1.RatingAvg = 4.8; cd1.RatingCount = 31; cd1.TripCount = 57;
        var cd2 = NewDriver("09128888882", "ناصر", "امینی", "0067812345", tehran, AccountStatus.Approved, co.CompanyId, 35.62, 51.36);
        db.Drivers.AddRange(d1, d2, d3, cd1, cd2);
        await db.SaveChangesAsync();

        // ---------------- خودروها ----------------
        var v1 = new Vehicle { PlateNo = "۱۲ ع ۳۴۵ ایران ۱۱", VehicleTypeId = T("تریلی چادری").VehicleTypeId, Brand = "ولوو", Model = "FH500", Year = 2019, CapacityTon = 24, DriverId = d1.DriverId, VehicleCardNo = "VC-1001", InsurancePolicyNo = "INS-77001", InsuranceExpiresAt = now.AddDays(18), InspectionExpiresAt = now.AddDays(120), VerifyStatus = AccountStatus.Approved, LastLat = 35.66, LastLng = 51.30, LastSeenAt = now.AddMinutes(-4) };
        var v2 = new Vehicle { PlateNo = "۵۴ ب ۶۷۸ ایران ۱۳", VehicleTypeId = T("کامیون تک یخچالی").VehicleTypeId, Brand = "بنز", Model = "2624", Year = 2015, CapacityTon = 10, DriverId = d2.DriverId, VehicleCardNo = "VC-1002", InsuranceExpiresAt = now.AddDays(90), InspectionExpiresAt = now.AddDays(-3), VerifyStatus = AccountStatus.Approved, LastLat = 34.10, LastLng = 51.25, LastSeenAt = now.AddMinutes(-2) };
        var v3 = new Vehicle { PlateNo = "۳۳ ج ۱۱۲ ایران ۳۶", VehicleTypeId = T("کامیون جفت بغل‌باز").VehicleTypeId, Brand = "اسکانیا", Year = 2012, CapacityTon = 18, DriverId = d3.DriverId, VerifyStatus = AccountStatus.Pending };
        var cv1 = new Vehicle { PlateNo = "۷۷ د ۴۴۱ ایران ۱۰", VehicleTypeId = T("تریلی کفی").VehicleTypeId, Brand = "داف", Model = "XF", Year = 2020, CapacityTon = 24, CompanyId = co.CompanyId, VehicleCardNo = "VC-2001", InsuranceExpiresAt = now.AddDays(210), InspectionExpiresAt = now.AddDays(160), VerifyStatus = AccountStatus.Approved, LastLat = 35.70, LastLng = 51.42, LastSeenAt = now.AddMinutes(-1) };
        var cv2 = new Vehicle { PlateNo = "۱۹ ه ۸۰۲ ایران ۲۰", VehicleTypeId = T("تریلی یخچالی").VehicleTypeId, Brand = "ولوو", Year = 2018, CapacityTon = 22, CompanyId = co.CompanyId, VehicleCardNo = "VC-2002", InsuranceExpiresAt = now.AddDays(25), InspectionExpiresAt = now.AddDays(300), VerifyStatus = AccountStatus.Approved };
        db.Vehicles.AddRange(v1, v2, v3, cv1, cv2);
        await db.SaveChangesAsync();

        // ---------------- مدارک ----------------
        void Doc(string ownerKind, int ownerId, string kind, string status, DateTime? expires = null) =>
            db.Documents.Add(new Document { OwnerKind = ownerKind, OwnerId = ownerId, Kind = kind, Status = status, ExpiresAt = expires, Number = kind.ToUpperInvariant()[..3] + "-" + ownerId, ReviewedAt = status == AccountStatus.Pending ? null : now.AddDays(-20), UploadedAt = now.AddDays(status == AccountStatus.Pending ? -1 : -25) });

        foreach (var d in new[] { d1, d2, cd1, cd2 })
        {
            Doc(OwnerKind.Driver, d.DriverId, DocumentKind.NationalCard, AccountStatus.Approved);
            Doc(OwnerKind.Driver, d.DriverId, DocumentKind.License, AccountStatus.Approved, d.LicenseExpiresAt);
            Doc(OwnerKind.Driver, d.DriverId, DocumentKind.SmartCard, AccountStatus.Approved, d.SmartCardExpiresAt);
        }
        foreach (var v in new[] { v1, v2, cv1, cv2 })
        {
            Doc(OwnerKind.Vehicle, v.VehicleId, DocumentKind.VehicleCard, AccountStatus.Approved);
            Doc(OwnerKind.Vehicle, v.VehicleId, DocumentKind.Insurance, AccountStatus.Approved, v.InsuranceExpiresAt);
            Doc(OwnerKind.Vehicle, v.VehicleId, DocumentKind.Inspection, AccountStatus.Approved, v.InspectionExpiresAt);
        }
        // رانندهٔ در انتظار تأیید با مدارک در صف بررسی مدیر
        Doc(OwnerKind.Driver, d3.DriverId, DocumentKind.NationalCard, AccountStatus.Pending);
        Doc(OwnerKind.Driver, d3.DriverId, DocumentKind.License, AccountStatus.Pending, now.AddDays(700));
        Doc(OwnerKind.Driver, d3.DriverId, DocumentKind.SmartCard, AccountStatus.Pending, now.AddDays(300));
        Doc(OwnerKind.Vehicle, v3.VehicleId, DocumentKind.Insurance, AccountStatus.Pending, now.AddDays(250));
        Doc(OwnerKind.Company, co.CompanyId, DocumentKind.CompanyLicense, AccountStatus.Approved, co.LicenseExpiresAt);
        Doc(OwnerKind.Company, coPending.CompanyId, DocumentKind.Registration, AccountStatus.Pending);
        Doc(OwnerKind.Shipper, s2.ShipperId, DocumentKind.NationalCard, AccountStatus.Pending);
        await db.SaveChangesAsync();

        // ---------------- بارها ----------------
        Load NewLoad(Shipper? sh, City from, City to, string cargo, string title, decimal ton, string vtype, int daysAhead, string mode, long? price, string status, long? value = null) => new()
        {
            ShipperId = sh?.ShipperId, OriginCityId = from.CityId, DestCityId = to.CityId,
            OriginAddress = $"{from.Name}، شهرک صنعتی", DestAddress = $"{to.Name}، انبار مرکزی",
            OriginLat = from.Lat, OriginLng = from.Lng, DestLat = to.Lat, DestLng = to.Lng,
            CargoType = cargo, Title = title, WeightTon = ton, VehicleTypeId = T(vtype).VehicleTypeId,
            LoadingFrom = Fa.WithHour(now.AddDays(daysAhead), 8), PriceMode = mode, Price = price, DeclaredValue = value,
            InsuranceRequested = value is > 0, ReceiverName = "انباردار " + to.Name, ReceiverMobile = "0913" + Random.Shared.Next(1000000, 9999999),
            DistanceKm = Geo.RoadKm(from.Lat, from.Lng, to.Lat, to.Lng), Status = status,
            PublishedAt = now.AddHours(-daysAhead * 3 - 5), CreatedAt = now.AddHours(-daysAhead * 3 - 6)
        };

        var lOpen1 = NewLoad(s1, tehran, mashhad, "مواد غذایی", "کنسرو و مواد غذایی بسته‌بندی", 22, "تریلی چادری", 2, PriceMode.Negotiable, 480_000_000, LoadStatus.Offering, 3_500_000_000);
        var lOpen2 = NewLoad(s1, tehran, tabriz, "مواد غذایی", "لبنیات — نیازمند یخچال", 9, "کامیون تک یخچالی", 1, PriceMode.Fixed, 350_000_000, LoadStatus.Open, 900_000_000);
        var lOpen3 = NewLoad(s2, isfahan, shiraz, "مصالح ساختمانی", "سیمان پاکتی", 18, "کامیون جفت بغل‌باز", 3, PriceMode.Negotiable, null, LoadStatus.Open);
        var lTransit = NewLoad(s1, tehran, isfahan, "لوازم خانگی", "یخچال و ماشین لباسشویی", 20, "تریلی چادری", -1, PriceMode.Negotiable, 300_000_000, LoadStatus.Booked, 6_000_000_000);
        var lCompany = NewLoad(s1, bandar, tehran, "کالای وارداتی", "کانتینر قطعات صنعتی", 24, "تریلی کفی", 1, PriceMode.Negotiable, 650_000_000, LoadStatus.Booked, 12_000_000_000);
        var lDone = NewLoad(s2, isfahan, tehran, "مصالح ساختمانی", "کاشی و سرامیک", 16, "کامیون جفت بغل‌باز", -8, PriceMode.Fixed, 260_000_000, LoadStatus.Completed);
        var lDirect = NewLoad(s1, tehran, shiraz, "دارو", "دارو — درخواست مستقیم", 6, "تریلی یخچالی", 4, PriceMode.Negotiable, 400_000_000, LoadStatus.Open, 8_000_000_000);
        lDirect.TargetCompanyId = co.CompanyId;
        db.Loads.AddRange(lOpen1, lOpen2, lOpen3, lTransit, lCompany, lDone, lDirect);
        await db.SaveChangesAsync();
        foreach (var l in new[] { lOpen1, lOpen2, lOpen3, lTransit, lCompany, lDone, lDirect })
            l.Code = Codes.Make("L", l.LoadId, l.CreatedAt);

        // ---------------- پیشنهادها ----------------
        db.Offers.AddRange(
            new Offer { LoadId = lOpen1.LoadId, CarrierKind = CarrierKind.Driver, DriverId = d1.DriverId, VehicleId = v1.VehicleId, Amount = 470_000_000, EtaHours = 6, Note = "فردا صبح در مبدا هستم." },
            new Offer { LoadId = lOpen1.LoadId, CarrierKind = CarrierKind.Company, CompanyId = co.CompanyId, Amount = 455_000_000, EtaHours = 12, Note = "با تریلی کفی و چادر." });

        var accepted1 = new Offer { LoadId = lTransit.LoadId, CarrierKind = CarrierKind.Driver, DriverId = d1.DriverId, VehicleId = v1.VehicleId, Amount = 300_000_000, Status = OfferStatus.Accepted, RespondedAt = now.AddDays(-2) };
        var accepted2 = new Offer { LoadId = lCompany.LoadId, CarrierKind = CarrierKind.Company, CompanyId = co.CompanyId, Amount = 640_000_000, Status = OfferStatus.Accepted, RespondedAt = now.AddHours(-10) };
        var accepted3 = new Offer { LoadId = lDone.LoadId, CarrierKind = CarrierKind.Driver, DriverId = d2.DriverId, VehicleId = v2.VehicleId, Amount = 260_000_000, Status = OfferStatus.Accepted, RespondedAt = now.AddDays(-9) };
        db.Offers.AddRange(accepted1, accepted2, accepted3);
        await db.SaveChangesAsync();

        // ---------------- سفرها ----------------
        Trip NewTrip(Load l, Offer o, string status, int? driverId, int? vehicleId, int? companyId, bool paid)
        {
            var commission = Math.Max(500_000, (long)Math.Round(o.Amount * 0.08m));
            return new Trip
            {
                LoadId = l.LoadId, OfferId = o.OfferId, CarrierKind = o.CarrierKind, DriverId = driverId, VehicleId = vehicleId, CompanyId = companyId,
                Fare = o.Amount, CommissionPercent = 8, Commission = commission, CarrierShare = o.Amount - commission,
                DriverShare = companyId is null ? null : (long)Math.Round((o.Amount - commission) * 0.7m),
                Status = status, IsPaid = paid, ScheduledDepartureAt = l.LoadingFrom, PlannedKm = l.DistanceKm,
                CreatedAt = o.RespondedAt ?? now
            };
        }

        // سفر در حال حمل: تهران ← اصفهان، راننده مستقل
        var tTransit = NewTrip(lTransit, accepted1, TripStatus.InTransit, d1.DriverId, v1.VehicleId, null, true);
        tTransit.StartedAt = now.AddHours(-9); tTransit.LoadedAt = now.AddHours(-6);
        // سفر شرکتی در انتظار تخصیص
        var tCompany = NewTrip(lCompany, accepted2, TripStatus.AwaitingAssignment, null, null, co.CompanyId, false);
        // سفر تکمیل و تسویه‌شده
        var tDone = NewTrip(lDone, accepted3, TripStatus.Settled, d2.DriverId, v2.VehicleId, null, true);
        tDone.StartedAt = now.AddDays(-8); tDone.LoadedAt = now.AddDays(-8).AddHours(3); tDone.DeliveredAt = now.AddDays(-7); tDone.SettledAt = now.AddDays(-7);
        db.Trips.AddRange(tTransit, tCompany, tDone);
        await db.SaveChangesAsync();
        foreach (var t in new[] { tTransit, tCompany, tDone }) t.Code = Codes.Make("T", t.TripId, t.CreatedAt);

        void Ev(Trip t, string? from, string to, string actorKind, int actorId, string name, DateTime at, string? note = null) =>
            db.TripEvents.Add(new TripEvent { TripId = t.TripId, FromStatus = from, ToStatus = to, ActorKind = actorKind, ActorId = actorId, ActorName = name, CreatedAt = at, Note = note });

        Ev(tTransit, null, TripStatus.Accepted, Roles.Shipper, s1.ShipperId, s1.FullName, now.AddDays(-2), "پیشنهاد پذیرفته شد و سفر ساخته شد");
        Ev(tTransit, TripStatus.Accepted, TripStatus.ToOrigin, Roles.Driver, d1.DriverId, d1.FullName, now.AddHours(-9));
        Ev(tTransit, TripStatus.ToOrigin, TripStatus.AtOrigin, Roles.Driver, d1.DriverId, d1.FullName, now.AddHours(-8));
        Ev(tTransit, TripStatus.AtOrigin, TripStatus.Loaded, Roles.Driver, d1.DriverId, d1.FullName, now.AddHours(-6));
        Ev(tTransit, TripStatus.Loaded, TripStatus.Loaded, Roles.Driver, d1.DriverId, d1.FullName, now.AddHours(-5.8), "ثبت بارنامه BG-88213");
        Ev(tTransit, TripStatus.Loaded, TripStatus.InTransit, Roles.Driver, d1.DriverId, d1.FullName, now.AddHours(-5.5));
        Ev(tCompany, null, TripStatus.AwaitingAssignment, Roles.Shipper, s1.ShipperId, s1.FullName, now.AddHours(-10), "پیشنهاد پذیرفته شد و سفر ساخته شد");
        Ev(tDone, null, TripStatus.Accepted, Roles.Shipper, s2.ShipperId, s2.FullName, now.AddDays(-9));
        Ev(tDone, TripStatus.Unloaded, TripStatus.Delivered, Roles.Driver, d2.DriverId, d2.FullName, now.AddDays(-7), "تحویل با کد گیرنده تأیید شد");
        Ev(tDone, TripStatus.Delivered, TripStatus.Settled, "system", 0, "سامانه", now.AddDays(-7));

        db.Waybills.Add(new Waybill { TripId = tTransit.TripId, Number = "BG-88213", IssuedByKind = Roles.Driver, IssuedById = d1.DriverId, IssuedAt = now.AddHours(-5.8) });
        db.Waybills.Add(new Waybill { TripId = tDone.TripId, Number = "BG-87990", IssuedByKind = Roles.Driver, IssuedById = d2.DriverId, IssuedAt = now.AddDays(-8), Status = "verified" });
        db.TripAssignments.Add(new TripAssignment { TripId = tTransit.TripId, DriverId = d1.DriverId, VehicleId = v1.VehicleId, ByName = "پیشنهاد راننده", CreatedAt = now.AddDays(-2) });

        // مسیر GPS سفر در حال حمل — از تهران تا حدود قم
        var from = (lat: 35.66, lng: 51.30);
        var to = (lat: 34.35, lng: 50.95);
        var steps = 24;
        var prev = from;
        double km = 0;
        for (var i = 0; i <= steps; i++)
        {
            var f = i / (double)steps;
            var p = (lat: from.lat + (to.lat - from.lat) * f + Math.Sin(f * 9) * 0.02, lng: from.lng + (to.lng - from.lng) * f);
            var step = i == 0 ? 0 : Geo.Km(prev.lat, prev.lng, p.lat, p.lng);
            km += step;
            db.TrackingPoints.Add(new TrackingPoint { TripId = tTransit.TripId, DriverId = d1.DriverId, VehicleId = v1.VehicleId, Lat = p.lat, Lng = p.lng, SpeedKmh = 60 + (i % 5) * 4, StepKm = step, RecordedAt = now.AddHours(-5.5).AddMinutes(i * 13) });
            prev = p;
        }
        tTransit.TravelledKm = Math.Round(km, 1);
        tTransit.LastLat = to.lat; tTransit.LastLng = to.lng; tTransit.LastPointAt = now.AddMinutes(-4);
        tTransit.EtaAt = Geo.EtaUtc(Geo.RoadKm(to.lat, to.lng, isfahan.Lat, isfahan.Lng) ?? 200);
        d1.LastLat = to.lat; d1.LastLng = to.lng; d1.LastSeenAt = now.AddMinutes(-4);
        v1.LastLat = to.lat; v1.LastLng = to.lng; v1.LastSeenAt = now.AddMinutes(-4);

        // ---------------- مالی ----------------
        var wallet = new WalletService(db);
        await wallet.PostAsync(OwnerKind.Shipper, s1.ShipperId, 2_000_000_000, WalletTxnKind.Charge, note: "شارژ اولیه (نمایشی)");
        await wallet.PostAsync(OwnerKind.Shipper, s1.ShipperId, -tTransit.Fare, WalletTxnKind.FarePayment, tTransit.TripId, $"کرایهٔ سفر {tTransit.Code}");
        await wallet.PostAsync(OwnerKind.Shipper, s2.ShipperId, 300_000_000, WalletTxnKind.Charge, note: "شارژ اولیه (نمایشی)");
        await wallet.PostAsync(OwnerKind.Shipper, s2.ShipperId, -tDone.Fare, WalletTxnKind.FarePayment, tDone.TripId, $"کرایهٔ سفر {tDone.Code}");
        await wallet.PostAsync(OwnerKind.Driver, d2.DriverId, tDone.CarrierShare, WalletTxnKind.FareIncome, tDone.TripId, $"سهم کرایهٔ سفر {tDone.Code}");
        await wallet.PostAsync(OwnerKind.Platform, 0, tDone.Commission, WalletTxnKind.Commission, tDone.TripId, $"کمیسیون سفر {tDone.Code}");
        await wallet.PostAsync(OwnerKind.Company, co.CompanyId, 150_000_000, WalletTxnKind.Charge, note: "شارژ اولیه (نمایشی)");
        db.Payments.AddRange(
            new Payment { PayerKind = OwnerKind.Shipper, PayerId = s1.ShipperId, TripId = tTransit.TripId, Amount = tTransit.Fare, Method = "wallet", Purpose = "fare", Status = PaymentStatus.Paid, PaidAt = now.AddDays(-2) },
            new Payment { PayerKind = OwnerKind.Shipper, PayerId = s2.ShipperId, TripId = tDone.TripId, Amount = tDone.Fare, Method = "wallet", Purpose = "fare", Status = PaymentStatus.Paid, PaidAt = now.AddDays(-9) },
            new Payment { PayerKind = OwnerKind.Shipper, PayerId = s2.ShipperId, Amount = 50_000_000, Method = "gateway", Purpose = "charge", Gateway = "demo", Status = PaymentStatus.Failed, FailReason = "انصراف کاربر از پرداخت", CreatedAt = now.AddDays(-3) });
        db.Invoices.AddRange(
            new Invoice { No = $"F-{Fa.Stamp6(now)}-0001", TripId = tDone.TripId, OwnerKind = OwnerKind.Shipper, OwnerId = s2.ShipperId, Kind = "freight", Amount = tDone.Fare, Total = tDone.Fare, IssuedAt = now.AddDays(-7) },
            new Invoice { No = $"C-{Fa.Stamp6(now)}-0002", TripId = tDone.TripId, OwnerKind = OwnerKind.Driver, OwnerId = d2.DriverId, Kind = "commission", Amount = tDone.Commission, Total = tDone.Commission, IssuedAt = now.AddDays(-7) });
        db.PayoutRequests.Add(new PayoutRequest { OwnerKind = OwnerKind.Driver, OwnerId = d2.DriverId, OwnerName = d2.FullName, Amount = 100_000_000, Sheba = d2.Sheba!, CreatedAt = now.AddDays(-1) });
        db.Ratings.Add(new Rating { TripId = tDone.TripId, FromKind = Roles.Shipper, FromId = s2.ShipperId, ToKind = OwnerKind.Driver, ToId = d2.DriverId, Score = 5, Comment = "سروقت و با دقت. بار سالم رسید.", CreatedAt = now.AddDays(-7) });
        db.FavoriteDrivers.Add(new FavoriteDriver { ShipperId = s2.ShipperId, DriverId = d2.DriverId });

        // ---------------- پشتیبانی و اعلان ----------------
        var ticket = new Ticket { OwnerKind = OwnerKind.Driver, OwnerId = d2.DriverId, OwnerName = d2.FullName, Category = TicketCategory.Financial, Subject = "زمان واریز درخواست برداشت", Priority = TicketPriority.Normal, Status = TicketStatus.Open, CreatedAt = now.AddHours(-20), UpdatedAt = now.AddHours(-20) };
        ticket.Messages.Add(new TicketMessage { SenderKind = OwnerKind.Driver, SenderId = d2.DriverId, SenderName = d2.FullName, Body = "دیروز درخواست برداشت ثبت کردم. چه زمانی به حساب واریز می‌شود؟", CreatedAt = now.AddHours(-20) });
        db.Tickets.Add(ticket);
        db.Complaints.Add(new Complaint { Code = "CP-" + Random.Shared.Next(100000, 999999), TripId = tDone.TripId, FromKind = Roles.Shipper, FromId = s2.ShipperId, FromName = s2.FullName, AgainstKind = OwnerKind.Driver, AgainstId = d2.DriverId, Kind = ComplaintKind.Delay, Title = "تأخیر دو ساعته در رسیدن به مبدا", Body = "راننده دو ساعت دیرتر از زمان اعلامی به مبدا رسید.", CreatedAt = now.AddDays(-6) });
        db.Notifications.AddRange(
            new Notification { OwnerKind = OwnerKind.Shipper, OwnerId = s1.ShipperId, Title = $"پیشنهاد تازه برای بار {lOpen1.Code}", Body = "حمل‌ونقل سپهر بار: ۴۵,۵۰۰,۰۰۰ تومان", Link = $"/Shipper/Offers/Compare/{lOpen1.LoadId}", Kind = "offer" },
            new Notification { OwnerKind = OwnerKind.Company, OwnerId = co.CompanyId, Title = $"پیشنهاد شما برای بار {lCompany.Code} پذیرفته شد", Body = "راننده و خودرو را برای این سفر تخصیص دهید.", Link = $"/Company/Dispatch/Assign/{tCompany.TripId}", Kind = "trip" },
            new Notification { OwnerKind = OwnerKind.Driver, OwnerId = d1.DriverId, Title = "بیمه‌نامهٔ خودروی ۱۲ ع ۳۴۵ به‌زودی منقضی می‌شود", Link = "/Driver/Vehicle/Alerts", Kind = "document" });
        db.Messages.AddRange(
            new Message { ThreadKey = $"trip:{tTransit.TripId}", TripId = tTransit.TripId, FromKind = OwnerKind.Shipper, FromId = s1.ShipperId, FromName = s1.DisplayName, ToKind = OwnerKind.Driver, ToId = d1.DriverId, Body = "سلام، ساعت تقریبی رسیدن به اصفهان؟", CreatedAt = now.AddHours(-2) },
            new Message { ThreadKey = $"trip:{tTransit.TripId}", TripId = tTransit.TripId, FromKind = OwnerKind.Driver, FromId = d1.DriverId, FromName = d1.FullName, ToKind = OwnerKind.Shipper, ToId = s1.ShipperId, Body = "سلام، حدود ۴ ساعت دیگر می‌رسم.", CreatedAt = now.AddHours(-1.8) });

        db.Terminals.AddRange(
            new Terminal { CityId = tehran.CityId, Name = "پایانهٔ بار جادهٔ ساوه", Lat = 35.60, Lng = 51.25 },
            new Terminal { CityId = bandar.CityId, Name = "بندر شهید رجایی", Lat = 27.10, Lng = 56.06 });
        db.RouteLanes.AddRange(
            new RouteLane { OriginCityId = tehran.CityId, DestCityId = isfahan.CityId, DistanceKm = 440, BaseRatePerTon = 14_000_000 },
            new RouteLane { OriginCityId = bandar.CityId, DestCityId = tehran.CityId, DistanceKm = 1330, BaseRatePerTon = 27_000_000 });

        await db.SaveChangesAsync();
    }
}
