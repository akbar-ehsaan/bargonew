using System.Security.Cryptography;
using System.Text;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Services;

/// <summary>
/// گردش کار واحد بارگو — تنها جایی که وضعیت پیشنهاد، بار و سفر عوض می‌شود.
///
/// <code>
/// ثبت بار → انتشار → پیشنهاد (PlaceOffer) → انتخاب پیشنهاد (AcceptOffer: ساخت Trip)
///   → تخصیص راننده و خودرو (Assign — فقط سفر شرکتی) → قبول مأموریت → حرکت به مبدا
///   → حضور در مبدا → بارگیری → ثبت بارنامه (RegisterWaybill) → شروع حمل → رهگیری (RecordPoint)
///   → رسیدن → تخلیه (کد تحویل برای گیرنده) → تحویل با کد (ConfirmDelivery)
///   → تسویه و کمیسیون (Settle) → امتیازدهی (Rate)
/// </code>
///
/// کنترلرها هیچ‌وقت Status را مستقیم نمی‌نویسند. هر گذار یک TripEvent با
/// انجام‌دهنده، زمان و موقعیت می‌سازد که «پروندهٔ سفر» در پنل مدیر از آن ساخته می‌شود.
/// متدها خطای قابل نمایش را با <see cref="UserError"/> برمی‌گردانند.
/// </summary>
public class TripFlow(
    BargoDbContext db,
    WalletService wallet,
    NotificationService notify,
    SettingsService settings,
    DriverReadiness readiness,
    ISmsSender sms)
{
    private const string System = "system";

    // ------------------------------------------------------------------
    //  نقشهٔ گذارها: از هر وضعیت، به کجا و توسط چه نقشی
    // ------------------------------------------------------------------
    private static readonly Dictionary<string, (string To, string[] By)[]> Moves = new()
    {
        [TripStatus.AwaitingAssignment] =
        [
            (TripStatus.Assigned, new[] { Roles.Company, Roles.Admin }),
            (TripStatus.Cancelled, new[] { Roles.Company, Roles.Shipper, Roles.Admin }),
        ],
        [TripStatus.Assigned] =
        [
            (TripStatus.Accepted, new[] { Roles.Driver }),
            // راننده مأموریت را نپذیرفت — به صف تخصیص شرکت برمی‌گردد
            (TripStatus.AwaitingAssignment, new[] { Roles.Driver, Roles.Company }),
            (TripStatus.Cancelled, new[] { Roles.Company, Roles.Shipper, Roles.Admin }),
        ],
        [TripStatus.Accepted] =
        [
            (TripStatus.ToOrigin, new[] { Roles.Driver, Roles.Company }),
            (TripStatus.Cancelled, new[] { Roles.Driver, Roles.Company, Roles.Shipper, Roles.Admin }),
        ],
        [TripStatus.ToOrigin] =
        [
            (TripStatus.AtOrigin, new[] { Roles.Driver, Roles.Company }),
            (TripStatus.Cancelled, new[] { Roles.Driver, Roles.Company, Roles.Admin }),
        ],
        [TripStatus.AtOrigin] =
        [
            (TripStatus.Loaded, new[] { Roles.Driver, Roles.Company }),
            (TripStatus.Cancelled, new[] { Roles.Company, Roles.Admin }),
        ],
        [TripStatus.Loaded] = [(TripStatus.InTransit, new[] { Roles.Driver, Roles.Company }), (TripStatus.Cancelled, new[] { Roles.Admin })],
        [TripStatus.InTransit] = [(TripStatus.Arrived, new[] { Roles.Driver, Roles.Company }), (TripStatus.Cancelled, new[] { Roles.Admin })],
        [TripStatus.Arrived] = [(TripStatus.Unloaded, new[] { Roles.Driver, Roles.Company }), (TripStatus.Cancelled, new[] { Roles.Admin })],
        // تحویل از راننده فقط با کد گیرنده (ConfirmDeliveryAsync)؛ مدیر در اختلاف مستقیم ثبت می‌کند
        [TripStatus.Unloaded] = [(TripStatus.Delivered, new[] { Roles.Driver, Roles.Company, Roles.Admin }), (TripStatus.Cancelled, new[] { Roles.Admin })],
        [TripStatus.Delivered] = [(TripStatus.Settled, new[] { Roles.Admin, System })],
    };

    public static bool CanMove(string from, string to, string actorKind) =>
        Moves.TryGetValue(from, out var m) && m.Any(x => x.To == to && x.By.Contains(actorKind));

    /// <summary>گام‌های بعدی مجاز برای این نقش (بدون لغو) — دکمه‌های «عملیات سفر».</summary>
    public static IReadOnlyList<string> NextSteps(string status, string actorKind) =>
        Moves.TryGetValue(status, out var m)
            ? m.Where(x => x.By.Contains(actorKind) && x.To != TripStatus.Cancelled).Select(x => x.To).ToList()
            : [];

    public static bool CanCancel(string status, string actorKind) => CanMove(status, TripStatus.Cancelled, actorKind);

    /// <summary>متن دکمهٔ هر گام، از دید انجام‌دهنده.</summary>
    public static string ActionLabel(string to) => to switch
    {
        TripStatus.Assigned => "تخصیص راننده",
        TripStatus.Accepted => "قبول مأموریت",
        TripStatus.AwaitingAssignment => "رد مأموریت",
        TripStatus.ToOrigin => "حرکت به سمت مبدا",
        TripStatus.AtOrigin => "اعلام حضور در مبدا",
        TripStatus.Loaded => "تأیید بارگیری",
        TripStatus.InTransit => "شروع سفر",
        TripStatus.Arrived => "اعلام رسیدن به مقصد",
        TripStatus.Unloaded => "تأیید تخلیه",
        TripStatus.Delivered => "تأیید تحویل با کد",
        TripStatus.Settled => "تسویه",
        TripStatus.Cancelled => "لغو سفر",
        _ => TripStatus.Label(to)
    };

    // ------------------------------------------------------------------
    //  دسترسی
    // ------------------------------------------------------------------

    /// <summary>
    /// سفر با همهٔ وابستگی‌ها، فقط اگر این انجام‌دهنده حق دیدنش را دارد. شرط مالکیت
    /// داخل خودِ کوئری است؛ شناسهٔ حدس‌زده ردیفی برنمی‌گرداند.
    /// </summary>
    public Task<Trip?> FindForAsync(int tripId, Actor actor, CancellationToken ct = default)
    {
        var q = db.Trips
            .Include(t => t.Load).ThenInclude(l => l!.OriginCity)
            .Include(t => t.Load).ThenInclude(l => l!.DestCity)
            .Include(t => t.Load).ThenInclude(l => l!.Shipper)
            .Include(t => t.Driver)
            .Include(t => t.Vehicle).ThenInclude(v => v!.VehicleType)
            .Include(t => t.Company)
            .Include(t => t.Waybill)
            .Where(t => t.TripId == tripId);

        q = actor.Kind switch
        {
            Roles.Driver => q.Where(t => t.DriverId == actor.Id),
            Roles.Company => q.Where(t => t.CompanyId == actor.CompanyId || t.Load!.CompanyId == actor.CompanyId),
            Roles.Shipper => q.Where(t => t.Load!.ShipperId == actor.Id),
            Roles.Admin => q,
            _ => q.Where(_ => false)
        };
        return q.AsSplitQuery().FirstOrDefaultAsync(ct);
    }

    private static bool OwnsLoad(Load load, Actor actor) => actor.Kind switch
    {
        Roles.Shipper => load.ShipperId == actor.Id,
        Roles.Company => load.CompanyId == actor.CompanyId,
        Roles.Admin => true,
        _ => false
    };

    // ------------------------------------------------------------------
    //  پیشنهاد
    // ------------------------------------------------------------------

    /// <summary>ثبت یا به‌روزرسانی پیشنهاد راننده یا شرکت روی یک بار.</summary>
    public async Task<Offer> PlaceOfferAsync(int loadId, Actor actor, long? amount, int? etaHours, string? note,
        int? vehicleId, CancellationToken ct = default)
    {
        var load = await db.Loads.FirstOrDefaultAsync(l => l.LoadId == loadId, ct)
                   ?? throw new UserError("بار پیدا نشد.");
        if (!LoadStatus.Market.Contains(load.Status))
            throw new UserError("این بار دیگر پیشنهاد نمی‌پذیرد.");
        if (load.TargetCompanyId is int target && !(actor.Kind == Roles.Company && actor.CompanyId == target))
            throw new UserError("این درخواست حمل مستقیماً برای شرکت دیگری ارسال شده است.");

        int? driverId = null, companyId = null;
        if (actor.Kind == Roles.Driver)
        {
            if (vehicleId is int v && !await db.Vehicles.AnyAsync(x => x.VehicleId == v && x.DriverId == actor.Id, ct))
                throw new UserError("این خودرو متعلق به شما نیست.");
            var issues = await readiness.CheckAsync(actor.Id, vehicleId, ct);
            var block = issues.FirstOrDefault(i => i.Blocking);
            if (block is not null) throw new UserError(block.Text);
            driverId = actor.Id;
        }
        else if (actor.Kind == Roles.Company)
        {
            var status = await db.Companies.Where(c => c.CompanyId == actor.CompanyId).Select(c => c.Status).FirstOrDefaultAsync(ct);
            if (status != AccountStatus.Approved) throw new UserError("حساب شرکت هنوز تأیید نشده است.");
            if (load.CompanyId == actor.CompanyId) throw new UserError("روی بارِ خودِ شرکت نمی‌توان پیشنهاد داد.");
            if (vehicleId is int v && !await db.Vehicles.AnyAsync(x => x.VehicleId == v && x.CompanyId == actor.CompanyId, ct))
                throw new UserError("این خودرو متعلق به شرکت نیست.");
            companyId = actor.CompanyId;
        }
        else throw new UserError("فقط راننده یا شرکت حمل‌ونقل می‌تواند پیشنهاد بدهد.");

        // در بار «کرایه ثابت» مبلغ همان کرایهٔ اعلامی است و کاربر نمی‌تواند عوضش کند
        var finalAmount = load.PriceMode == PriceMode.Fixed && load.Price is long p ? p : amount ?? 0;
        if (finalAmount < 1_000_000) throw new UserError("مبلغ پیشنهاد معتبر نیست (حداقل ۱۰۰,۰۰۰ تومان).");

        var offer = await db.Offers.FirstOrDefaultAsync(o => o.LoadId == loadId && o.Status == OfferStatus.Pending &&
            (driverId != null ? o.DriverId == driverId : o.CompanyId == companyId), ct);

        var isNew = offer is null;
        offer ??= new Offer
        {
            LoadId = loadId,
            CarrierKind = actor.Kind == Roles.Company ? CarrierKind.Company : CarrierKind.Driver,
            DriverId = driverId,
            CompanyId = companyId
        };
        offer.Amount = finalAmount;
        offer.EtaHours = etaHours;
        offer.Note = note;
        offer.VehicleId = vehicleId;
        if (isNew) db.Offers.Add(offer);

        if (load.Status == LoadStatus.Open) load.Status = LoadStatus.Offering;

        notify.ToLoadOwner(load,
            isNew ? $"پیشنهاد تازه برای بار {load.Code}" : $"پیشنهاد بار {load.Code} به‌روز شد",
            $"{actor.Name}: {Fa.Toman(finalAmount)}",
            load.ShipperId != null ? $"/Shipper/Offers/Compare/{load.LoadId}" : $"/Company/Loads/Detail/{load.LoadId}",
            "offer");

        await db.SaveChangesAsync(ct);
        return offer;
    }

    public async Task WithdrawOfferAsync(int offerId, Actor actor, CancellationToken ct = default)
    {
        var offer = await db.Offers.Include(o => o.Load).FirstOrDefaultAsync(o => o.OfferId == offerId &&
            (actor.Kind == Roles.Driver ? o.DriverId == actor.Id : o.CompanyId == actor.CompanyId), ct)
            ?? throw new UserError("پیشنهاد پیدا نشد.");
        if (offer.Status != OfferStatus.Pending) throw new UserError("فقط پیشنهادِ در انتظار را می‌توان پس گرفت.");
        offer.Status = OfferStatus.Withdrawn;
        offer.RespondedAt = DateTime.UtcNow;
        await ReopenIfNoOffersAsync(offer.Load!, offer.OfferId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RejectOfferAsync(int offerId, Actor actor, CancellationToken ct = default)
    {
        var offer = await db.Offers.Include(o => o.Load).FirstOrDefaultAsync(o => o.OfferId == offerId, ct)
                    ?? throw new UserError("پیشنهاد پیدا نشد.");
        if (!OwnsLoad(offer.Load!, actor)) throw new UserError("این بار متعلق به شما نیست.");
        if (offer.Status != OfferStatus.Pending) throw new UserError("این پیشنهاد دیگر در انتظار پاسخ نیست.");

        offer.Status = OfferStatus.Rejected;
        offer.RespondedAt = DateTime.UtcNow;
        NotifyOfferOwner(offer, $"پیشنهاد شما برای بار {offer.Load!.Code} رد شد", null);
        await ReopenIfNoOffersAsync(offer.Load!, offer.OfferId, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// لغو باری که هنوز قطعی نشده (پیش‌نویس، در انتظار راننده، در حال دریافت پیشنهاد).
    /// بارِ قطعی‌شده از این راه لغو نمی‌شود — باید سفرش لغو شود (<see cref="CancelAsync"/>).
    /// </summary>
    public async Task CancelLoadAsync(int loadId, Actor actor, string reason, CancellationToken ct = default)
    {
        var load = await db.Loads.FirstOrDefaultAsync(l => l.LoadId == loadId, ct) ?? throw new UserError("بار پیدا نشد.");
        if (!OwnsLoad(load, actor)) throw new UserError("این بار متعلق به شما نیست.");
        if (load.Status is not (LoadStatus.Draft or LoadStatus.Open or LoadStatus.Offering))
            throw new UserError(load.Status == LoadStatus.Booked
                ? "برای این بار سفر ساخته شده است؛ از صفحهٔ سفر آن را لغو کنید."
                : "این بار دیگر قابل لغو نیست.");
        if (string.IsNullOrWhiteSpace(reason)) throw new UserError("علت لغو را بنویسید.");

        var pending = await db.Offers.Where(o => o.LoadId == loadId && o.Status == OfferStatus.Pending).ToListAsync(ct);
        foreach (var o in pending)
        {
            o.Status = OfferStatus.Rejected;
            o.RespondedAt = DateTime.UtcNow;
            NotifyOfferOwner(o, $"بار {load.Code} توسط صاحب بار لغو شد", reason);
        }
        load.Status = LoadStatus.Cancelled;
        load.CancelReason = reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// شرکت بارِ مشتریِ خودش را با ناوگان خودش حمل می‌کند — بدون بازار و بدون پیشنهاد.
    /// کرایه بیرون از بارگو دریافت می‌شود، پس کمیسیونی ندارد و در تسویه کیف پولی جابه‌جا
    /// نمی‌شود؛ سفر فقط برای تخصیص، رهگیری، بارنامه و گزارش ساخته می‌شود.
    /// </summary>
    public async Task<Trip> BookOwnLoadAsync(int loadId, Actor actor, CancellationToken ct = default)
    {
        if (actor.Kind != Roles.Company || actor.CompanyId is not int cid) throw new UserError("فقط شرکت حمل‌ونقل.");
        var load = await db.Loads.FirstOrDefaultAsync(l => l.LoadId == loadId && l.CompanyId == cid, ct)
                   ?? throw new UserError("بار پیدا نشد.");
        if (!LoadStatus.Market.Contains(load.Status) && load.Status != LoadStatus.Draft)
            throw new UserError("این بار قبلاً به حمل‌کننده‌ای سپرده شده است.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var trip = new Trip
        {
            LoadId = load.LoadId, CarrierKind = CarrierKind.Company, CompanyId = cid,
            Fare = load.Price ?? 0, CommissionPercent = 0, Commission = 0, CarrierShare = load.Price ?? 0,
            IsPaid = true, Status = TripStatus.AwaitingAssignment,
            ScheduledDepartureAt = load.LoadingFrom, PlannedKm = load.DistanceKm
        };
        db.Trips.Add(trip);
        foreach (var o in await db.Offers.Where(o => o.LoadId == loadId && o.Status == OfferStatus.Pending).ToListAsync(ct))
        {
            o.Status = OfferStatus.Rejected;
            o.RespondedAt = DateTime.UtcNow;
            NotifyOfferOwner(o, $"بار {load.Code} به حمل‌کنندهٔ دیگری سپرده شد", null);
        }
        load.Status = LoadStatus.Booked;
        await db.SaveChangesAsync(ct);

        trip.Code = Codes.Make("T", trip.TripId, trip.CreatedAt);
        AddEvent(trip, null, trip.Status, actor, "حمل با ناوگان خود شرکت");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return trip;
    }

    private async Task ReopenIfNoOffersAsync(Load load, int exceptOfferId, CancellationToken ct)
    {
        if (load.Status != LoadStatus.Offering) return;
        var others = await db.Offers.AnyAsync(o => o.LoadId == load.LoadId && o.OfferId != exceptOfferId && o.Status == OfferStatus.Pending, ct);
        if (!others) load.Status = LoadStatus.Open;
    }

    private void NotifyOfferOwner(Offer offer, string title, string? body)
    {
        if (offer.DriverId is int d) notify.Add(OwnerKind.Driver, d, title, body, "/Driver/Offers", "offer");
        if (offer.CompanyId is int c) notify.Add(OwnerKind.Company, c, title, body, "/Company/Loads/Incoming", "offer");
    }

    /// <summary>
    /// پذیرش پیشنهاد — نقطه‌ای که «بار» به «سفر» تبدیل می‌شود. مبالغ (کرایه،
    /// درصد و مبلغ کمیسیون) همین‌جا روی سفر قفل می‌شوند.
    /// </summary>
    public async Task<Trip> AcceptOfferAsync(int offerId, Actor actor, CancellationToken ct = default)
    {
        var offer = await db.Offers.Include(o => o.Load).Include(o => o.Driver)
                        .FirstOrDefaultAsync(o => o.OfferId == offerId, ct)
                    ?? throw new UserError("پیشنهاد پیدا نشد.");
        var load = offer.Load!;
        if (!OwnsLoad(load, actor)) throw new UserError("این بار متعلق به شما نیست.");
        if (offer.Status != OfferStatus.Pending) throw new UserError("این پیشنهاد دیگر در انتظار پاسخ نیست.");
        if (!LoadStatus.Market.Contains(load.Status)) throw new UserError("برای این بار قبلاً حمل‌کننده انتخاب شده است.");
        if (offer.Driver is { } drv && drv.Status != AccountStatus.Approved)
            throw new UserError("حساب این راننده در حال حاضر فعال نیست.");

        // نرخ کمیسیون به نوع حمل‌کننده بسته است — مثل نرخ مصوب راهداری (راننده/شرکت جدا)
        var pct = await settings.GetDecimalAsync(offer.CarrierKind == CarrierKind.Company
            ? SettingsService.Keys.CommissionPercentCompany
            : SettingsService.Keys.CommissionPercentDriver, ct);
        var min = await settings.GetLongAsync(SettingsService.Keys.CommissionMinRial, ct);
        var commission = Math.Min(offer.Amount, Math.Max(min, (long)Math.Round(offer.Amount * pct / 100m)));

        // در پرداخت نقدی پولی از بارگو نمی‌گذرد؛ هزینه‌های جانبی و مالیات هم نقدی
        // رد و بدل می‌شوند و فقط کمیسیون از کیف پول حمل‌کننده کسر خواهد شد
        var cash = load.PayMethod == PayMethods.Cash;
        var loadingFee = cash ? 0 : await settings.GetLongAsync(SettingsService.Keys.LoadingFeeRial, ct);
        var unloadingFee = cash ? 0 : await settings.GetLongAsync(SettingsService.Keys.UnloadingFeeRial, ct);
        var waybillFee = cash ? 0 : await settings.GetLongAsync(SettingsService.Keys.WaybillFeeRial, ct);
        var vatPct = !cash && await settings.GetBoolAsync(SettingsService.Keys.VatEnabled, ct)
            ? await settings.GetDecimalAsync(SettingsService.Keys.VatPercent, ct) : 0m;

        // بار نقدی: حمل‌کننده باید کمیسیون را در کیف پول داشته باشد — وگرنه تسویه گیر می‌کند
        if (cash)
        {
            var (ck, cid2) = offer.CarrierKind == CarrierKind.Company
                ? (OwnerKind.Company, offer.CompanyId!.Value)
                : (OwnerKind.Driver, offer.DriverId!.Value);
            if (await wallet.BalanceAsync(ck, cid2, ct) < commission)
                throw new UserError($"برای بار نقدی، کیف پول حمل‌کننده باید دست‌کم به اندازهٔ کمیسیون ({Fa.Toman(commission)}) موجودی داشته باشد.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var isCompany = offer.CarrierKind == CarrierKind.Company;
        var trip = new Trip
        {
            LoadId = load.LoadId,
            OfferId = offer.OfferId,
            CarrierKind = offer.CarrierKind,
            CompanyId = offer.CompanyId,
            DriverId = offer.DriverId,
            VehicleId = offer.VehicleId,
            Fare = offer.Amount,
            PayMethod = load.PayMethod,
            CommissionPercent = pct,
            Commission = commission,
            CarrierShare = offer.Amount - commission,
            LoadingFee = loadingFee,
            UnloadingFee = unloadingFee,
            WaybillFee = waybillFee,
            VatPercent = vatPct,
            Vat = (long)Math.Round((offer.Amount + loadingFee + unloadingFee + waybillFee) * vatPct / 100m),
            // رانندهٔ مستقل با دادن پیشنهاد، مأموریت را از پیش پذیرفته است
            Status = isCompany ? TripStatus.AwaitingAssignment : TripStatus.Accepted,
            ScheduledDepartureAt = load.LoadingFrom,
            PlannedKm = load.DistanceKm
        };
        db.Trips.Add(trip);

        offer.Status = OfferStatus.Accepted;
        offer.RespondedAt = DateTime.UtcNow;
        load.Status = LoadStatus.Booked;

        var others = await db.Offers.Where(o => o.LoadId == load.LoadId && o.OfferId != offer.OfferId && o.Status == OfferStatus.Pending)
            .ToListAsync(ct);
        foreach (var o in others)
        {
            o.Status = OfferStatus.Rejected;
            o.RespondedAt = DateTime.UtcNow;
            NotifyOfferOwner(o, $"بار {load.Code} به حمل‌کنندهٔ دیگری سپرده شد", null);
        }

        await db.SaveChangesAsync(ct);

        trip.Code = Codes.Make("T", trip.TripId, trip.CreatedAt);
        AddEvent(trip, null, trip.Status, actor, "پیشنهاد پذیرفته شد و سفر ساخته شد");
        if (trip.DriverId is int did)
            db.TripAssignments.Add(new TripAssignment { TripId = trip.TripId, DriverId = did, VehicleId = trip.VehicleId, ByName = "پیشنهاد راننده" });

        notify.ToCarrier(trip, $"پیشنهاد شما برای بار {load.Code} پذیرفته شد",
            isCompany ? "راننده و خودرو را برای این سفر تخصیص دهید." : "سفر ساخته شد؛ در زمان بارگیری به سمت مبدا حرکت کنید.");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return trip;
    }

    // ------------------------------------------------------------------
    //  تخصیص (سفر شرکتی)
    // ------------------------------------------------------------------

    /// <summary>تخصیص یا تعویض راننده و خودرو توسط شرکت. هر تغییر در TripAssignments می‌ماند.</summary>
    public async Task AssignAsync(int tripId, int driverId, int? vehicleId, Actor actor, string? reason, CancellationToken ct = default)
    {
        if (actor.Kind != Roles.Company && actor.Kind != Roles.Admin) throw new UserError("تخصیص فقط از پنل شرکت ممکن است.");
        var trip = await db.Trips.Include(t => t.Load).Include(t => t.Company)
                       .FirstOrDefaultAsync(t => t.TripId == tripId && t.CarrierKind == CarrierKind.Company &&
                                                 (actor.Kind == Roles.Admin || t.CompanyId == actor.CompanyId), ct)
                   ?? throw new UserError("سفر پیدا نشد.");
        if (trip.Status is TripStatus.Delivered or TripStatus.Settled or TripStatus.Cancelled)
            throw new UserError("این سفر پایان یافته و تخصیص آن قابل تغییر نیست.");

        var driver = await db.Drivers.FirstOrDefaultAsync(d => d.DriverId == driverId && d.CompanyId == trip.CompanyId, ct)
                     ?? throw new UserError("این راننده عضو شرکت نیست.");
        if (vehicleId is int vid && !await db.Vehicles.AnyAsync(v => v.VehicleId == vid && v.CompanyId == trip.CompanyId, ct))
            throw new UserError("این خودرو متعلق به شرکت نیست.");

        var issues = await readiness.CheckAsync(driverId, vehicleId, ct);
        var block = issues.FirstOrDefault(i => i.Blocking);
        if (block is not null) throw new UserError($"{driver.FullName}: {block.Text}");

        var busy = await db.Trips.AnyAsync(t => t.TripId != tripId && t.DriverId == driverId &&
            (TripStatus.Live.Contains(t.Status) || t.Status == TripStatus.Accepted), ct);
        if (busy) throw new UserError($"{driver.FullName} در حال حاضر در سفر دیگری است.");

        if (trip.DriverId == driverId && trip.VehicleId == vehicleId) throw new UserError("تغییری در تخصیص داده نشد.");

        var prevDriver = trip.DriverId;
        db.TripAssignments.Add(new TripAssignment
        {
            TripId = trip.TripId,
            DriverId = driverId,
            VehicleId = vehicleId,
            PreviousDriverId = trip.DriverId,
            PreviousVehicleId = trip.VehicleId,
            ByCompanyUserId = actor.Kind == Roles.Company ? actor.Id : null,
            ByName = actor.Name,
            Reason = reason
        });

        trip.DriverId = driverId;
        trip.VehicleId = vehicleId;
        trip.DriverShare = (long)Math.Round(trip.CarrierShare * (trip.Company?.DriverSharePercent ?? 0) / 100m);

        if (trip.Status == TripStatus.AwaitingAssignment)
        {
            AddEvent(trip, trip.Status, TripStatus.Assigned, actor, $"تخصیص به {driver.FullName}");
            trip.Status = TripStatus.Assigned;
        }
        else
        {
            AddEvent(trip, trip.Status, trip.Status, actor,
                prevDriver != driverId ? $"تعویض راننده به {driver.FullName}" + (reason is null ? "" : $" — {reason}")
                                       : "تعویض خودرو" + (reason is null ? "" : $" — {reason}"));
        }

        if (prevDriver != driverId)
        {
            notify.Add(OwnerKind.Driver, driverId, $"مأموریت تازه: سفر {trip.Code}", null, $"/Driver/Trips/Detail/{trip.TripId}", "trip");
            if (prevDriver is int pd)
                notify.Add(OwnerKind.Driver, pd, $"سفر {trip.Code} به رانندهٔ دیگری سپرده شد", reason, "/Driver/Trips", "trip");
            if (prevDriver is not null)
                notify.ToLoadOwner(trip.Load!, $"رانندهٔ سفر {trip.Code} تغییر کرد", driver.FullName);
        }

        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    //  مراحل سفر
    // ------------------------------------------------------------------

    public async Task MoveAsync(Trip trip, string to, Actor actor, string? note = null, double? lat = null, double? lng = null,
        CancellationToken ct = default)
    {
        if (to == TripStatus.Cancelled)
        {
            await CancelAsync(trip, actor, note ?? "", ct);
            return;
        }
        if (!CanMove(trip.Status, to, actor.Kind))
            throw new UserError($"از مرحلهٔ «{TripStatus.Label(trip.Status)}» نمی‌توان «{ActionLabel(to)}» را ثبت کرد.");

        if (to == TripStatus.Delivered && actor.Kind != Roles.Admin &&
            await settings.GetBoolAsync(SettingsService.Keys.DeliveryOtp, ct))
            throw new UserError("تحویل فقط با کد یکبارمصرف گیرنده ثبت می‌شود.");

        if (to is TripStatus.Accepted or TripStatus.ToOrigin && trip.DriverId is null)
            throw new UserError("هنوز راننده‌ای برای این سفر تعیین نشده است.");

        if (to == TripStatus.InTransit && await settings.GetBoolAsync(SettingsService.Keys.RequireWaybill, ct) &&
            !await db.Waybills.AnyAsync(w => w.TripId == trip.TripId && w.Status != "void", ct))
            throw new UserError("پیش از شروع سفر، شمارهٔ بارنامه را ثبت کنید.");

        await ApplyAsync(trip, to, actor, note, lat, lng, ct);
    }

    private async Task ApplyAsync(Trip trip, string to, Actor actor, string? note, double? lat, double? lng, CancellationToken ct)
    {
        var from = trip.Status;
        var now = DateTime.UtcNow;
        trip.Status = to;
        if (lat is double la && lng is double lo) { trip.LastLat = la; trip.LastLng = lo; trip.LastPointAt = now; }

        switch (to)
        {
            case TripStatus.AwaitingAssignment:
                // راننده مأموریت را رد کرد
                if (trip.DriverId is int d)
                    db.TripAssignments.Add(new TripAssignment { TripId = trip.TripId, DriverId = d, PreviousDriverId = d, ByName = actor.Name, Reason = note ?? "رد مأموریت توسط راننده" });
                trip.DriverId = null;
                break;
            case TripStatus.ToOrigin:
                trip.StartedAt ??= now;
                break;
            case TripStatus.Loaded:
                trip.LoadedAt = now;
                break;
            case TripStatus.InTransit:
                if (trip.PlannedKm is double km) trip.EtaAt = Geo.EtaUtc(km);
                break;
            case TripStatus.Unloaded:
                // اگر کد تحویل هنگام ثبت بار صادر شده، گیرنده آن را دارد و ارسال دوباره لازم نیست
                var unloadedLoad = trip.Load ?? await db.Loads.FirstAsync(l => l.LoadId == trip.LoadId, ct);
                if (await settings.GetBoolAsync(SettingsService.Keys.DeliveryOtp, ct) &&
                    trip.DeliveryOtpHash is null && unloadedLoad.DeliveryCodeHash is null)
                    await IssueDeliveryOtpAsync(trip, ct);
                break;
            case TripStatus.Delivered:
                trip.DeliveredAt = now;
                trip.DeliveryOtpHash = null;
                var load = trip.Load ?? await db.Loads.FirstAsync(l => l.LoadId == trip.LoadId, ct);
                load.Status = LoadStatus.Completed;
                if (trip.DriverId is int dd)
                {
                    var drv = await db.Drivers.FirstAsync(x => x.DriverId == dd, ct);
                    drv.TripCount++;
                }
                break;
        }

        AddEvent(trip, from, to, actor, note, lat, lng);

        var l = trip.Load ?? await db.Loads.FirstAsync(x => x.LoadId == trip.LoadId, ct);
        notify.ToLoadOwner(l, $"سفر {trip.Code}: {TripStatus.Label(to)}", note,
            l.ShipperId != null ? $"/Shipper/Tracking?tripId={trip.TripId}" : null);
        if (actor.Kind != Roles.Company && trip.CompanyId is int c && to != TripStatus.AwaitingAssignment)
            notify.Add(OwnerKind.Company, c, $"سفر {trip.Code}: {TripStatus.Label(to)}", note, $"/Company/Trips/Detail/{trip.TripId}", "trip");
        if (to == TripStatus.AwaitingAssignment && trip.CompanyId is int c2)
            notify.Add(OwnerKind.Company, c2, $"راننده مأموریت سفر {trip.Code} را نپذیرفت", note, $"/Company/Dispatch/Assign/{trip.TripId}", "trip");

        await db.SaveChangesAsync(ct);

        // تحویل شد و کرایه از قبل پرداخت شده (یا نقدی است) → تسویهٔ خودکار
        if (to == TripStatus.Delivered && (trip.IsPaid || trip.PayMethod == PayMethods.Cash))
        {
            try { await SettleAsync(trip, Actor.System, ct); }
            catch (UserError e) when (trip.PayMethod == PayMethods.Cash)
            {
                // کیف پول حمل‌کننده برای کمیسیون کافی نبود — سفر تحویل‌شده می‌ماند و به صف مدیر می‌رود
                trip.IsProblem = true;
                trip.ProblemNote = $"تسویهٔ نقدی ناموفق: {e.Message}";
                notify.ToCarrier(trip, $"کمیسیون سفر {trip.Code} کسر نشد", "کیف پول را شارژ کنید تا سفر تسویه شود.", "finance");
                await db.SaveChangesAsync(ct);
            }
        }
    }

    // ------------------------------------------------------------------
    //  بارنامه و اسناد
    // ------------------------------------------------------------------

    public async Task RegisterWaybillAsync(Trip trip, string number, string? filePath, Actor actor, CancellationToken ct = default)
    {
        number = Fa.Latin(number);
        if (string.IsNullOrWhiteSpace(number)) throw new UserError("شمارهٔ بارنامه را وارد کنید.");
        if (trip.Status is TripStatus.Cancelled or TripStatus.Settled or TripStatus.AwaitingAssignment or TripStatus.Assigned)
            throw new UserError("در این مرحله نمی‌توان بارنامه ثبت کرد.");

        var w = await db.Waybills.FirstOrDefaultAsync(x => x.TripId == trip.TripId, ct);
        if (w is null)
        {
            w = new Waybill { TripId = trip.TripId };
            db.Waybills.Add(w);
        }
        w.Number = number;
        w.Status = "registered";
        w.IssuedByKind = actor.Kind;
        w.IssuedById = actor.Id;
        w.IssuedAt = DateTime.UtcNow;
        if (filePath is not null) w.FilePath = filePath;

        AddEvent(trip, trip.Status, trip.Status, actor, $"ثبت بارنامه {number}");
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    //  تحویل با کد
    // ------------------------------------------------------------------

    private static string HashOtp(Trip trip, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trip.TrackToken + ":" + code)));

    private static string HashLoadCode(Load load, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(load.Code + ":" + code)));

    /// <summary>
    /// کد تحویل هنگام ثبت بار: همان لحظه ساخته و به گیرنده (یا صاحب بار) پیامک می‌شود
    /// تا هنگام رسیدن بار آن را به راننده بدهد. باید پس از تعیین Load.Code صدا شود.
    /// اگر بعداً «ارسال دوبارهٔ کد» زده شود، کد تازهٔ سفر جایگزین این کد می‌شود.
    /// </summary>
    public async Task<string> IssueLoadDeliveryCodeAsync(Load load, CancellationToken ct = default)
    {
        var code = RandomNumberGenerator.GetInt32(10000, 100000).ToString();
        load.DeliveryCodeHash = HashLoadCode(load, code);

        var mobile = load.ReceiverMobile;
        if (string.IsNullOrEmpty(mobile) && load.ShipperId is int sid)
            mobile = await db.Shippers.AsNoTracking().Where(s => s.ShipperId == sid).Select(s => s.Mobile).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(mobile))
            await sms.SendAsync(mobile, $"بارگو — کد تحویل بار {load.Code}: {code}\nاین کد را فقط پس از تحویل کامل و سالم بار به راننده بدهید تا در سامانه ثبت کند.", "delivery", ct);
        return code;
    }

    /// <summary>
    /// کد پنج‌رقمی برای گیرنده. کد به گیرنده (یا صاحب بار) پیامک و در اعلانِ صاحب بار
    /// نمایش داده می‌شود؛ هرگز به راننده نشان داده نمی‌شود — کل معنای کد همین است.
    /// </summary>
    public async Task<string> IssueDeliveryOtpAsync(Trip trip, CancellationToken ct = default)
    {
        var load = trip.Load ?? await db.Loads.Include(l => l.Shipper).FirstAsync(l => l.LoadId == trip.LoadId, ct);
        var code = RandomNumberGenerator.GetInt32(10000, 100000).ToString();
        trip.DeliveryOtpHash = HashOtp(trip, code);
        trip.DeliveryOtpSentAt = DateTime.UtcNow;
        trip.DeliveryOtpAttempts = 0;

        var mobile = load.ReceiverMobile ?? load.Shipper?.Mobile;
        if (!string.IsNullOrEmpty(mobile))
            await sms.SendAsync(mobile, $"بارگو — کد تحویل سفر {trip.Code}: {code}\nاین کد را فقط پس از تحویل کامل بار به راننده بدهید.", "delivery", ct);
        notify.ToLoadOwner(load, $"کد تحویل سفر {trip.Code}: {Fa.Digits(code)}",
            "این کد را فقط پس از تحویل کامل و سالم بار به راننده بدهید.", kind: "trip");
        return code;
    }

    public async Task ConfirmDeliveryAsync(Trip trip, string code, Actor actor, double? lat, double? lng, CancellationToken ct = default)
    {
        if (!CanMove(trip.Status, TripStatus.Delivered, actor.Kind))
            throw new UserError("سفر هنوز به مرحلهٔ تحویل نرسیده است؛ ابتدا تخلیه را تأیید کنید.");

        // کد معتبر: کد سفر (اگر دوباره ارسال شده) وگرنه کدِ صادرشده هنگام ثبت بار
        var load = trip.Load ?? await db.Loads.FirstAsync(l => l.LoadId == trip.LoadId, ct);
        if (trip.DeliveryOtpHash is null && load.DeliveryCodeHash is null)
        {
            await IssueDeliveryOtpAsync(trip, ct);
            await db.SaveChangesAsync(ct);
            throw new UserError("کد تحویل تازه برای گیرنده ارسال شد؛ کد را از گیرنده بگیرید.");
        }
        if (trip.DeliveryOtpAttempts >= 5)
            throw new UserError("تعداد تلاش نادرست زیاد شد. از «ارسال دوبارهٔ کد» استفاده کنید.");

        var latin = Fa.Latin(code);
        var expected = Convert.FromHexString(trip.DeliveryOtpHash ?? load.DeliveryCodeHash!);
        var actual = Convert.FromHexString(trip.DeliveryOtpHash is not null ? HashOtp(trip, latin) : HashLoadCode(load, latin));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            trip.DeliveryOtpAttempts++;
            await db.SaveChangesAsync(ct);
            throw new UserError("کد تحویل نادرست است.");
        }

        await ApplyAsync(trip, TripStatus.Delivered, actor, "تحویل با کد گیرنده تأیید شد", lat, lng, ct);
    }

    // ------------------------------------------------------------------
    //  مالی
    // ------------------------------------------------------------------

    /// <summary>پرداخت کرایه از کیف پول صاحب بار. پول تا تحویل نزد بارگو امانت می‌ماند.</summary>
    public async Task PayFareFromWalletAsync(Trip trip, Actor actor, CancellationToken ct = default)
    {
        var load = trip.Load ?? await db.Loads.FirstAsync(l => l.LoadId == trip.LoadId, ct);
        if (!OwnsLoad(load, actor)) throw new UserError("این سفر متعلق به شما نیست.");
        if (trip.PayMethod == PayMethods.Cash) throw new UserError("کرایهٔ این سفر نقدی است و در مقصد به حمل‌کننده پرداخت می‌شود.");
        if (trip.IsPaid) throw new UserError("کرایهٔ این سفر قبلاً پرداخت شده است.");
        if (trip.Status == TripStatus.Cancelled) throw new UserError("سفر لغو شده است.");

        var (kind, id) = actor.Kind == Roles.Company ? (OwnerKind.Company, actor.CompanyId!.Value) : (OwnerKind.Shipper, actor.Id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await wallet.PostAsync(kind, id, -trip.TotalPayable, WalletTxnKind.FarePayment, trip.TripId, $"کرایه و هزینه‌های سفر {trip.Code}", ct);
        db.Payments.Add(new Payment
        {
            PayerKind = kind, PayerId = id, TripId = trip.TripId, Amount = trip.TotalPayable,
            Method = "wallet", Purpose = "fare", Status = PaymentStatus.Paid, PaidAt = DateTime.UtcNow
        });
        trip.IsPaid = true;
        AddEvent(trip, trip.Status, trip.Status, actor, $"پرداخت {Fa.Toman(trip.TotalPayable)} (کرایه و هزینه‌ها) از کیف پول");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (trip.Status == TripStatus.Delivered)
            await SettleAsync(trip, Actor.System, ct);
    }

    /// <summary>
    /// تسویه: سهم حمل‌کننده به کیف پولش، کمیسیون به کیف پول بارگو، و فاکتور برای هر دو
    /// طرف. سهم رانندهٔ شرکت اینجا جابه‌جا نمی‌شود — شرکت از «تسویه با رانندگان»
    /// پرداختش را ثبت می‌کند (<see cref="PayDriverShareAsync"/>)، چون بسیاری از شرکت‌ها
    /// حقوق ماهانه می‌دهند نه سهم سفر.
    /// </summary>
    public async Task SettleAsync(Trip trip, Actor actor, CancellationToken ct = default)
    {
        if (trip.Status != TripStatus.Delivered) throw new UserError("فقط سفرِ تحویل‌شده تسویه می‌شود.");
        var cash = trip.PayMethod == PayMethods.Cash;
        if (!trip.IsPaid && !cash) throw new UserError("کرایهٔ این سفر هنوز از صاحب بار دریافت نشده است.");

        var load = trip.Load ?? await db.Loads.FirstAsync(l => l.LoadId == trip.LoadId, ct);
        var (carrierKind, carrierId) = trip.CarrierKind == CarrierKind.Company
            ? (OwnerKind.Company, trip.CompanyId!.Value)
            : (OwnerKind.Driver, trip.DriverId!.Value);

        var ownTx = db.Database.CurrentTransaction is null;
        await using var tx = ownTx ? await db.Database.BeginTransactionAsync(ct) : null;

        // بارِ مشتریِ خودِ شرکت که با ناوگان خودش حمل شده: پولی نزد بارگو نیست
        var selfCarried = load.CompanyId is not null && load.CompanyId == trip.CompanyId;
        if (!selfCarried && cash)
        {
            // نقدی — الگوی کمیسیون باربری: کرایه دستِ حمل‌کننده است و فقط کمیسیون
            // از کیف پولش کسر می‌شود (اگر موجودی نباشد WalletService رد می‌کند)
            await wallet.PostAsync(carrierKind, carrierId, -trip.Commission, WalletTxnKind.Commission, trip.TripId, $"کمیسیون سفر نقدی {trip.Code}", ct);
            await wallet.PostAsync(OwnerKind.Platform, 0, trip.Commission, WalletTxnKind.Commission, trip.TripId, $"کمیسیون سفر {trip.Code}", ct);
            db.Invoices.Add(NewInvoice("C", trip, carrierKind, carrierId, "commission", trip.Commission, trip.VatPercent));
        }
        else if (!selfCarried)
        {
            await wallet.PostAsync(carrierKind, carrierId, trip.CarrierShare, WalletTxnKind.FareIncome, trip.TripId, $"سهم کرایهٔ سفر {trip.Code}", ct);
            await wallet.PostAsync(OwnerKind.Platform, 0, trip.Commission, WalletTxnKind.Commission, trip.TripId, $"کمیسیون سفر {trip.Code}", ct);

            // هزینه‌های جانبی: بارگیری و تخلیه به حمل‌کننده (خودش انجام می‌دهد)،
            // هزینهٔ بارنامه و ارزش افزوده به بارگو (صدور بارنامه و پرداخت مالیات با بارگوست)
            if (trip.LoadingFee + trip.UnloadingFee > 0)
                await wallet.PostAsync(carrierKind, carrierId, trip.LoadingFee + trip.UnloadingFee, WalletTxnKind.Fees, trip.TripId, $"هزینهٔ بارگیری و تخلیهٔ سفر {trip.Code}", ct);
            if (trip.WaybillFee + trip.Vat > 0)
                await wallet.PostAsync(OwnerKind.Platform, 0, trip.WaybillFee + trip.Vat, WalletTxnKind.Fees, trip.TripId, $"هزینهٔ بارنامه و مالیات سفر {trip.Code}", ct);

            var (payerKind, payerId) = load.ShipperId is int s ? (OwnerKind.Shipper, s) : (OwnerKind.Company, load.CompanyId ?? 0);
            db.Invoices.Add(NewInvoice("F", trip, payerKind, payerId, "freight",
                trip.Fare + trip.LoadingFee + trip.UnloadingFee + trip.WaybillFee, trip.VatPercent));
            db.Invoices.Add(NewInvoice("C", trip, carrierKind, carrierId, "commission", trip.Commission, trip.VatPercent));
        }

        var from = trip.Status;
        trip.Status = TripStatus.Settled;
        trip.SettledAt = DateTime.UtcNow;
        AddEvent(trip, from, TripStatus.Settled, actor,
            $"تسویه: سهم حمل‌کننده {Fa.Toman(trip.CarrierShare)}، کمیسیون {Fa.Toman(trip.Commission)}");
        notify.ToCarrier(trip, $"سفر {trip.Code} تسویه شد", $"{Fa.Toman(trip.CarrierShare)} به کیف پول واریز شد.", "finance");

        await db.SaveChangesAsync(ct);
        foreach (var inv in db.Invoices.Local.Where(i => i.TripId == trip.TripId && i.No == ""))
            inv.No = $"{(inv.Kind == "freight" ? "F" : "C")}-{Fa.Stamp6(inv.IssuedAt)}-{inv.InvoiceId:0000}";
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    private static Invoice NewInvoice(string _, Trip trip, string ownerKind, int ownerId, string kind, long amount, decimal vatPct)
    {
        var tax = (long)Math.Round(amount * vatPct / 100m);
        return new Invoice
        {
            TripId = trip.TripId, OwnerKind = ownerKind, OwnerId = ownerId, Kind = kind,
            Amount = amount, Tax = tax, Total = amount + tax, Status = "paid"
        };
    }

    /// <summary>شرکت سهم رانندهٔ یک سفر را از کیف پول خودش به کیف پول راننده منتقل می‌کند.</summary>
    public async Task PayDriverShareAsync(int tripId, Actor actor, CancellationToken ct = default)
    {
        var trip = await db.Trips.FirstOrDefaultAsync(t => t.TripId == tripId && t.CompanyId == actor.CompanyId, ct)
                   ?? throw new UserError("سفر پیدا نشد.");
        if (trip.Status != TripStatus.Settled) throw new UserError("سهم راننده پس از تسویهٔ سفر پرداخت می‌شود.");
        if (trip.DriverSharePaidAt is not null) throw new UserError("سهم راننده قبلاً پرداخت شده است.");
        if (trip.DriverId is not int d || trip.DriverShare is not long share || share <= 0)
            throw new UserError("برای این سفر سهم راننده تعیین نشده است.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await wallet.PostAsync(OwnerKind.Company, trip.CompanyId!.Value, -share, WalletTxnKind.DriverShare, trip.TripId, $"سهم راننده — سفر {trip.Code}", ct);
        await wallet.PostAsync(OwnerKind.Driver, d, share, WalletTxnKind.DriverShare, trip.TripId, $"سهم سفر {trip.Code}", ct);
        trip.DriverSharePaidAt = DateTime.UtcNow;
        notify.Add(OwnerKind.Driver, d, $"سهم سفر {trip.Code} واریز شد", Fa.Toman(share), "/Driver/Finance", "finance");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------
    //  لغو
    // ------------------------------------------------------------------

    public async Task CancelAsync(Trip trip, Actor actor, string reason, CancellationToken ct = default)
    {
        if (!CanCancel(trip.Status, actor.Kind))
            throw new UserError($"در مرحلهٔ «{TripStatus.Label(trip.Status)}» امکان لغو سفر برای شما نیست.");
        if (string.IsNullOrWhiteSpace(reason)) throw new UserError("علت لغو را بنویسید.");

        var load = trip.Load ?? await db.Loads.FirstAsync(l => l.LoadId == trip.LoadId, ct);
        var from = trip.Status;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        trip.Status = TripStatus.Cancelled;
        trip.CancelledAt = DateTime.UtcNow;
        trip.CancelReason = reason;

        // پیش از بارگیری: بار دوباره به بازار برمی‌گردد. پس از بارگیری، بار روی
        // خودروست و «بازنشر» معنا ندارد — سفر مشکل‌دار می‌شود تا مدیر تصمیم بگیرد.
        var beforeLoading = from is TripStatus.AwaitingAssignment or TripStatus.Assigned or TripStatus.Accepted
            or TripStatus.ToOrigin or TripStatus.AtOrigin;
        if (beforeLoading)
        {
            load.Status = actor.Kind == Roles.Shipper ? LoadStatus.Cancelled : LoadStatus.Open;
        }
        else
        {
            trip.IsProblem = true;
            trip.ProblemNote = $"لغو پس از بارگیری: {reason}";
        }

        if (trip.IsPaid)
        {
            var (k, id) = load.ShipperId is int s ? (OwnerKind.Shipper, s) : (OwnerKind.Company, load.CompanyId ?? 0);
            await wallet.PostAsync(k, id, trip.TotalPayable, WalletTxnKind.Refund, trip.TripId, $"استرداد کرایه و هزینه‌های سفر لغوشده {trip.Code}", ct);
            db.Refunds.Add(new Refund { TripId = trip.TripId, OwnerKind = k, OwnerId = id, Amount = trip.TotalPayable, Reason = reason, Status = "done", DoneAt = DateTime.UtcNow });
            trip.IsPaid = false;
        }

        AddEvent(trip, from, TripStatus.Cancelled, actor, reason);
        notify.ToLoadOwner(load, $"سفر {trip.Code} لغو شد", reason);
        notify.ToCarrier(trip, $"سفر {trip.Code} لغو شد", reason);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------
    //  رهگیری
    // ------------------------------------------------------------------

    /// <summary>
    /// ثبت یک نقطهٔ GPS برای راننده. جهشی با سرعت ضمنی بالای ۱۶۰ کیلومتر بر ساعت و
    /// جابه‌جایی زیر ۳۰ متر (لرزش GPS) در کیلومتر طی‌شده شمرده نمی‌شوند.
    /// </summary>
    public async Task RecordPointAsync(Driver driver, double lat, double lng, double? speedKmh, double? accuracyM, CancellationToken ct = default)
    {
        if (lat is < 24 or > 40 || lng is < 44 or > 64) throw new UserError("موقعیت خارج از محدودهٔ ایران است.");
        var now = DateTime.UtcNow;

        double step = 0;
        if (driver.LastLat is double pl && driver.LastLng is double pg && driver.LastSeenAt is DateTime ps)
        {
            var km = Geo.Km(pl, pg, lat, lng);
            var sec = (now - ps).TotalSeconds;
            var jump = sec >= 20 && km / (sec / 3600) > 160;
            if (km >= 0.03 && !jump) step = km;
        }

        driver.LastLat = lat;
        driver.LastLng = lng;
        driver.LastSeenAt = now;

        var trip = await db.Trips.Include(t => t.Load)
            .Where(t => t.DriverId == driver.DriverId && TripStatus.Live.Contains(t.Status))
            .OrderByDescending(t => t.TripId).FirstOrDefaultAsync(ct);

        var vehicleId = trip?.VehicleId ?? await db.Vehicles
            .Where(v => v.DriverId == driver.DriverId && v.Status == VehicleStatus.Active)
            .Select(v => (int?)v.VehicleId).FirstOrDefaultAsync(ct);

        db.TrackingPoints.Add(new TrackingPoint
        {
            TripId = trip?.TripId, DriverId = driver.DriverId, VehicleId = vehicleId,
            Lat = lat, Lng = lng, SpeedKmh = speedKmh, AccuracyM = accuracyM, StepKm = step, RecordedAt = now
        });

        if (vehicleId is int vid && await db.Vehicles.FirstOrDefaultAsync(v => v.VehicleId == vid, ct) is { } veh)
        {
            veh.LastLat = lat; veh.LastLng = lng; veh.LastSeenAt = now; veh.TrackedKm += step;
        }

        if (trip is not null)
        {
            trip.TravelledKm += step;
            trip.LastLat = lat; trip.LastLng = lng; trip.LastPointAt = now;
            if (trip.Load is { } l && trip.Status is TripStatus.InTransit)
            {
                var remaining = Geo.RoadKm(lat, lng, l.DestLat, l.DestLng);
                if (remaining is double r) trip.EtaAt = Geo.EtaUtc(r, speedKmh is > 20 ? speedKmh.Value : Geo.TruckAvgKmh);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    //  امتیاز
    // ------------------------------------------------------------------

    public async Task RateAsync(int tripId, Actor actor, int score, string? comment, CancellationToken ct = default)
    {
        if (score is < 1 or > 5) throw new UserError("امتیاز باید بین ۱ تا ۵ باشد.");
        var trip = await FindForAsync(tripId, actor, ct) ?? throw new UserError("سفر پیدا نشد.");
        if (trip.Status is not (TripStatus.Delivered or TripStatus.Settled))
            throw new UserError("امتیازدهی پس از تحویل بار ممکن است.");

        // صاحب بار به حمل‌کننده امتیاز می‌دهد؛ راننده به صاحب بار
        (string toKind, int toId) = actor.Kind switch
        {
            Roles.Shipper or Roles.Company when trip.Load!.ShipperId == actor.Id || trip.Load!.CompanyId == actor.CompanyId =>
                trip.CarrierKind == CarrierKind.Company && trip.DriverId is null
                    ? (OwnerKind.Company, trip.CompanyId!.Value)
                    : (OwnerKind.Driver, trip.DriverId!.Value),
            Roles.Driver when trip.Load!.ShipperId is int s => (OwnerKind.Shipper, s),
            _ => throw new UserError("امکان امتیازدهی برای این سفر نیست.")
        };
        var fromId = actor.Kind == Roles.Company ? actor.CompanyId!.Value : actor.Id;

        if (await db.Ratings.AnyAsync(r => r.TripId == tripId && r.FromKind == actor.Kind && r.FromId == fromId && r.ToKind == toKind, ct))
            throw new UserError("برای این سفر قبلاً امتیاز داده‌اید.");

        db.Ratings.Add(new Rating { TripId = tripId, FromKind = actor.Kind, FromId = fromId, ToKind = toKind, ToId = toId, Score = score, Comment = comment });

        switch (toKind)
        {
            case OwnerKind.Driver:
                var d = await db.Drivers.FirstAsync(x => x.DriverId == toId, ct);
                d.RatingAvg = (d.RatingAvg * d.RatingCount + score) / (d.RatingCount + 1);
                d.RatingCount++;
                notify.Add(OwnerKind.Driver, toId, $"امتیاز تازه برای سفر {trip.Code}: {Fa.Digits(score.ToString())} از ۵", comment, "/Driver/Ratings/Reviews", "info");
                break;
            case OwnerKind.Company:
                var c = await db.Companies.FirstAsync(x => x.CompanyId == toId, ct);
                c.RatingAvg = (c.RatingAvg * c.RatingCount + score) / (c.RatingCount + 1);
                c.RatingCount++;
                break;
            case OwnerKind.Shipper:
                var s = await db.Shippers.FirstAsync(x => x.ShipperId == toId, ct);
                s.RatingAvg = (s.RatingAvg * s.RatingCount + score) / (s.RatingCount + 1);
                s.RatingCount++;
                break;
        }
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------

    private void AddEvent(Trip trip, string? from, string to, Actor actor, string? note, double? lat = null, double? lng = null) =>
        db.TripEvents.Add(new TripEvent
        {
            TripId = trip.TripId,
            FromStatus = from,
            ToStatus = to,
            ActorKind = actor.Kind,
            ActorId = actor.Id,
            ActorName = actor.Name,
            Note = note,
            Lat = lat,
            Lng = lng
        });
}
