using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// ثبت و مدیریت بار. ثبت بار یک فرمِ بخش‌بندی‌شده است (منوی «ثبت بار جدید» با
/// ?section= به هر بخش می‌پرد). بار فقط هنگام «ساخته شدن» وضعیت می‌گیرد (پیش‌نویس یا
/// منتشرشده)؛ هر تغییر بعدی — پیشنهاد، قطعی شدن، لغو — از TripFlow می‌گذرد.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class LoadsController(BargoDbContext db, CurrentUser me, TripFlow flow, DocumentStorage storage, NotificationService notify) : Controller
{
    private const int MaxPhotos = 6;
    private static readonly HashSet<string> PhotoExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private static readonly string[] SelectedSteps = TripStatus.Upcoming;
    private static readonly string[] LoadingSteps = [TripStatus.ToOrigin, TripStatus.AtOrigin, TripStatus.Loaded];
    private static readonly string[] TransitSteps = [TripStatus.InTransit, TripStatus.Arrived, TripStatus.Unloaded];
    private static readonly string[] WaitingStatuses = [LoadStatus.Open, LoadStatus.Draft];
    private static readonly string[] ClosedStatuses = [LoadStatus.Cancelled, LoadStatus.Expired];

    public static readonly (string Key, string Label, string Icon)[] Tabs =
    [
        ("waiting", "در انتظار راننده", "bi-hourglass"),
        ("offering", "در حال دریافت پیشنهاد", "bi-tags"),
        ("selected", "راننده انتخاب‌شده", "bi-person-check"),
        ("loading", "در حال بارگیری", "bi-box-arrow-in-down"),
        ("transit", "در حال حمل", "bi-truck"),
        ("delivered", "تحویل‌شده", "bi-check2-all"),
        ("cancelled", "لغوشده", "bi-x-octagon"),
    ];

    private static IQueryable<Load> Filter(IQueryable<Load> q, string? status) => status switch
    {
        "waiting" => q.Where(l => WaitingStatuses.Contains(l.Status)),
        "offering" => q.Where(l => l.Status == LoadStatus.Offering),
        "selected" => q.Where(l => l.Status == LoadStatus.Booked && l.Trips.Any(t => SelectedSteps.Contains(t.Status))),
        "loading" => q.Where(l => l.Status == LoadStatus.Booked && l.Trips.Any(t => LoadingSteps.Contains(t.Status))),
        "transit" => q.Where(l => l.Status == LoadStatus.Booked && l.Trips.Any(t => TransitSteps.Contains(t.Status))),
        "delivered" => q.Where(l => l.Status == LoadStatus.Completed || l.Trips.Any(t => TripStatus.Done.Contains(t.Status))),
        "cancelled" => q.Where(l => ClosedStatuses.Contains(l.Status)),
        _ => q
    };

    // ------------------------------------------------------------------ فهرست

    public async Task<IActionResult> Index(string? status, string? q, int page = 1, CancellationToken ct = default)
    {
        if (status is not null && Tabs.All(t => t.Key != status)) status = null;
        ViewData["Title"] = status is null ? "بارهای من" : "بارهای من — " + Tabs.First(t => t.Key == status).Label;

        var mine = db.Loads.AsNoTracking().OwnedBy(me.ToActor());
        var counts = new Dictionary<string, int> { ["all"] = await mine.CountAsync(ct) };
        foreach (var t in Tabs) counts[t.Key] = await Filter(mine, t.Key).CountAsync(ct);

        var query = Filter(mine, status);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = Fa.Latin(q).Trim();
            query = query.Where(l => l.Code.Contains(term) || l.Title.Contains(term) || l.CargoType.Contains(term) ||
                                     l.OriginCity!.Name.Contains(term) || l.DestCity!.Name.Contains(term));
        }

        var vm = await PageVm<ShipperLoadRow>.FromAsync(
            query.OrderByDescending(l => l.LoadId).Select(ShipperLoadRow.FromLoad), page, PageLink.For(Request), 20, ct);

        ViewBag.Status = status;
        ViewBag.Q = q;
        ViewBag.Counts = counts;
        return View(vm);
    }

    // ------------------------------------------------------------------ ثبت بار

    [HttpGet]
    public async Task<IActionResult> Create(int? copyOf, CancellationToken ct)
    {
        ViewData["Title"] = "ثبت بار جدید";
        var vm = new ShipperLoadForm { LoadingDate = DateTime.UtcNow.AddDays(1) };

        if (copyOf is int src)
        {
            var l = await db.Loads.AsNoTracking().OwnedBy(me.ToActor()).FirstOrDefaultAsync(x => x.LoadId == src, ct);
            if (l is null) return NotFound();
            var known = ShipperLoadForm.CargoTypes.Contains(l.CargoType);
            vm = new ShipperLoadForm
            {
                CopyOf = l.LoadId,
                Title = l.Title,
                CargoType = known ? l.CargoType : "other",
                CargoTypeOther = known ? null : l.CargoType,
                Packaging = l.Packaging,
                OriginCityId = l.OriginCityId, OriginAddress = l.OriginAddress,
                OriginLat = ShipperUi.Box(l.OriginLat), OriginLng = ShipperUi.Box(l.OriginLng),
                DestCityId = l.DestCityId, DestAddress = l.DestAddress,
                DestLat = ShipperUi.Box(l.DestLat), DestLng = ShipperUi.Box(l.DestLng),
                // تاریخ بارِ قبلی معمولاً گذشته است؛ فردا با همان ساعت
                LoadingDate = l.LoadingFrom > DateTime.UtcNow ? l.LoadingFrom : DateTime.UtcNow.AddDays(1),
                LoadingHour = ShipperUi.TehranHour(l.LoadingFrom),
                VehicleTypeId = l.VehicleTypeId,
                WeightTon = ShipperUi.Box(l.WeightTon), VolumeM3 = ShipperUi.Box(l.VolumeM3),
                LengthM = ShipperUi.Box(l.LengthM), WidthM = ShipperUi.Box(l.WidthM), HeightM = ShipperUi.Box(l.HeightM),
                PriceMode = l.PriceMode,
                PriceToman = ShipperUi.TomanBox(l.Price),
                DeclaredValueToman = ShipperUi.TomanBox(l.DeclaredValue),
                Description = l.Description,
                InsuranceRequested = l.InsuranceRequested,
                InsuranceType = l.InsuranceType,
                InsuranceAmountToman = ShipperUi.TomanBox(l.InsuranceAmount),
                ReceiverName = l.ReceiverName, ReceiverMobile = l.ReceiverMobile,
                TargetCompanyId = l.TargetCompanyId
            };
            ViewBag.CopyCode = l.Code;
        }

        ViewBag.Lists = await ListsAsync(ct);
        return View(vm);
    }

    [HttpPost]
    [RequestSizeLimit(60 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 60 * 1024 * 1024)]
    public async Task<IActionResult> Create(ShipperLoadForm vm, List<IFormFile>? photos, string? intent, CancellationToken ct)
    {
        ViewData["Title"] = "ثبت بار جدید";
        var draft = intent == "draft";
        var now = DateTime.UtcNow;

        // ---- بار ----
        var cargo = (vm.CargoType == "other" ? vm.CargoTypeOther : vm.CargoType)?.Trim() ?? "";
        var title = vm.Title?.Trim() ?? "";
        if (!draft && cargo.Length == 0) ModelState.AddModelError(nameof(vm.CargoType), "نوع بار را انتخاب کنید یا بنویسید.");
        if (cargo.Length > 60) ModelState.AddModelError(nameof(vm.CargoType), "نوع بار حداکثر ۶۰ نویسه باشد.");
        if (title.Length > 150) ModelState.AddModelError(nameof(vm.Title), "عنوان بار حداکثر ۱۵۰ نویسه باشد.");
        var packaging = vm.Packaging?.Trim();
        if (packaging is { Length: > 40 }) ModelState.AddModelError(nameof(vm.Packaging), "بسته‌بندی حداکثر ۴۰ نویسه باشد.");

        // ---- مسیر ----
        var cityIds = new[] { vm.OriginCityId ?? 0, vm.DestCityId ?? 0 };
        var cities = await db.Cities.AsNoTracking().Where(c => cityIds.Contains(c.CityId) && c.IsActive).ToListAsync(ct);
        var origin = cities.FirstOrDefault(c => c.CityId == vm.OriginCityId);
        var dest = cities.FirstOrDefault(c => c.CityId == vm.DestCityId);
        if (origin is null) ModelState.AddModelError(nameof(vm.OriginCityId), "شهر مبدا را انتخاب کنید.");
        if (dest is null) ModelState.AddModelError(nameof(vm.DestCityId), "شهر مقصد را انتخاب کنید.");
        var originAddress = vm.OriginAddress?.Trim() ?? "";
        var destAddress = vm.DestAddress?.Trim() ?? "";
        if (!draft && originAddress.Length < 5) ModelState.AddModelError(nameof(vm.OriginAddress), "نشانی محل بارگیری را کامل بنویسید.");
        if (!draft && destAddress.Length < 5) ModelState.AddModelError(nameof(vm.DestAddress), "نشانی محل تخلیه را کامل بنویسید.");
        if (origin is not null && dest is not null && origin.CityId == dest.CityId &&
            originAddress.Length > 0 && originAddress == destAddress)
            ModelState.AddModelError(nameof(vm.DestAddress), "نشانی مقصد با مبدا یکی است.");
        var (oLat, oLng) = Coords(vm.OriginLat, vm.OriginLng, nameof(vm.OriginLat), "مبدا");
        var (dLat, dLng) = Coords(vm.DestLat, vm.DestLng, nameof(vm.DestLat), "مقصد");

        // ---- زمان ----
        DateTime loadingFrom = default;
        DateTime? loadingTo = null;
        if (vm.LoadingDate is null)
        {
            if (!ModelState.ContainsKey(nameof(vm.LoadingDate)) || ModelState[nameof(vm.LoadingDate)]!.Errors.Count == 0)
                ModelState.AddModelError(nameof(vm.LoadingDate), "تاریخ بارگیری را انتخاب کنید.");
        }
        else
        {
            loadingFrom = Fa.WithHour(vm.LoadingDate.Value, vm.LoadingHour);
            if (vm.LoadingDate.Value < Fa.TodayStartUtc)
                ModelState.AddModelError(nameof(vm.LoadingDate), "تاریخ بارگیری گذشته است.");
            else if (!draft && loadingFrom < now)
                ModelState.AddModelError(nameof(vm.LoadingDate), "ساعت بارگیری امروز گذشته است؛ ساعت یا روز دیگری انتخاب کنید.");
            else if (loadingFrom > now.AddDays(90))
                ModelState.AddModelError(nameof(vm.LoadingDate), "تاریخ بارگیری حداکثر تا ۹۰ روز آینده قابل ثبت است.");

            if (vm.LoadingToDate is DateTime to)
            {
                loadingTo = Fa.WithHour(to, 23);
                if (to < vm.LoadingDate.Value) ModelState.AddModelError(nameof(vm.LoadingToDate), "آخرین مهلت بارگیری نباید پیش از تاریخ بارگیری باشد.");
            }
        }
        if (vm.LoadingHour is < 0 or > 23) ModelState.AddModelError(nameof(vm.LoadingHour), "ساعت بارگیری معتبر نیست.");

        // ---- خودرو و وزن ----
        VehicleType? vType = null;
        if (vm.VehicleTypeId is int vt)
            vType = await db.VehicleTypes.AsNoTracking().FirstOrDefaultAsync(t => t.VehicleTypeId == vt && t.IsActive, ct);
        if (!draft && vType is null) ModelState.AddModelError(nameof(vm.VehicleTypeId), "نوع خودروی مورد نیاز را انتخاب کنید.");

        var weight = ShipperUi.ParseDecimal(vm.WeightTon);
        if (!string.IsNullOrWhiteSpace(vm.WeightTon) && weight is null) ModelState.AddModelError(nameof(vm.WeightTon), "وزن را به عدد بنویسید (مثلاً ۲۲ یا ۲/۵).");
        else if (!draft && weight is null or <= 0) ModelState.AddModelError(nameof(vm.WeightTon), "وزن بار را وارد کنید.");
        else if (weight is > 60) ModelState.AddModelError(nameof(vm.WeightTon), "وزن بار بیش از ۶۰ تن قابل ثبت نیست.");
        else if (weight is decimal w && vType is not null && vType.CapacityTon > 0 && w > vType.CapacityTon)
            ModelState.AddModelError(nameof(vm.WeightTon), $"وزن بار ({ShipperUi.Ton(w)}) از ظرفیت «{vType.Name}» ({ShipperUi.Ton(vType.CapacityTon)}) بیشتر است.");

        var volume = Dimension(vm.VolumeM3, nameof(vm.VolumeM3), "حجم", 150);
        var length = Dimension(vm.LengthM, nameof(vm.LengthM), "طول", 25);
        var width = Dimension(vm.WidthM, nameof(vm.WidthM), "عرض", 5);
        var height = Dimension(vm.HeightM, nameof(vm.HeightM), "ارتفاع", 6);

        // ---- قیمت و ارزش ----
        var mode = vm.PriceMode == PriceMode.Fixed ? PriceMode.Fixed : PriceMode.Negotiable;
        var price = Fa.ParseToman(vm.PriceToman);
        if (!string.IsNullOrWhiteSpace(vm.PriceToman) && price is null) ModelState.AddModelError(nameof(vm.PriceToman), "کرایه را فقط با رقم بنویسید.");
        else if (mode == PriceMode.Fixed && price is null && !draft) ModelState.AddModelError(nameof(vm.PriceToman), "در حالت «کرایه ثابت»، مبلغ کرایه لازم است.");
        else if (price is < 1_000_000) ModelState.AddModelError(nameof(vm.PriceToman), "کرایه دست‌کم ۱۰۰,۰۰۰ تومان باشد.");
        else if (price is > 100_000_000_000) ModelState.AddModelError(nameof(vm.PriceToman), "مبلغ کرایه بیش از حد مجاز است.");

        var declared = Fa.ParseToman(vm.DeclaredValueToman);
        if (!string.IsNullOrWhiteSpace(vm.DeclaredValueToman) && declared is null) ModelState.AddModelError(nameof(vm.DeclaredValueToman), "ارزش بار را فقط با رقم بنویسید.");
        else if (declared is > 10_000_000_000_000) ModelState.AddModelError(nameof(vm.DeclaredValueToman), "ارزش اعلامی بیش از حد مجاز است.");
        if (vm.InsuranceRequested && declared is null or 0 && !ModelState.ContainsKey(nameof(vm.DeclaredValueToman)))
            ModelState.AddModelError(nameof(vm.DeclaredValueToman), "برای بیمهٔ بار، ارزش تقریبی بار را وارد کنید.");

        // ---- بیمه: نوع و مبلغ — در بارنامه ثبت می‌شود ----
        var insuranceType = vm.InsuranceType?.Trim();
        if (insuranceType is { Length: > 40 }) ModelState.AddModelError(nameof(vm.InsuranceType), "نوع بیمه حداکثر ۴۰ نویسه باشد.");
        if (vm.InsuranceRequested && !draft && string.IsNullOrEmpty(insuranceType))
            ModelState.AddModelError(nameof(vm.InsuranceType), "نوع بیمهٔ بار را انتخاب کنید.");
        var insuranceAmount = Fa.ParseToman(vm.InsuranceAmountToman);
        if (!string.IsNullOrWhiteSpace(vm.InsuranceAmountToman) && insuranceAmount is null)
            ModelState.AddModelError(nameof(vm.InsuranceAmountToman), "مبلغ بیمه را فقط با رقم بنویسید.");

        // ---- توضیحات، گیرنده، شرکت ----
        var description = vm.Description?.Trim();
        if (description is { Length: > 2000 }) ModelState.AddModelError(nameof(vm.Description), "توضیحات حداکثر ۲۰۰۰ نویسه باشد.");
        var receiverName = vm.ReceiverName?.Trim();
        if (receiverName is { Length: > 100 }) ModelState.AddModelError(nameof(vm.ReceiverName), "نام گیرنده حداکثر ۱۰۰ نویسه باشد.");
        string? receiverMobile = null;
        if (!string.IsNullOrWhiteSpace(vm.ReceiverMobile))
        {
            receiverMobile = Fa.NormMobile(vm.ReceiverMobile);
            if (receiverMobile.Length == 0) ModelState.AddModelError(nameof(vm.ReceiverMobile), "موبایل گیرنده معتبر نیست.");
        }

        string? targetName = null;
        if (vm.TargetCompanyId is int cid)
        {
            targetName = await db.Companies.AsNoTracking()
                .Where(c => c.CompanyId == cid && c.Status == AccountStatus.Approved && c.AcceptsDirectRequests)
                .Select(c => c.Name).FirstOrDefaultAsync(ct);
            if (targetName is null) ModelState.AddModelError(nameof(vm.TargetCompanyId), "این شرکت درخواست مستقیم نمی‌پذیرد.");
        }

        // ---- تصاویر ----
        var files = (photos ?? new List<IFormFile>()).Where(f => f is { Length: > 0 }).ToList();
        if (files.Count > MaxPhotos) ModelState.AddModelError("photos", $"حداکثر {Fa.N(MaxPhotos)} تصویر.");
        foreach (var f in files)
        {
            if (!PhotoExt.Contains(Path.GetExtension(f.FileName)))
                ModelState.AddModelError("photos", $"«{f.FileName}» تصویر نیست (jpg، png یا webp).");
            else if (f.Length > DocumentStorage.MaxBytes)
                ModelState.AddModelError("photos", $"حجم «{f.FileName}» بیش از ۸ مگابایت است.");
        }

        if (!ModelState.IsValid)
        {
            TempData["err"] = files.Count > 0
                ? "فرم خطا دارد؛ موارد مشخص‌شده را اصلاح و تصاویر را دوباره انتخاب کنید."
                : "فرم خطا دارد؛ موارد مشخص‌شده را اصلاح کنید.";
            ViewBag.Lists = await ListsAsync(ct);
            return View(vm);
        }

        if (title.Length == 0)
            title = cargo.Length > 0 ? $"{cargo} — {origin!.Name} به {dest!.Name}" : $"بار {origin!.Name} به {dest!.Name}";

        oLat ??= origin!.Lat; oLng ??= origin!.Lng;
        dLat ??= dest!.Lat; dLng ??= dest!.Lng;

        var load = new Load
        {
            ShipperId = me.Id,
            TargetCompanyId = vm.TargetCompanyId,
            OriginCityId = origin!.CityId, OriginAddress = originAddress, OriginLat = oLat, OriginLng = oLng,
            DestCityId = dest!.CityId, DestAddress = destAddress, DestLat = dLat, DestLng = dLng,
            CargoType = cargo, Title = title, Packaging = string.IsNullOrWhiteSpace(packaging) ? null : packaging,
            WeightTon = weight ?? 0, VolumeM3 = volume, LengthM = length, WidthM = width, HeightM = height,
            VehicleTypeId = vType?.VehicleTypeId,
            LoadingFrom = loadingFrom, LoadingTo = loadingTo,
            PriceMode = mode, Price = price, DeclaredValue = declared, InsuranceRequested = vm.InsuranceRequested,
            InsuranceType = string.IsNullOrEmpty(insuranceType) ? null : insuranceType,
            InsuranceAmount = insuranceAmount,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            ReceiverName = string.IsNullOrWhiteSpace(receiverName) ? null : receiverName,
            ReceiverMobile = receiverMobile,
            DistanceKm = Geo.RoadKm(oLat, oLng, dLat, dLng),
            // ساخت بار — تنها جایی که وضعیت بار مستقیم تعیین می‌شود
            Status = draft ? LoadStatus.Draft : LoadStatus.Open,
            PublishedAt = draft ? null : now,
            ExpiresAt = draft ? null : loadingFrom.AddDays(2),
            CreatedAt = now
        };

        try
        {
            db.Loads.Add(load);
            await db.SaveChangesAsync(ct);
            load.Code = Codes.Make("L", load.LoadId, load.CreatedAt);

            foreach (var f in files)
            {
                var path = await storage.SaveAsync(f, "loads", ct);
                if (path is not null) db.LoadPhotos.Add(new LoadPhoto { LoadId = load.LoadId, FilePath = path });
            }

            if (!draft)
            {
                await AnnounceAsync(load, targetName, ct);
                // کد تحویل همین حالا ساخته و به گیرنده پیامک می‌شود؛ هنگام رسیدن بار به راننده می‌دهد
                await flow.IssueLoadDeliveryCodeAsync(load, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        catch (UserError e)
        {
            // بار ذخیره شده ولی تصویری ناموفق بود — بار را نگه می‌داریم و خبر می‌دهیم
            if (load.LoadId > 0)
            {
                if (load.Code.Length == 0) load.Code = Codes.Make("L", load.LoadId, load.CreatedAt);
                await db.SaveChangesAsync(ct);
                TempData["err"] = $"بار {load.Code} ثبت شد ولی بارگذاری تصویر ناموفق بود: {e.Message}";
                return RedirectToAction(nameof(Detail), new { id = load.LoadId });
            }
            TempData["err"] = e.Message;
            ViewBag.Lists = await ListsAsync(ct);
            return View(vm);
        }

        TempData["ok"] = draft
            ? $"پیش‌نویس بار {load.Code} ذخیره شد. هر زمان آماده بودید، آن را منتشر کنید."
            : targetName is not null
                ? $"بار {load.Code} ثبت و به‌صورت درخواست مستقیم برای «{targetName}» ارسال شد."
                : $"بار {load.Code} منتشر شد و برای رانندگان و شرکت‌های واجد شرایط نمایش داده می‌شود.";
        return RedirectToAction(nameof(Detail), new { id = load.LoadId });
    }

    // ------------------------------------------------------------------ جزئیات

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var load = await db.Loads.AsNoTracking().OwnedBy(me.ToActor())
            .Include(l => l.OriginCity).Include(l => l.DestCity).Include(l => l.VehicleType)
            .Include(l => l.Photos).Include(l => l.TargetCompany)
            .AsSplitQuery()
            .FirstOrDefaultAsync(l => l.LoadId == id, ct);
        if (load is null) return NotFound();
        ViewData["Title"] = $"بار {load.Code}";

        var offerQ = db.Offers.AsNoTracking().Where(o => o.LoadId == id);
        var totalOffers = await offerQ.CountAsync(o => o.Status != OfferStatus.Withdrawn, ct);
        var pendingOffers = await offerQ.CountAsync(o => o.Status == OfferStatus.Pending, ct);
        var lowestOffer = await offerQ.Where(o => o.Status == OfferStatus.Pending).MinAsync(o => (long?)o.Amount, ct);

        var trip = await db.Trips.AsNoTracking()
            .Include(t => t.Driver).Include(t => t.Company).Include(t => t.Vehicle).ThenInclude(v => v!.VehicleType)
            .Include(t => t.Waybill)
            .Where(t => t.LoadId == id && t.Status != TripStatus.Cancelled)
            .OrderByDescending(t => t.TripId)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);

        var cancelled = await db.Trips.AsNoTracking().VisibleTo(me.ToActor())
            .Where(t => t.LoadId == id && t.Status == TripStatus.Cancelled)
            .OrderByDescending(t => t.TripId).Select(ShipperTripRow.FromTrip).ToListAsync(ct);

        var pct = await InsurancePercentAsync(ct);

        var markers = new List<object>();
        if (load.OriginLat is double ola && load.OriginLng is double olg)
            markers.Add(new { lat = ola, lng = olg, kind = "origin", label = "مبدا: " + load.OriginCity?.Name });
        if (load.DestLat is double dla && load.DestLng is double dlg)
            markers.Add(new { lat = dla, lng = dlg, kind = "dest", label = "مقصد: " + load.DestCity?.Name });
        if (trip is { LastLat: double tla, LastLng: double tlg } && TripStatus.Live.Contains(trip.Status))
            markers.Add(new { lat = tla, lng = tlg, kind = "truck", label = "آخرین موقعیت خودرو" });

        var vm = new ShipperLoadDetailVm
        {
            Load = load,
            TotalOffers = totalOffers,
            PendingOffers = pendingOffers,
            LowestOffer = lowestOffer,
            Trip = trip,
            CancelledTrips = cancelled,
            InsurancePercent = pct,
            InsuranceEstimate = load.DeclaredValue is long dv && pct is decimal p ? (long)Math.Round(dv * p / 100m) : null,
            PublishProblems = load.Status == LoadStatus.Draft ? PublishProblems(load) : [],
            MapJson = markers.Count == 0 ? "" : System.Text.Json.JsonSerializer.Serialize(new { markers, fit = true })
        };
        return View(vm);
    }

    // ------------------------------------------------------------------ انتشار پیش‌نویس

    [HttpPost]
    public async Task<IActionResult> Publish(int id, CancellationToken ct)
    {
        var load = await db.Loads.OwnedBy(me.ToActor()).Include(l => l.VehicleType)
            .FirstOrDefaultAsync(l => l.LoadId == id, ct);
        if (load is null) return NotFound();

        if (load.Status != LoadStatus.Draft)
        {
            TempData["err"] = "فقط بارِ پیش‌نویس منتشر می‌شود.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        var problems = PublishProblems(load);
        if (problems.Count > 0)
        {
            TempData["err"] = "پیش از انتشار: " + string.Join("، ", problems) + " — از «ثبت دوباره» بار را کامل کنید.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        // پیش‌نویس هنوز در مرحلهٔ «ساخت بار» است؛ انتشار آن گذارِ ساختِ بار است نه گردش کار سفر
        var now = DateTime.UtcNow;
        load.Status = LoadStatus.Open;
        load.PublishedAt = now;
        load.ExpiresAt = load.LoadingFrom.AddDays(2);

        string? targetName = load.TargetCompanyId is int cid
            ? await db.Companies.Where(c => c.CompanyId == cid).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        await AnnounceAsync(load, targetName, ct);
        if (load.DeliveryCodeHash is null) await flow.IssueLoadDeliveryCodeAsync(load, ct);
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"بار {load.Code} منتشر شد و کد تحویل برای گیرنده پیامک شد.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    // ------------------------------------------------------------------ لغو

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct)
    {
        if (!await db.Loads.OwnedBy(me.ToActor()).AnyAsync(l => l.LoadId == id, ct)) return NotFound();
        try
        {
            await flow.CancelLoadAsync(id, me.ToActor(), reason?.Trim() ?? "", ct);
            TempData["ok"] = "بار لغو شد و پیشنهادهای در انتظار آن بسته شدند.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    // ------------------------------------------------------------------ کمکی‌ها

    /// <summary>کمبودهای بار برای انتشار (پیش‌نویس ممکن است ناقص ذخیره شده باشد).</summary>
    private static List<string> PublishProblems(Load l)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(l.CargoType)) p.Add("نوع بار مشخص نیست");
        if (l.OriginAddress.Trim().Length < 5) p.Add("نشانی مبدا ناقص است");
        if (l.DestAddress.Trim().Length < 5) p.Add("نشانی مقصد ناقص است");
        if (l.VehicleTypeId is null) p.Add("نوع خودرو انتخاب نشده");
        if (l.WeightTon <= 0) p.Add("وزن بار وارد نشده");
        if (l.PriceMode == PriceMode.Fixed && l.Price is null) p.Add("کرایهٔ ثابت وارد نشده");
        if (l.LoadingFrom < DateTime.UtcNow) p.Add("زمان بارگیری گذشته است");
        return p;
    }

    /// <summary>اعلان انتشار: درخواست مستقیم به شرکت، و بار عمومی به رانندگان منتخبِ این صاحب بار.</summary>
    private async Task AnnounceAsync(Load load, string? targetName, CancellationToken ct)
    {
        var from = await db.Cities.Where(c => c.CityId == load.OriginCityId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var to = await db.Cities.Where(c => c.CityId == load.DestCityId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var body = $"{from} ← {to} · {ShipperUi.Ton(load.WeightTon)} · بارگیری {Fa.Stamp(load.LoadingFrom)}";

        if (load.TargetCompanyId is int cid && targetName is not null)
        {
            notify.Add(OwnerKind.Company, cid, $"درخواست حمل مستقیم: بار {load.Code}", $"{me.Name} — {body}", "/Company/Loads/Incoming", "load");
            return;
        }

        var favorites = await db.FavoriteDrivers.AsNoTracking()
            .Where(f => f.ShipperId == me.Id && f.Driver!.Status == AccountStatus.Approved)
            .Select(f => f.DriverId).ToListAsync(ct);
        foreach (var d in favorites)
            notify.Add(OwnerKind.Driver, d, $"بار تازه از {me.Name}: {load.Code}", body, "/Driver/Loads", "load");
    }

    private (double? Lat, double? Lng) Coords(string? lat, string? lng, string key, string label)
    {
        if (string.IsNullOrWhiteSpace(lat) && string.IsNullOrWhiteSpace(lng)) return (null, null);
        var la = ShipperUi.ParseDouble(lat);
        var lo = ShipperUi.ParseDouble(lng);
        if (la is null || lo is null || la is < 24 or > 40 || lo is < 44 or > 64)
        {
            ModelState.AddModelError(key, $"مختصات {label} معتبر نیست (عرض ۲۴ تا ۴۰ و طول ۴۴ تا ۶۴) — یا هر دو را خالی بگذارید.");
            return (null, null);
        }
        return (la, lo);
    }

    private decimal? Dimension(string? input, string key, string label, decimal max)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var v = ShipperUi.ParseDecimal(input);
        if (v is null || v <= 0 || v > max)
        {
            ModelState.AddModelError(key, $"{label} باید عددی بزرگ‌تر از صفر و حداکثر {Fa.N(max)} باشد.");
            return null;
        }
        return v;
    }

    private Task<decimal?> InsurancePercentAsync(CancellationToken ct) =>
        db.Tariffs.AsNoTracking().Where(t => t.Key == "cargo_insurance" && t.IsActive).Select(t => t.Percent).FirstOrDefaultAsync(ct);

    private async Task<ShipperLoadFormLists> ListsAsync(CancellationToken ct) => new ShipperLoadFormLists
    {
        Cities = await db.Cities.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.ProvinceId).ThenBy(c => c.Name)
            .Select(c => new ShipperCityOption { CityId = c.CityId, Name = c.Name, Province = c.Province!.Name, Lat = c.Lat, Lng = c.Lng })
            .ToListAsync(ct),
        VehicleTypes = await db.VehicleTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToListAsync(ct),
        Addresses = await db.SavedAddresses.AsNoTracking().Include(a => a.City)
            .Where(a => a.ShipperId == me.Id).OrderBy(a => a.Title).ToListAsync(ct),
        Companies = await db.Companies.AsNoTracking()
            .Where(c => c.Status == AccountStatus.Approved && c.AcceptsDirectRequests)
            .OrderByDescending(c => c.RatingAvg).ThenBy(c => c.Name)
            .Select(c => new CompanyOption { CompanyId = c.CompanyId, Name = c.Name, City = c.City != null ? c.City.Name : null, RatingAvg = c.RatingAvg, RatingCount = c.RatingCount })
            .ToListAsync(ct),
        InsurancePercent = await InsurancePercentAsync(ct)
    };
}
