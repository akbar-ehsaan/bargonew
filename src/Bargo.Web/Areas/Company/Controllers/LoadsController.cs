namespace Bargo.Web.Areas.CompanyPanel.Controllers;

using System.Linq.Expressions;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// «مدیریت بارها» — دو راه کار گرفتن شرکت و بارهای مشتریان خودش:
///   ۱) بازار بارگو و درخواست‌های مستقیم صاحبان بار ← پیشنهاد (PlaceOffer)
///   ۲) ثبت بار برای مشتریِ خود شرکت ← حمل با ناوگان خود (BookOwnLoad) یا انتشار در بازار
/// همهٔ تغییر وضعیت‌ها از TripFlow می‌گذرد؛ اینجا فقط «بار تازه» با وضعیت اولیه ساخته می‌شود.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Loads)]
public class LoadsController(BargoDbContext db, CurrentUser me, TripFlow flow, AuditService audit, SettingsService settings) : Controller
{
    private static readonly string[] Unassigned = [TripStatus.AwaitingAssignment, TripStatus.Assigned];
    private static readonly string[] OwnUnbooked = [LoadStatus.Draft, LoadStatus.Open, LoadStatus.Offering, LoadStatus.Expired];

    private static Expression<Func<Load, OpsLoadCard>> Card(int cid) => l => new OpsLoadCard
    {
        LoadId = l.LoadId, Code = l.Code, Title = l.Title, CargoType = l.CargoType,
        Origin = l.OriginCity!.Name, OriginProvince = l.OriginCity.Province!.Name,
        Dest = l.DestCity!.Name, DestProvince = l.DestCity.Province!.Name,
        WeightTon = l.WeightTon, VehicleType = l.VehicleType != null ? l.VehicleType.Name : null,
        LoadingFrom = l.LoadingFrom, PriceMode = l.PriceMode, Price = l.Price, DistanceKm = l.DistanceKm,
        // صاحب بار در بازار ناشناس است (Privacy)؛ فقط بارهای خودِ شرکت نام دارند
        OwnerName = l.Shipper != null
            ? (l.Shipper.Kind == "business" ? "صاحب بار (کسب‌وکار)" : "صاحب بار (حقیقی)")
            : (l.Company != null ? (l.CompanyId == cid ? l.Company.Name : "شرکت حمل‌ونقل") : ""),
        OwnerRating = l.Shipper != null ? l.Shipper.RatingAvg : (l.Company != null ? l.Company.RatingAvg : 0),
        OwnerRatingCount = l.Shipper != null ? l.Shipper.RatingCount : (l.Company != null ? l.Company.RatingCount : 0),
        PendingOffers = l.Offers.Count(o => o.Status == OfferStatus.Pending),
        MyOfferAmount = l.Offers.Where(o => o.CompanyId == cid && o.Status == OfferStatus.Pending).Select(o => (long?)o.Amount).FirstOrDefault(),
        InsuranceRequested = l.InsuranceRequested,
        IsDirect = l.TargetCompanyId != null,
        ExpiresAt = l.ExpiresAt
    };

