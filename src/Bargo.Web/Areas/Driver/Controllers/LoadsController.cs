using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// بازار بار از دید راننده: همهٔ بارها، جستجو، نزدیک من، پیشنهادی، ذخیره‌شده و صفحهٔ
/// بار با فرم پیشنهاد. ثبت و پس‌گرفتن پیشنهاد فقط از <see cref="TripFlow"/> است.
///
/// «نزدیک من» و «پیشنهادی» در حافظه مرتب می‌شوند (فاصلهٔ Haversine در SQL Server
/// بدون ستون geography قابل ایندکس نیست)؛ برای همین فقط ۳۰۰ بارِ تازهٔ بازار سنجیده
/// می‌شوند — بازار بارِ یک روز معمولاً کمتر از این است.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class LoadsController(BargoDbContext db, CurrentUser me, TripFlow flow, DriverReadiness readiness) : Controller
{
    private const int PageSize = 24;
    private const int ScanLimit = 300;
    private const double SuggestRadiusKm = 150;

    public Task<IActionResult> Index([FromQuery] LoadFilterVm filter, int page = 1, CancellationToken ct = default) =>
        ListAsync("all", filter, page, ct);

    /// <summary>همان فهرست با فرم فیلتر کامل (شهر، وزن، تاریخ بارگیری).</summary>
    public Task<IActionResult> Search([FromQuery] LoadFilterVm filter, int page = 1, CancellationToken ct = default) =>
        ListAsync("search", filter, page, ct);

    private async Task<IActionResult> ListAsync(string mode, LoadFilterVm f, int page, CancellationToken ct)
    {
        ViewData["Title"] = mode == "search" ? "جستجو و فیلتر بار" : "همه بارها";

        var q = db.Loads.AsNoTracking().Market();
        if (f.OriginCityId is int oc) q = q.Where(l => l.OriginCityId == oc);
        else if (f.OriginProvinceId is int op) q = q.Where(l => l.OriginCity!.ProvinceId == op);
        if (f.DestCityId is int dc) q = q.Where(l => l.DestCityId == dc);
        else if (f.DestProvinceId is int dp) q = q.Where(l => l.DestCity!.ProvinceId == dp);
        if (f.VehicleTypeId is int vt) q = q.Where(l => l.VehicleTypeId == vt);
        if (f.MaxWeight is decimal w && w > 0) q = q.Where(l => l.WeightTon <= w);
        if (f.FromDate is DateTime fd)
        {
            // «از تاریخ»: بارهایی که در این روز یا پس از آن هنوز قابل بارگیری‌اند
            var start = Fa.ToUtc(Fa.ToTehran(fd).Date);
            q = q.Where(l => (l.LoadingTo ?? l.LoadingFrom) >= start);
        }
        if (f.PriceMode == PriceMode.Fixed) q = q.Where(l => l.PriceMode == PriceMode.Fixed);
        else if (f.PriceMode == PriceMode.Negotiable) q = q.Where(l => l.PriceMode == PriceMode.Negotiable);

        q = f.Sort switch
        {
            "loading" => q.OrderBy(l => l.LoadingFrom),
            "price" => q.OrderByDescending(l => l.Price ?? 0),
            "weight" => q.OrderByDescending(l => l.WeightTon),
            _ => q.OrderByDescending(l => l.PublishedAt ?? l.CreatedAt)
        };

        var vm = await BaseVmAsync(mode, ct);
        vm.Filter = f;
        vm.Page = await PageVm<LoadCardVm>.FromAsync(q.ToCards(db, me.Id), page, PageLink.For(Request), PageSize, ct);
        var (lat, lng, live) = await db.PositionAsync(me.Id, ct);
        vm.HasPosition = lat is not null && lng is not null;
        vm.LivePosition = live;
        vm.Page.Rows.MeasureFrom(lat, lng);
        return View("Index", vm);
    }

    public async Task<IActionResult> Nearby(int? radius, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارهای نزدیک من";
        var vm = await BaseVmAsync("nearby", ct);
        var (lat, lng, live) = await db.PositionAsync(me.Id, ct);
        vm.HasPosition = lat is not null && lng is not null;
        vm.LivePosition = live;
        vm.Radius = radius is > 0 ? radius : null;

        var cards = await db.Loads.AsNoTracking().Market()
            .OrderByDescending(l => l.PublishedAt ?? l.CreatedAt).Take(ScanLimit)
            .ToCards(db, me.Id).ToListAsync(ct);
        cards.MeasureFrom(lat, lng);

        IEnumerable<LoadCardVm> sorted = vm.HasPosition
            ? cards.OrderBy(c => c.KmFromMe ?? double.MaxValue).ThenBy(c => c.LoadingFrom)
            : cards.OrderBy(c => c.LoadingFrom);
        if (vm.HasPosition && vm.Radius is int r)
            sorted = sorted.Where(c => c.KmFromMe is double k && k <= r);

        vm.Page = DriverQueries.PageInMemory(sorted.ToList(), page, PageLink.For(Request), PageSize);
        // هر پنج حالت فهرست بار (همه، جستجو، نزدیک، پیشنهادی، ذخیره‌شده) یک ویو دارند و Mode را از vm می‌خوانند
        return View("Index", vm);
    }

    /// <summary>
    /// پیشنهاد قاعده‌محور، نه «هوشمند»: نوع و ظرفیت خودروی فعال، مبدا تا ۱۵۰ کیلومتری
    /// راننده یا در استان خودش، و بارگیری در هفت روز آینده. هر کارت دلیلش را نشان می‌دهد
    /// تا راننده بداند چرا این بار را می‌بیند و چرا باری را نمی‌بیند.
    /// </summary>
    public async Task<IActionResult> Suggested(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارهای پیشنهادی به من";
        var vm = await BaseVmAsync("suggested", ct);

        var profile = await db.Drivers.AsNoTracking().Where(x => x.DriverId == me.Id)
            .Select(x => new { x.CompanyId, ProvinceId = (int?)x.City!.ProvinceId }).FirstAsync(ct);
        var vehicle = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType)
            .Where(v => v.DriverId == me.Id && v.Status == VehicleStatus.Active)
            .OrderByDescending(v => v.VehicleId).FirstOrDefaultAsync(ct);
        var (lat, lng, live) = await db.PositionAsync(me.Id, ct);
        vm.HasPosition = lat is not null && lng is not null;
        vm.LivePosition = live;

        var today = Fa.TodayStartUtc;
        var horizon = today.AddDays(8);
        var q = db.Loads.AsNoTracking().Market()
            .Where(l => (l.LoadingTo ?? l.LoadingFrom) >= today && l.LoadingFrom < horizon);
        if (vehicle is not null)
        {
            vm.VehicleLabel = $"{vehicle.VehicleType?.Name} — {vehicle.PlateNo}";
            vm.VehicleCapacity = vehicle.CapacityTon;
            var cap = vehicle.CapacityTon;
            var typeId = vehicle.VehicleTypeId;
            q = q.Where(l => l.WeightTon <= cap && (l.VehicleTypeId == null || l.VehicleTypeId == typeId));
        }
        else
        {
            vm.Note = profile.CompanyId is not null
                ? "خودروی شخصی ثبت نکرده‌اید؛ بارها بدون در نظر گرفتن نوع و ظرفیت خودرو پیشنهاد شده‌اند."
                : "خودروی فعالی ندارید؛ برای پیشنهاد دقیق، خودرو را در «مشخصات خودرو» ثبت یا فعال کنید.";
        }

        var cards = await q.OrderBy(l => l.LoadingFrom).Take(ScanLimit).ToCards(db, me.Id).ToListAsync(ct);
        cards.MeasureFrom(lat, lng);

        // بدون موقعیت و بدون شهرِ پروفایل، شرط مکان را نمی‌شود سنجید؛ آن را کنار می‌گذاریم و می‌گوییم
        var canLocate = vm.HasPosition || profile.ProvinceId is not null;
        if (!canLocate)
            vm.Note = (vm.Note is null ? "" : vm.Note + " ") + "موقعیت و شهر شما ثبت نشده است؛ فاصله تا مبدا سنجیده نشد.";

        var list = new List<LoadCardVm>();
        foreach (var c in cards)
        {
            var near = c.KmFromMe is double k && k <= SuggestRadiusKm;
            var sameProvince = profile.ProvinceId is int p && c.FromProvinceId == p;
            if (canLocate && !near && !sameProvince) continue;

            if (vehicle is not null)
            {
                if (c.VehicleTypeId == vehicle.VehicleTypeId)
                {
                    c.Score += 3;
                    c.Reasons.Add($"مناسب {vehicle.VehicleType?.Name}");
                }
                else
                {
                    c.Score += 1;
                    c.Reasons.Add("نوع خودرو آزاد");
                }
                c.Reasons.Add($"{Fa.N(c.WeightTon, 1)} تن از {Fa.N(vehicle.CapacityTon, 1)} تن ظرفیت");
                // باری که ظرفیت را پر می‌کند برای راننده صرفه دارد
                if (vehicle.CapacityTon > 0 && c.WeightTon >= vehicle.CapacityTon * 0.7m) c.Score += 1;
            }

            if (near)
            {
                c.Score += c.KmFromMe < 50 ? 3 : 2;
                c.Reasons.Add($"{Fa.N(c.KmFromMe!.Value)} کیلومتر تا مبدا");
            }
            else if (sameProvince)
            {
                c.Score += 1;
                c.Reasons.Add("مبدا در استان شما");
            }

            if (c.LoadingFrom < today.AddDays(3)) c.Score += 1;
            c.Reasons.Add(c.LoadingFrom <= DateTime.UtcNow ? "آمادهٔ بارگیری" : $"بارگیری {Fa.Date(c.LoadingFrom)}");
            list.Add(c);
        }

        var ordered = list.OrderByDescending(c => c.Score).ThenBy(c => c.LoadingFrom).ToList();
        vm.Page = DriverQueries.PageInMemory(ordered, page, PageLink.For(Request), PageSize);
        return View("Index", vm);
    }

    public async Task<IActionResult> Saved(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "بارهای ذخیره‌شده";
        var vm = await BaseVmAsync("saved", ct);
        var meId = me.Id;
        // بارِ ذخیره‌شده ممکن است دیگر در بازار نباشد؛ نشان داده می‌شود ولی بارهای بازار اول می‌آیند
        var q = db.Loads.AsNoTracking()
            .Where(l => l.TargetCompanyId == null && db.SavedLoads.Any(s => s.DriverId == meId && s.LoadId == l.LoadId))
            .OrderByDescending(l => l.Status == LoadStatus.Open || l.Status == LoadStatus.Offering)
            .ThenBy(l => l.LoadingFrom);
        vm.Page = await PageVm<LoadCardVm>.FromAsync(q.ToCards(db, meId), page, PageLink.For(Request), PageSize, ct);
        var (lat, lng, _) = await db.PositionAsync(meId, ct);
        vm.Page.Rows.MeasureFrom(lat, lng);
        return View("Index", vm);
    }

    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var meId = me.Id;
        // در بازار، یا باری که قبلاً رویش پیشنهاد داده‌ام یا نشانش کرده‌ام (تا تاریخچه‌ام باز شود)
        var load = await db.Loads.AsNoTracking()
            .Include(l => l.OriginCity).ThenInclude(c => c!.Province)
            .Include(l => l.DestCity).ThenInclude(c => c!.Province)
            .Include(l => l.VehicleType)
            .Include(l => l.Photos)
            .Where(l => l.LoadId == id)
            .Where(l => l.Offers.Any(o => o.DriverId == meId) ||
                        (l.TargetCompanyId == null &&
                         (LoadStatus.Market.Contains(l.Status) || db.SavedLoads.Any(s => s.DriverId == meId && s.LoadId == l.LoadId))))
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
        if (load is null) return NotFound();

        ViewData["Title"] = $"بار {load.Code}";
        var vm = new LoadDetailVm { Load = load };

        // صاحب بار: نام و امتیاز — موبایل پیش از قطعی شدن نمایش داده نمی‌شود
        if (load.ShipperId is int sid)
        {
            var s = await db.Shippers.AsNoTracking().Where(x => x.ShipperId == sid)
                .Select(x => new { x.Kind, x.FullName, x.BusinessName, x.RatingAvg, x.RatingCount }).FirstOrDefaultAsync(ct);
            if (s is not null)
            {
                vm.OwnerName = s.Kind == "business" && !string.IsNullOrWhiteSpace(s.BusinessName) ? s.BusinessName! : s.FullName;
                vm.OwnerRating = s.RatingAvg;
                vm.OwnerRatingCount = s.RatingCount;
            }
        }
        else if (load.CompanyId is int cid)
        {
            var c = await db.Companies.AsNoTracking().Where(x => x.CompanyId == cid)
                .Select(x => new { x.Name, x.RatingAvg, x.RatingCount }).FirstOrDefaultAsync(ct);
            vm.OwnerIsCompany = true;
            vm.OwnerName = c?.Name ?? "";
            vm.OwnerRating = c?.RatingAvg ?? 0;
            vm.OwnerRatingCount = c?.RatingCount ?? 0;
        }

        vm.PendingOffers = await db.Offers.CountAsync(o => o.LoadId == id && o.Status == OfferStatus.Pending, ct);
        vm.Saved = await db.SavedLoads.AnyAsync(s => s.DriverId == meId && s.LoadId == id, ct);
        vm.MyOffers = await db.Offers.AsNoTracking().Include(o => o.Vehicle)
            .Where(o => o.LoadId == id && o.DriverId == meId)
            .OrderByDescending(o => o.OfferId).ToListAsync(ct);

        var offerIds = vm.MyOffers.Select(o => o.OfferId).ToList();
        if (offerIds.Count > 0)
        {
            var trips = await db.Trips.AsNoTracking()
                .Where(t => t.OfferId != null && offerIds.Contains(t.OfferId.Value) && t.DriverId == meId)
                .Select(t => new { OfferId = t.OfferId!.Value, t.TripId }).ToListAsync(ct);
            vm.TripByOffer = trips.GroupBy(t => t.OfferId).ToDictionary(g => g.Key, g => g.Max(x => x.TripId));
        }

        vm.Vehicles = await db.Vehicles.AsNoTracking().Include(v => v.VehicleType)
            .Where(v => v.DriverId == meId)
            .OrderByDescending(v => v.Status == VehicleStatus.Active).ThenByDescending(v => v.VehicleId)
            .ToListAsync(ct);
        vm.Issues = await readiness.CheckAsync(meId, vm.DefaultVehicleId, ct);

        if (vm.Vehicles.FirstOrDefault(v => v.VehicleId == vm.DefaultVehicleId) is { } veh)
        {
            if (load.VehicleTypeId is int lt && lt != veh.VehicleTypeId)
                vm.Warnings.Add($"صاحب بار «{load.VehicleType?.Name}» خواسته و خودروی شما «{veh.VehicleType?.Name}» است.");
            if (load.WeightTon > veh.CapacityTon)
                vm.Warnings.Add($"وزن بار ({Fa.N(load.WeightTon, 1)} تن) از ظرفیت خودروی شما ({Fa.N(veh.CapacityTon, 1)} تن) بیشتر است.");
        }
        if (vm.InMarket && (load.LoadingTo ?? load.LoadingFrom) < DateTime.UtcNow)
            vm.Warnings.Add("زمان بارگیری اعلامی گذشته است؛ پیش از پیشنهاد، زمان را با صاحب بار هماهنگ کنید.");

        var (lat, lng, _) = await db.PositionAsync(meId, ct);
        vm.KmFromMe = Geo.Km(lat, lng, load.OriginLat ?? load.OriginCity?.Lat, load.OriginLng ?? load.OriginCity?.Lng) is double km
            ? Math.Round(km, 1) : null;

        return View(vm);
    }

    /// <summary>ثبت یا به‌روزرسانی پیشنهاد (در بار کرایه‌ثابت همان «درخواست حمل»).</summary>
    [HttpPost]
    public async Task<IActionResult> Offer(int id, string? amountToman, int? etaHours, string? note, int? vehicleId, CancellationToken ct)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > 500 }) note = note[..500];
        if (etaHours is < 0 or > 720)
        {
            TempData["err"] = "زمان رسیدن به مبدا باید بین ۰ و ۷۲۰ ساعت باشد.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        try
        {
            var offer = await flow.PlaceOfferAsync(id, me.ToActor(), Fa.ParseToman(amountToman), etaHours, note, vehicleId, ct);
            TempData["ok"] = $"پیشنهاد {Fa.Toman(offer.Amount)} ثبت شد و برای صاحب بار ارسال شد. نتیجه در «پیشنهادهای من» و اعلان‌ها نمایش داده می‌شود.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Save(int id, string? returnUrl, CancellationToken ct)
    {
        if (!await db.Loads.Market().AnyAsync(l => l.LoadId == id, ct)) return NotFound();
        if (!await db.SavedLoads.AnyAsync(s => s.DriverId == me.Id && s.LoadId == id, ct))
        {
            db.SavedLoads.Add(new SavedLoad { DriverId = me.Id, LoadId = id });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* دو کلیک هم‌زمان — ایندکس یکتا ردیف دوم را نپذیرفت */ }
        }
        TempData["ok"] = "بار در «بارهای ذخیره‌شده» نشان شد.";
        return Back(returnUrl, id);
    }

    [HttpPost]
    public async Task<IActionResult> Unsave(int id, string? returnUrl, CancellationToken ct)
    {
        await db.SavedLoads.Where(s => s.DriverId == me.Id && s.LoadId == id).ExecuteDeleteAsync(ct);
        TempData["ok"] = "بار از فهرست ذخیره‌شده‌ها برداشته شد.";
        return Back(returnUrl, id);
    }

    private IActionResult Back(string? returnUrl, int id) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Detail), new { id });

    private async Task<LoadListVm> BaseVmAsync(string mode, CancellationToken ct)
    {
        var issues = await readiness.CheckAsync(me.Id, null, ct);
        return new LoadListVm
        {
            Mode = mode,
            Provinces = await db.ProvinceListAsync(ct),
            Cities = await db.CityListAsync(ct),
            VehicleTypes = await db.VehicleTypeListAsync(ct),
            Blocking = issues.Where(i => i.Blocking).ToList()
        };
    }
}