    // =====================================================================
    //  ثبت بار برای مشتری شرکت
    // =====================================================================

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        ViewData["Title"] = "ثبت بار";
        await FillFormListsAsync(ct);
        return View(new OpsLoadForm { LoadingDate = DateTime.UtcNow.AddDays(1) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(OpsLoadForm f, CancellationToken ct)
    {
        ViewData["Title"] = "ثبت بار";
        var cid = me.CompanyId;
        var errors = CompanyOps.BindingErrors(ModelState);

        var title = f.Title?.Trim() ?? "";
        if (title.Length is < 3 or > 150) errors.Add("عنوان بار را بنویسید (۳ تا ۱۵۰ نویسه).");
        var cargo = f.CargoType?.Trim() ?? "";
        if (cargo.Length is 0 or > 60) errors.Add("نوع کالا را بنویسید.");

        var cityIds = new[] { f.OriginCityId ?? 0, f.DestCityId ?? 0 };
        var cities = await db.Cities.AsNoTracking().Where(c => cityIds.Contains(c.CityId)).ToListAsync(ct);
        var origin = cities.FirstOrDefault(c => c.CityId == f.OriginCityId);
        var dest = cities.FirstOrDefault(c => c.CityId == f.DestCityId);
        if (origin is null) errors.Add("شهر مبدا را انتخاب کنید.");
        if (dest is null) errors.Add("شهر مقصد را انتخاب کنید.");
        if (string.IsNullOrWhiteSpace(f.OriginAddress)) errors.Add("نشانی بارگیری در مبدا را بنویسید.");
        if (string.IsNullOrWhiteSpace(f.DestAddress)) errors.Add("نشانی تخلیه در مقصد را بنویسید.");

        DateTime? loadingFrom = f.LoadingDate is DateTime ld ? Fa.WithHour(ld, f.LoadingHour) : null;
        if (loadingFrom is null) errors.Add("تاریخ بارگیری را انتخاب کنید.");
        else if (loadingFrom < Fa.TodayStartUtc) errors.Add("تاریخ بارگیری نمی‌تواند در گذشته باشد.");

        var weight = CompanyOps.ParseDecimal(f.WeightTon);
        if (weight is null or <= 0 or > 100) errors.Add("وزن بار را به تن وارد کنید (بیشتر از صفر و حداکثر ۱۰۰ تن).");
        var volume = CompanyOps.ParseDecimal(f.VolumeM3);
        if (!string.IsNullOrWhiteSpace(f.VolumeM3) && volume is null or < 0 or > 500) errors.Add("حجم بار معتبر نیست.");

        if (f.VehicleTypeId is int vt && !await db.VehicleTypes.AnyAsync(t => t.VehicleTypeId == vt && t.IsActive, ct))
            errors.Add("نوع خودرو معتبر نیست.");

        var own = f.Mode != "market";
        var mode = f.PriceMode == PriceMode.Fixed ? PriceMode.Fixed : PriceMode.Negotiable;
        var price = Fa.ParseToman(f.PriceToman);
        if (!string.IsNullOrWhiteSpace(f.PriceToman) && price is null) errors.Add("مبلغ کرایه معتبر نیست.");
        else if (price is < 1_000_000) errors.Add("کرایه دست‌کم ۱۰۰,۰۰۰ تومان باشد.");
        else if (price is null && own) errors.Add("برای حمل با ناوگان خودتان، کرایه‌ای را که از مشتری می‌گیرید وارد کنید.");
        else if (price is null && mode == PriceMode.Fixed) errors.Add("در کرایهٔ ثابت، مبلغ کرایه لازم است.");

        var declared = Fa.ParseToman(f.DeclaredValueToman);
        if (!string.IsNullOrWhiteSpace(f.DeclaredValueToman) && declared is null) errors.Add("ارزش تقریبی بار معتبر نیست.");

        string? receiverMobile = null;
        if (!string.IsNullOrWhiteSpace(f.ReceiverMobile))
        {
            receiverMobile = Fa.NormMobile(f.ReceiverMobile);
            if (receiverMobile.Length == 0) errors.Add("موبایل گیرنده معتبر نیست.");
        }

        CompanyCustomer? customer = null;
        if (f.CustomerId is int cuId)
        {
            customer = await db.CompanyCustomers.FirstOrDefaultAsync(c => c.CompanyCustomerId == cuId && c.CompanyId == cid, ct);
            if (customer is null) errors.Add("مشتری انتخاب‌شده پیدا نشد.");
        }
        else if (!string.IsNullOrWhiteSpace(f.NewCustomerName))
        {
            var name = f.NewCustomerName.Trim();
            if (name.Length > 150) errors.Add("نام مشتری طولانی است.");
            string? cm = null;
            if (!string.IsNullOrWhiteSpace(f.NewCustomerMobile))
            {
                cm = Fa.NormMobile(f.NewCustomerMobile);
                if (cm.Length == 0) errors.Add("موبایل مشتری معتبر نیست.");
            }
            customer = new CompanyCustomer { CompanyId = cid, Name = name, Mobile = cm, Kind = f.NewCustomerKind == "person" ? "person" : "corporate" };
        }

        if (errors.Count > 0)
        {
            ViewBag.Errors = errors;
            await FillFormListsAsync(ct);
            return View(f);
        }

        if (customer is { CompanyCustomerId: 0 }) db.CompanyCustomers.Add(customer);

        // بارِ «ناوگان خودمان» پیش‌نویس ساخته می‌شود و بلافاصله با BookOwnLoad قطعی می‌شود؛
        // اگر آن گام به هر دلیل شکست بخورد، بار در بازار عمومی ظاهر نمی‌شود.
        var now = DateTime.UtcNow;
        var load = new Load
        {
            CompanyId = cid, ShipperId = null,
            OriginCityId = origin!.CityId, OriginAddress = f.OriginAddress!.Trim(), OriginLat = origin.Lat, OriginLng = origin.Lng,
            DestCityId = dest!.CityId, DestAddress = f.DestAddress!.Trim(), DestLat = dest.Lat, DestLng = dest.Lng,
            CargoType = cargo, Title = title, Packaging = string.IsNullOrWhiteSpace(f.Packaging) ? null : f.Packaging.Trim(),
            WeightTon = weight!.Value, VolumeM3 = volume, VehicleTypeId = f.VehicleTypeId,
            LoadingFrom = loadingFrom!.Value,
            PriceMode = mode, Price = price, DeclaredValue = declared, InsuranceRequested = f.InsuranceRequested,
            Description = string.IsNullOrWhiteSpace(f.Description) ? null : f.Description.Trim(),
            ReceiverName = string.IsNullOrWhiteSpace(f.ReceiverName) ? null : f.ReceiverName.Trim(),
            ReceiverMobile = receiverMobile,
            DistanceKm = Geo.RoadKm(origin.Lat, origin.Lng, dest.Lat, dest.Lng),
            Status = own ? LoadStatus.Draft : LoadStatus.Open,
            PublishedAt = own ? null : now,
            ExpiresAt = own ? null : loadingFrom.Value.AddDays(2),
            CreatedAt = now
        };
        db.Loads.Add(load);
        await db.SaveChangesAsync(ct);

        load.Code = Codes.Make("L", load.LoadId, load.CreatedAt);
        if (customer is not null) CompanyOps.LinkCustomer(audit, load.LoadId, customer);
        // کد تحویل همان لحظهٔ ثبت ساخته و به گیرنده/مشتری پیامک می‌شود
        if (!string.IsNullOrEmpty(load.ReceiverMobile ?? customer?.Mobile))
        {
            load.ReceiverMobile ??= customer?.Mobile;
            await flow.IssueLoadDeliveryCodeAsync(load, ct);
        }
        await db.SaveChangesAsync(ct);

        if (!own)
        {
            TempData["ok"] = $"بار {load.Code} در بازار بارگو منتشر شد. پیشنهادهای حمل‌کنندگان در همین صفحه نمایش داده می‌شود.";
            return RedirectToAction(nameof(Detail), new { id = load.LoadId });
        }

        try
        {
            var trip = await flow.BookOwnLoadAsync(load.LoadId, me.ToActor(), ct);
            TempData["ok"] = $"بار {load.Code} ثبت و سفر {trip.Code} ساخته شد. راننده و خودرو را تخصیص دهید.";
            return await CompanyOps.HasAsync(db, me, CompanyPermission.Dispatch, ct)
                ? RedirectToAction("Assign", "Dispatch", new { id = trip.TripId })
                : RedirectToAction(nameof(Index), new { tab = "unassigned" });
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
            return RedirectToAction(nameof(Detail), new { id = load.LoadId });
        }
    }

    private async Task FillFormListsAsync(CancellationToken ct)
    {
        ViewBag.Cities = await CompanyOps.CitiesAsync(db, ct);
        ViewBag.VehicleTypes = await db.VehicleTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToListAsync(ct);
        ViewBag.Customers = await db.CompanyCustomers.AsNoTracking()
            .Where(c => c.CompanyId == me.CompanyId).OrderBy(c => c.Name)
            .Select(c => new OpsOption(c.CompanyCustomerId, c.Mobile == null ? c.Name : c.Name + " — " + c.Mobile))
            .ToListAsync(ct);
    }

    // =====================================================================
    //  بارهای شرکت (بر پایهٔ سفر) + بارهای منتشرشدهٔ خود شرکت
    // =====================================================================

    public async Task<IActionResult> Index(string? tab, int page = 1, CancellationToken ct = default)
    {
        var cid = me.CompanyId;
        tab = tab is "active" or "done" or "cancelled" or "published" ? tab : "unassigned";
        ViewData["Title"] = tab switch
        {
            "active" => "بارهای فعال",
            "done" => "بارهای تکمیل‌شده",
            "cancelled" => "بارهای لغوشده",
            "published" => "بارهای منتشرشده در بازار",
            _ => "بارهای در انتظار تخصیص"
        };

        var byStatus = await db.Trips.AsNoTracking().Where(t => t.CompanyId == cid)
            .GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        int Sum(Func<string, bool> p) => byStatus.Where(x => p(x.Key)).Sum(x => x.N);

        var ownLoads = db.Loads.AsNoTracking().Where(l => l.CompanyId == cid && OwnUnbooked.Contains(l.Status));
        var vm = new OpsLoadTripsVm { Tab = tab };
        vm.Counts["unassigned"] = Sum(s => Unassigned.Contains(s));
        vm.Counts["active"] = Sum(s => s == TripStatus.Accepted || TripStatus.Live.Contains(s));
        vm.Counts["done"] = Sum(s => TripStatus.Done.Contains(s));
        vm.Counts["cancelled"] = Sum(s => s == TripStatus.Cancelled);
        vm.Counts["published"] = await ownLoads.CountAsync(ct);

        if (tab == "published")
        {
            vm.Loads = await PageVm<Load>.FromAsync(
                ownLoads.Include(l => l.OriginCity).Include(l => l.DestCity).Include(l => l.VehicleType).OrderByDescending(l => l.CreatedAt),
                page, PageLink.For(Request), ct: ct);
            var ids = vm.Loads.Rows.Select(l => l.LoadId).ToList();
            vm.OfferCounts = await db.Offers.AsNoTracking()
                .Where(o => ids.Contains(o.LoadId) && o.Status == OfferStatus.Pending)
                .GroupBy(o => o.LoadId).Select(g => new { g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.N, ct);
            vm.Customers = await CompanyOps.CustomersOfLoadsAsync(db, cid, ids, ct);
            return View(vm);
        }

        var q = db.Trips.AsNoTracking().Where(t => t.CompanyId == cid);
        q = tab switch
        {
            "active" => q.Where(t => t.Status == TripStatus.Accepted || TripStatus.Live.Contains(t.Status)),
            "done" => q.Where(t => TripStatus.Done.Contains(t.Status)),
            "cancelled" => q.Where(t => t.Status == TripStatus.Cancelled),
            _ => q.Where(t => Unassigned.Contains(t.Status))
        };
        var ordered = tab switch
        {
            "unassigned" => q.OrderBy(t => t.ScheduledDepartureAt),
            "done" => q.OrderByDescending(t => t.DeliveredAt),
            "cancelled" => q.OrderByDescending(t => t.CancelledAt),
            _ => q.OrderByDescending(t => t.StartedAt ?? t.CreatedAt)
        };
        vm.Trips = await PageVm<Trip>.FromAsync(
            ordered.Include(t => t.Load).ThenInclude(l => l!.OriginCity)
                .Include(t => t.Load).ThenInclude(l => l!.DestCity)
                .Include(t => t.Load).ThenInclude(l => l!.Shipper)
                .Include(t => t.Load).ThenInclude(l => l!.Company)
                .Include(t => t.Driver).Include(t => t.Vehicle),
            page, PageLink.For(Request), ct: ct);
        vm.Customers = await CompanyOps.CustomersOfLoadsAsync(db, cid,
            vm.Trips.Rows.Where(t => t.Load!.CompanyId == cid).Select(t => t.LoadId), ct);
        return View(vm);
    }

    // =====================================================================
    //  بارهای دریافتی: درخواست‌های مستقیم + پیشنهادهای شرکت
    // =====================================================================

    public async Task<IActionResult> Incoming(string? offers, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارهای دریافتی";
        var cid = me.CompanyId;
        var vm = new OpsIncomingVm
        {
            StatusFilter = offers is OfferStatus.Accepted or OfferStatus.Rejected or OfferStatus.Withdrawn or "all" ? offers : OfferStatus.Pending
        };

        vm.Requests = await db.Loads.AsNoTracking().Market(cid)
            .Where(l => l.TargetCompanyId == cid)
            .OrderBy(l => l.LoadingFrom)
            .Select(Card(cid))
            .ToListAsync(ct);

        var mine = db.Offers.AsNoTracking().Where(o => o.CompanyId == cid);
        vm.OfferCounts = await mine.GroupBy(o => o.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var oq = vm.StatusFilter == "all" ? mine : mine.Where(o => o.Status == vm.StatusFilter);
        vm.Offers = await PageVm<Offer>.FromAsync(
            oq.Include(o => o.Load).ThenInclude(l => l!.OriginCity)
                .Include(o => o.Load).ThenInclude(l => l!.DestCity)
                .Include(o => o.Vehicle)
                .OrderByDescending(o => o.CreatedAt),
            page, PageLink.For(Request), ct: ct);

        var acceptedIds = vm.Offers.Rows.Where(o => o.Status == OfferStatus.Accepted).Select(o => o.OfferId).ToList();
        if (acceptedIds.Count > 0)
        {
            var trips = await db.Trips.AsNoTracking()
                .Where(t => t.OfferId != null && acceptedIds.Contains(t.OfferId.Value) && t.CompanyId == cid)
                .Select(t => new { OfferId = t.OfferId!.Value, t.TripId })
                .ToListAsync(ct);
            vm.TripOfOffer = trips.GroupBy(x => x.OfferId).ToDictionary(g => g.Key, g => g.Max(x => x.TripId));
        }
        return View(vm);
    }

    // =====================================================================
    //  بازار بار
    // =====================================================================

    public async Task<IActionResult> Market(int? origin, int? dest, int? vehicleTypeId, DateTime? date, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بازار بار";
        var cid = me.CompanyId;

        var q = db.Loads.AsNoTracking().Market(cid).Where(l => l.TargetCompanyId == null && l.CompanyId != cid);
        if (origin is int op) q = q.Where(l => l.OriginCity!.ProvinceId == op);
        if (dest is int dp) q = q.Where(l => l.DestCity!.ProvinceId == dp);
        if (vehicleTypeId is int vt) q = q.Where(l => l.VehicleTypeId == vt);
        if (date is DateTime d)
        {
            var dayStart = Fa.ToUtc(Fa.ToTehran(d).Date);
            q = q.Where(l => l.LoadingFrom >= dayStart);
        }

        var vm = new OpsMarketVm
        {
            Origin = origin, Dest = dest, VehicleTypeId = vehicleTypeId, Date = date,
            Page = await PageVm<OpsLoadCard>.FromAsync(q.OrderBy(l => l.LoadingFrom).ThenByDescending(l => l.LoadId).Select(Card(cid)),
                page, PageLink.For(Request), pageSize: 24, ct: ct),
            Provinces = await db.Provinces.AsNoTracking().OrderBy(p => p.ProvinceId).ToListAsync(ct),
            VehicleTypes = await db.VehicleTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToListAsync(ct),
            DirectCount = await db.Loads.Market(cid).CountAsync(l => l.TargetCompanyId == cid, ct)
        };
        return View(vm);
    }

    // =====================================================================
    //  جزئیات بار
    // =====================================================================

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var load = await db.Loads.AsNoTracking()
            .Include(l => l.OriginCity).ThenInclude(c => c!.Province)
            .Include(l => l.DestCity).ThenInclude(c => c!.Province)
            .Include(l => l.VehicleType).Include(l => l.Shipper).Include(l => l.Company).Include(l => l.Photos)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.LoadId == id, ct);
        if (load is null) return NotFound();

        var own = load.CompanyId == cid;
        var now = DateTime.UtcNow;
        var inMarket = LoadStatus.Market.Contains(load.Status)
                       && (load.ExpiresAt == null || load.ExpiresAt > now)
                       && (load.TargetCompanyId == null || load.TargetCompanyId == cid);

        var myOffers = new List<Offer>();
        if (!own)
            myOffers = await db.Offers.AsNoTracking().Include(o => o.Vehicle)
                .Where(o => o.LoadId == id && o.CompanyId == cid)
                .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);

        var trips = await db.Trips.AsNoTracking()
            .Include(t => t.Driver).Include(t => t.Vehicle).Include(t => t.Company)
            .Where(t => t.LoadId == id && (own || t.CompanyId == cid))
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

        // بارِ شرکتی دیگر یا درخواست مستقیمِ شرکت دیگر: فقط اگر پیشنهاد یا سفری از ما رویش هست
        if (!own && !inMarket && myOffers.Count == 0 && trips.Count == 0) return NotFound();

        ViewData["Title"] = $"بار {load.Code}";
        var vm = new OpsLoadDetailVm
        {
            Load = load, IsOwn = own, Trips = trips, MyOffers = myOffers,
            MyPendingOffer = myOffers.FirstOrDefault(o => o.Status == OfferStatus.Pending),
            CanOffer = !own && inMarket,
            CommissionPercent = await settings.GetDecimalAsync(SettingsService.Keys.CommissionPercent, ct),
            CanDispatch = await CompanyOps.HasAsync(db, me, CompanyPermission.Dispatch, ct)
        };

        if (own)
        {
            vm.Offers = await db.Offers.AsNoTracking()
                .Include(o => o.Driver).Include(o => o.Company).Include(o => o.Vehicle).ThenInclude(v => v!.VehicleType)
                .Where(o => o.LoadId == id)
                .OrderBy(o => o.Status == OfferStatus.Pending ? 0 : 1).ThenBy(o => o.Amount)
                .AsSplitQuery().ToListAsync(ct);
            vm.Customer = (await CompanyOps.CustomersOfLoadsAsync(db, cid, [id], ct)).GetValueOrDefault(id);
        }
        else if (vm.CanOffer)
        {
            vm.Vehicles = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType)
                .Where(v => v.CompanyId == cid && v.Status == VehicleStatus.Active)
                .OrderBy(v => v.CapacityTon).ThenBy(v => v.PlateNo).ToListAsync(ct);
        }
        return View(vm);
    }

    // ---------------- پیشنهاد روی بار بازار / درخواست مستقیم ----------------

    [HttpPost, ActionName("Offer")]
    public async Task<IActionResult> SendOffer(int id, string? amountToman, int? etaHours, int? vehicleId, string? note, CancellationToken ct)
    {
        if (etaHours is < 0 or > 720)
        {
            TempData["err"] = "زمان رسیدن به مبدا باید بین ۰ و ۷۲۰ ساعت باشد.";
            return RedirectToAction(nameof(Detail), new { id });
        }
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > 500 }) note = note[..500];

        try
        {
            var offer = await flow.PlaceOfferAsync(id, me.ToActor(), Fa.ParseToman(amountToman), etaHours, note, vehicleId, ct);
            TempData["ok"] = $"پیشنهاد شما به مبلغ {Fa.Toman(offer.Amount)} ثبت شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Withdraw(int offerId, string? back, CancellationToken ct)
    {
        var loadId = await db.Offers.Where(o => o.OfferId == offerId && o.CompanyId == me.CompanyId)
            .Select(o => (int?)o.LoadId).FirstOrDefaultAsync(ct);
        if (loadId is null) return NotFound();
        try
        {
            await flow.WithdrawOfferAsync(offerId, me.ToActor(), ct);
            TempData["ok"] = "پیشنهاد پس گرفته شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return back == "incoming" ? RedirectToAction(nameof(Incoming)) : RedirectToAction(nameof(Detail), new { id = loadId });
    }

    // ---------------- پیشنهادهای رسیده روی بارِ خود شرکت ----------------

    [HttpPost]
    public async Task<IActionResult> Accept(int offerId, CancellationToken ct)
    {
        var loadId = await OwnOfferLoadAsync(offerId, ct);
        if (loadId is null) return NotFound();
        try
        {
            var trip = await flow.AcceptOfferAsync(offerId, me.ToActor(), ct);
            TempData["ok"] = $"پیشنهاد پذیرفته شد و سفر {trip.Code} ساخته شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id = loadId });
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int offerId, CancellationToken ct)
    {
        var loadId = await OwnOfferLoadAsync(offerId, ct);
        if (loadId is null) return NotFound();
        try
        {
            await flow.RejectOfferAsync(offerId, me.ToActor(), ct);
            TempData["ok"] = "پیشنهاد رد شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id = loadId });
    }

    private Task<int?> OwnOfferLoadAsync(int offerId, CancellationToken ct)
    {
        var cid = me.CompanyId;
        return db.Offers.Where(o => o.OfferId == offerId && o.Load!.CompanyId == cid).Select(o => (int?)o.LoadId).FirstOrDefaultAsync(ct);
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct)
    {
        var cid = me.CompanyId;
        if (!await db.Loads.AnyAsync(l => l.LoadId == id && l.CompanyId == cid, ct)) return NotFound();
        try
        {
            await flow.CancelLoadAsync(id, me.ToActor(), reason?.Trim() ?? "", ct);
            TempData["ok"] = "بار لغو شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    /// <summary>بارِ منتشرشده یا پیش‌نویسِ خود شرکت را با ناوگان خود حمل کن.</summary>
    [HttpPost]
    public async Task<IActionResult> BookOwn(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        if (!await db.Loads.AnyAsync(l => l.LoadId == id && l.CompanyId == cid, ct)) return NotFound();
        try
        {
            var trip = await flow.BookOwnLoadAsync(id, me.ToActor(), ct);
            TempData["ok"] = $"سفر {trip.Code} ساخته شد. راننده و خودرو را تخصیص دهید.";
            if (await CompanyOps.HasAsync(db, me, CompanyPermission.Dispatch, ct))
                return RedirectToAction("Assign", "Dispatch", new { id = trip.TripId });
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }
}
