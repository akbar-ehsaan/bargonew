using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.CompanyPanel.Controllers;

/// <summary>
/// «مالی و حسابداری» شرکت. سه جریان پول از هم جدا نگه داشته می‌شوند:
///   ۱) سفرهای بازار که شرکت حمل می‌کند: کرایه نزد بارگو امانت است و پس از تحویل، سهم
///      شرکت (FareIncome) به کیف پول می‌آید؛ سهم راننده را شرکت خودش از کیف پول می‌پردازد.
///   ۲) بارهای خودِ شرکت که حمل‌کنندهٔ دیگری می‌برد: شرکت مثل صاحب بار کرایه می‌دهد (FarePayment).
///   ۳) بارهای مشتریان شرکت با ناوگان خودش: پولی از بارگو نمی‌گذرد، فقط گزارش می‌شود.
///
/// موجودی هرگز اینجا نوشته نمی‌شود — فقط WalletService؛ جابه‌جایی سهم راننده و پرداخت
/// کرایه فقط از TripFlow. درخواست برداشت موجودی را کم نمی‌کند: تا واریز مدیر، مبلغ
/// «رزرو» است (CompanyLedger.ReservedForPayoutAsync) و قابل برداشت دوباره نیست.
/// </summary>
[Area("Company")]
[Authorize(Roles = Roles.Company)]
[RequireCompanyPermission(CompanyPermission.Finance)]
public class FinanceController(BargoDbContext db, CurrentUser me, WalletService wallet, TripFlow flow, SettingsService settings, AuditService audit)
    : Controller
{
    private static readonly string[] InvoiceKinds = ["freight", "commission", "subscription"];

    // ------------------------------------------------------------------
    //  کیف پول
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "کیف پول شرکت";
        var cid = me.CompanyId;
        var monthStart = Fa.MonthStartUtc;
        var txns = db.WalletTransactions.AsNoTracking().Of(me.Owner);

        var bank = await db.Companies.AsNoTracking().Where(c => c.CompanyId == cid)
            .Select(c => new { c.Sheba, c.BankName }).FirstOrDefaultAsync(ct);

        var vm = new CompanyWalletVm
        {
            Balance = await wallet.BalanceAsync(OwnerKind.Company, cid, ct),
            Reserved = await CompanyLedger.ReservedForPayoutAsync(db, cid, ct),
            MinPayout = await settings.GetLongAsync(SettingsService.Keys.MinPayoutRial, ct),
            Sheba = bank?.Sheba,
            BankName = bank?.BankName,
            IncomeThisMonth = await txns.Where(t => t.Kind == WalletTxnKind.FareIncome && t.CreatedAt >= monthStart).SumAsync(t => (long?)t.Amount, ct) ?? 0,
            DriverSharesThisMonth = -(await txns.Where(t => t.Kind == WalletTxnKind.DriverShare && t.CreatedAt >= monthStart).SumAsync(t => (long?)t.Amount, ct) ?? 0),
            ExpensesThisMonth = await db.CompanyExpenses.AsNoTracking().Where(e => e.CompanyId == cid && e.SpentAt >= monthStart).SumAsync(e => (long?)e.Amount, ct) ?? 0,
            DriverDues = await UnpaidShares(cid).SumAsync(t => (long?)t.DriverShare, ct) ?? 0,
            Recent = await TxnRows(txns.OrderByDescending(t => t.WalletTransactionId).Take(10)).ToListAsync(ct),
            Payouts = await db.PayoutRequests.AsNoTracking().Of(me.Owner).OrderByDescending(p => p.PayoutRequestId).Take(5).ToListAsync(ct)
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Withdraw(string? amountToman, CancellationToken ct)
    {
        var cid = me.CompanyId;
        try
        {
            var amount = Fa.ParseToman(amountToman);
            if (amount is null or <= 0) throw new UserError("مبلغ برداشت را به تومان و فقط با رقم وارد کنید.");

            var min = await settings.GetLongAsync(SettingsService.Keys.MinPayoutRial, ct);
            if (amount < min) throw new UserError($"حداقل مبلغ درخواست برداشت {Fa.Toman(min)} است.");

            var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == cid, ct)
                          ?? throw new UserError("شرکت پیدا نشد.");
            if (string.IsNullOrWhiteSpace(company.Sheba))
                throw new UserError("برای شرکت شمارهٔ شبا ثبت نشده است. ابتدا از «حساب شرکت ← اطلاعات بانکی» شبا را ثبت کنید.");

            var balance = await wallet.BalanceAsync(OwnerKind.Company, cid, ct);
            var reserved = await CompanyLedger.ReservedForPayoutAsync(db, cid, ct);
            var available = balance - reserved;
            if (amount > available)
                throw new UserError(reserved > 0
                    ? $"موجودی قابل برداشت {Fa.Toman(Math.Max(0, available))} است ({Fa.Toman(reserved)} در درخواست‌های قبلی رزرو شده)."
                    : $"موجودی قابل برداشت {Fa.Toman(Math.Max(0, available))} است.");

            var pr = new PayoutRequest
            {
                OwnerKind = OwnerKind.Company, OwnerId = cid, OwnerName = company.Name,
                Amount = amount.Value, Sheba = company.Sheba, Status = PayoutStatus.Pending
            };
            db.PayoutRequests.Add(pr);
            await db.SaveChangesAsync(ct);
            audit.Add("PayoutRequest", pr.PayoutRequestId, "create", $"درخواست برداشت {Fa.Toman(pr.Amount)} — {company.Name}",
                new { pr.Amount, pr.Sheba, balance, reserved });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"درخواست برداشت {Fa.Toman(pr.Amount)} ثبت شد و پس از بررسی مدیر به شبای شرکت واریز می‌شود.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    //  درآمد
    // ------------------------------------------------------------------

    public async Task<IActionResult> Income(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "درآمد";
        var cid = me.CompanyId;
        var months = await CompanyLedger.MonthlyAsync(db, cid, PersianMonths.Last(12), ct);
        var txns = db.WalletTransactions.AsNoTracking().Of(me.Owner).Where(t => t.Kind == WalletTxnKind.FareIncome);

        return View(new CompanyIncomeVm
        {
            Months = months,
            ThisMonth = months.Count == 0 ? 0 : months[^1].Revenue,
            Page = await PageVm<TxnRow>.FromAsync(TxnRows(txns.OrderByDescending(t => t.WalletTransactionId)), page, PageLink.For(Request), ct: ct)
        });
    }

    // ------------------------------------------------------------------
    //  هزینه
    // ------------------------------------------------------------------

    public async Task<IActionResult> Expenses(string? kind, DateTime? from, DateTime? to, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "هزینه";
        var cid = me.CompanyId;
        kind = CompanyLabels.ExpenseKinds.Contains(kind) ? kind : null;
        var range = ReportRange.From(from, to, 30);

        var inRange = db.CompanyExpenses.AsNoTracking().Where(e => e.CompanyId == cid && e.SpentAt >= range.FromUtc && e.SpentAt < range.ToUtc);
        var byKind = (await inRange.GroupBy(e => e.Kind).Select(g => new { g.Key, N = g.Count(), Sum = g.Sum(e => e.Amount) }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => (x.N, x.Sum));

        var list = kind is null ? inRange : inRange.Where(e => e.Kind == kind);
        var rows = list.OrderByDescending(e => e.SpentAt).ThenByDescending(e => e.CompanyExpenseId).Select(e => new ExpenseRow
        {
            Id = e.CompanyExpenseId, Kind = e.Kind, Amount = e.Amount, Note = e.Note, SpentAt = e.SpentAt, TripId = e.TripId,
            TripCode = e.TripId != null ? db.Trips.Where(t => t.TripId == e.TripId).Select(t => t.Code).FirstOrDefault() : null,
            Plate = e.VehicleId != null ? db.Vehicles.Where(v => v.VehicleId == e.VehicleId).Select(v => v.PlateNo).FirstOrDefault() : null,
            DriverName = e.DriverId != null ? db.Drivers.Where(d => d.DriverId == e.DriverId).Select(d => d.FirstName + " " + d.LastName).FirstOrDefault() : null
        });

        return View(new CompanyExpensesVm
        {
            Page = await PageVm<ExpenseRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct),
            Range = range,
            Kind = kind,
            ByKind = CompanyLabels.ExpenseKinds.Select(k => new LabelCount
            {
                Key = k, Label = CompanyLabels.ExpenseKind(k),
                Count = byKind.TryGetValue(k, out var v) ? v.N : 0,
                Amount = byKind.TryGetValue(k, out var w) ? w.Sum : 0
            }).ToList(),
            Drivers = await db.Drivers.AsNoTracking().Where(d => d.CompanyId == cid).OrderBy(d => d.FirstName).ThenBy(d => d.LastName)
                .Select(d => new SelectItem { Id = d.DriverId, Text = d.FirstName + " " + d.LastName }).ToListAsync(ct),
            Vehicles = await db.Vehicles.AsNoTracking().Where(v => v.CompanyId == cid).OrderBy(v => v.PlateNo)
                .Select(v => new SelectItem { Id = v.VehicleId, Text = v.PlateNo + " — " + v.VehicleType!.Name }).ToListAsync(ct)
        });
    }

    [HttpPost]
    public async Task<IActionResult> AddExpense(string? kind, string? amountToman, string? tripCode, int? vehicleId, int? driverId,
        DateTime? spentAt, string? note, CancellationToken ct)
    {
        var cid = me.CompanyId;
        try
        {
            if (!ModelState.IsValid) throw new UserError("تاریخ معتبر نیست. قالب درست: ۱۴۰۵/۰۶/۲۱");
            kind = CompanyLabels.ExpenseKinds.Contains(kind) ? kind! : "other";
            var amount = Fa.ParseToman(amountToman);
            if (amount is null or <= 0) throw new UserError("مبلغ هزینه را به تومان و فقط با رقم وارد کنید.");
            if (spentAt is DateTime s && s > DateTime.UtcNow.AddDays(1)) throw new UserError("تاریخ هزینه نمی‌تواند در آینده باشد.");

            int? tripId = null;
            if (!string.IsNullOrWhiteSpace(tripCode))
            {
                var code = Fa.Latin(tripCode).ToUpperInvariant();
                tripId = await db.Trips.Where(t => t.Code == code && (t.CompanyId == cid || t.Load!.CompanyId == cid))
                    .Select(t => (int?)t.TripId).FirstOrDefaultAsync(ct);
                if (tripId is null) throw new UserError($"سفری با کد «{Fa.Digits(code)}» در سوابق شرکت پیدا نشد.");
            }
            if (vehicleId is int vid && !await db.Vehicles.AnyAsync(v => v.VehicleId == vid && v.CompanyId == cid, ct))
                throw new UserError("این خودرو متعلق به شرکت نیست.");
            if (driverId is int did && !await db.Drivers.AnyAsync(d => d.DriverId == did && d.CompanyId == cid, ct))
                throw new UserError("این راننده عضو شرکت نیست.");

            var e = new CompanyExpense
            {
                CompanyId = cid, Kind = kind, Amount = amount.Value, TripId = tripId, VehicleId = vehicleId, DriverId = driverId,
                SpentAt = spentAt ?? DateTime.UtcNow, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
            };
            db.CompanyExpenses.Add(e);
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"هزینهٔ «{CompanyLabels.ExpenseKind(kind)}» به مبلغ {Fa.Toman(e.Amount)} ثبت شد.";
        }
        catch (UserError ex)
        {
            TempData["err"] = ex.Message;
        }
        return RedirectToAction(nameof(Expenses));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var e = await db.CompanyExpenses.FirstOrDefaultAsync(x => x.CompanyExpenseId == id && x.CompanyId == cid, ct);
        if (e is null) return NotFound();

        audit.Add("CompanyExpense", e.CompanyExpenseId, "delete", $"حذف هزینهٔ {CompanyLabels.ExpenseKind(e.Kind)} {Fa.Toman(e.Amount)}",
            new { e.CompanyId, e.Kind, e.Amount, e.TripId, e.VehicleId, e.DriverId, e.SpentAt, e.Note });
        db.CompanyExpenses.Remove(e);
        await db.SaveChangesAsync(ct);
        TempData["ok"] = "هزینه حذف شد.";
        return RedirectToAction(nameof(Expenses));
    }

    // ------------------------------------------------------------------
    //  مطالبات و بدهی‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Receivables(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "مطالبات";
        var cid = me.CompanyId;

        // سفرهای بازار: کرایه هنوز از صاحب بار گرفته نشده. بارِ خودِ شرکت با ناوگان خودش
        // IsPaid=true ساخته می‌شود، پس خودبه‌خود اینجا نیست و در بخش دوم می‌آید.
        var unpaid = db.Trips.AsNoTracking().Where(t => t.CompanyId == cid && !t.IsPaid && t.Status != TripStatus.Cancelled);
        var offline = db.Trips.AsNoTracking()
            .Where(t => t.CompanyId == cid && t.Load!.CompanyId == cid && (t.Status == TripStatus.Delivered || t.Status == TripStatus.Settled));

        return View(new CompanyReceivablesVm
        {
            Unpaid = await PageVm<CompanyTripRow>.FromAsync(unpaid.OrderBy(t => t.CreatedAt).AsRows(partyIsCarrier: false), page, PageLink.For(Request), ct: ct),
            UnpaidTotal = await unpaid.SumAsync(t => (long?)t.Fare, ct) ?? 0,
            UnpaidShare = await unpaid.SumAsync(t => (long?)t.CarrierShare, ct) ?? 0,
            Offline = await offline.OrderByDescending(t => t.DeliveredAt).Take(50).AsRows(partyIsCarrier: false).ToListAsync(ct),
            OfflineTotal = await offline.SumAsync(t => (long?)t.Fare, ct) ?? 0
        });
    }

    public async Task<IActionResult> Payables(CancellationToken ct)
    {
        ViewData["Title"] = "بدهی‌ها";
        var cid = me.CompanyId;
        var shares = UnpaidShares(cid);
        var fares = db.Trips.AsNoTracking()
            .Where(t => t.Load!.CompanyId == cid && (t.CompanyId == null || t.CompanyId != cid) && !t.IsPaid && t.Status != TripStatus.Cancelled);

        return View(new CompanyPayablesVm
        {
            Balance = await wallet.BalanceAsync(OwnerKind.Company, cid, ct),
            DriverShares = await shares.OrderBy(t => t.SettledAt).AsRows(partyIsCarrier: false).ToListAsync(ct),
            DriverSharesTotal = await shares.SumAsync(t => (long?)t.DriverShare, ct) ?? 0,
            Fares = await fares.OrderBy(t => t.CreatedAt).AsRows(partyIsCarrier: true).ToListAsync(ct),
            FaresTotal = await fares.SumAsync(t => (long?)t.Fare, ct) ?? 0
        });
    }

    // ------------------------------------------------------------------
    //  تسویه با رانندگان
    // ------------------------------------------------------------------

    public async Task<IActionResult> DriverSettlements(int? driverId, CancellationToken ct)
    {
        ViewData["Title"] = "تسویه با رانندگان";
        var cid = me.CompanyId;
        var shares = UnpaidShares(cid);

        var agg = await shares.GroupBy(t => t.DriverId!.Value)
            .Select(g => new { DriverId = g.Key, Trips = g.Count(), Amount = g.Sum(t => t.DriverShare!.Value) })
            .ToListAsync(ct);
        var ids = agg.Select(a => a.DriverId).ToList();
        var drivers = await db.Drivers.AsNoTracking().Where(d => ids.Contains(d.DriverId))
            .Select(d => new { d.DriverId, Name = d.FirstName + " " + d.LastName, d.Mobile, InCompany = d.CompanyId == cid })
            .ToDictionaryAsync(d => d.DriverId, ct);
        var due = agg.Select(a =>
        {
            drivers.TryGetValue(a.DriverId, out var d);
            return new DriverDueRow
            {
                DriverId = a.DriverId, Name = d?.Name.Trim() ?? "—", Mobile = d?.Mobile ?? "", InCompany = d?.InCompany ?? false,
                Trips = a.Trips, Amount = a.Amount
            };
        }).OrderByDescending(r => r.Amount).ToList();

        var selected = driverId is int did ? due.FirstOrDefault(r => r.DriverId == did) : null;
        if (driverId is not null && selected is null)
        {
            // راننده‌ای که بدهی ندارد ولی عضو شرکت است — تاریخچه‌اش را می‌شود دید
            var d = await db.Drivers.AsNoTracking().Where(x => x.DriverId == driverId && x.CompanyId == cid)
                .Select(x => new DriverDueRow { DriverId = x.DriverId, Name = x.FirstName + " " + x.LastName, Mobile = x.Mobile, InCompany = true })
                .FirstOrDefaultAsync(ct);
            if (d is null) return NotFound();
            selected = d;
        }

        var monthStart = Fa.MonthStartUtc;
        var history = db.WalletTransactions.AsNoTracking().Of(me.Owner).Where(t => t.Kind == WalletTxnKind.DriverShare);
        var trips = new List<CompanyTripRow>();
        if (selected is not null)
        {
            var sid = selected.DriverId;
            history = history.Where(t => t.TripId != null && db.Trips.Any(x => x.TripId == t.TripId && x.DriverId == sid));
            trips = await shares.Where(t => t.DriverId == sid).OrderBy(t => t.SettledAt).AsRows(partyIsCarrier: false).ToListAsync(ct);
        }

        return View(new DriverSettlementsVm
        {
            Balance = await wallet.BalanceAsync(OwnerKind.Company, cid, ct),
            Due = due,
            Selected = selected,
            Trips = trips,
            History = await TxnRows(history.OrderByDescending(t => t.WalletTransactionId).Take(30)).ToListAsync(ct),
            PaidThisMonth = -(await db.WalletTransactions.AsNoTracking().Of(me.Owner)
                .Where(t => t.Kind == WalletTxnKind.DriverShare && t.CreatedAt >= monthStart).SumAsync(t => (long?)t.Amount, ct) ?? 0)
        });
    }

    [HttpPost]
    public async Task<IActionResult> PayDriverShare(int tripId, int? driverId, CancellationToken ct)
    {
        try
        {
            await flow.PayDriverShareAsync(tripId, me.ToActor(), ct);
            var code = await db.Trips.Where(t => t.TripId == tripId).Select(t => t.Code).FirstOrDefaultAsync(ct);
            TempData["ok"] = $"سهم رانندهٔ سفر {code} از کیف پول شرکت به کیف پول راننده منتقل شد.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
        }
        return RedirectToAction(nameof(DriverSettlements), new { driverId });
    }

    /// <summary>پرداخت همهٔ سهم‌های معوق یک راننده، سفر به سفر؛ با اولین خطا (معمولاً کمبود موجودی) می‌ایستد.</summary>
    [HttpPost]
    public async Task<IActionResult> PayDriverAll(int driverId, CancellationToken ct)
    {
        var cid = me.CompanyId;
        var tripIds = await UnpaidShares(cid).Where(t => t.DriverId == driverId).OrderBy(t => t.SettledAt).Select(t => t.TripId).ToListAsync(ct);
        if (tripIds.Count == 0)
        {
            TempData["err"] = "برای این راننده سهم پرداخت‌نشده‌ای نیست.";
            return RedirectToAction(nameof(DriverSettlements), new { driverId });
        }

        var paid = 0;
        string? stop = null;
        foreach (var id in tripIds)
        {
            try { await flow.PayDriverShareAsync(id, me.ToActor(), ct); paid++; }
            catch (UserError e) { stop = e.Message; break; }
        }

        if (stop is null) TempData["ok"] = $"سهم {Fa.N(paid)} سفر به کیف پول راننده منتقل شد.";
        else if (paid == 0) TempData["err"] = stop;
        else TempData["err"] = $"سهم {Fa.N(paid)} سفر پرداخت شد و ادامه متوقف ماند: {stop}";
        return RedirectToAction(nameof(DriverSettlements), new { driverId });
    }

    // ------------------------------------------------------------------
    //  تسویه با صاحبان بار (بارهای شرکت که دیگری حمل می‌کند)
    // ------------------------------------------------------------------

    public async Task<IActionResult> ShipperSettlements(string? tab, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تسویه با صاحبان بار";
        var cid = me.CompanyId;
        tab = tab is "paid" or "all" ? tab : "unpaid";
        var monthStart = Fa.MonthStartUtc;

        var all = db.Trips.AsNoTracking().Where(t => t.Load!.CompanyId == cid && (t.CompanyId == null || t.CompanyId != cid));
        var unpaid = all.Where(t => !t.IsPaid && t.Status != TripStatus.Cancelled);
        var list = tab switch
        {
            "paid" => all.Where(t => t.IsPaid).OrderByDescending(t => t.TripId),
            "all" => all.OrderByDescending(t => t.TripId),
            _ => unpaid.OrderBy(t => t.ScheduledDepartureAt ?? t.CreatedAt)
        };

        return View(new ShipperSettlementsVm
        {
            Balance = await wallet.BalanceAsync(OwnerKind.Company, cid, ct),
            Tab = tab,
            Rows = await PageVm<CompanyTripRow>.FromAsync(list.AsRows(partyIsCarrier: true), page, PageLink.For(Request), ct: ct),
            UnpaidCount = await unpaid.CountAsync(ct),
            UnpaidTotal = await unpaid.SumAsync(t => (long?)t.Fare, ct) ?? 0,
            PaidCount = await all.CountAsync(t => t.IsPaid, ct),
            PaidThisMonth = -(await db.WalletTransactions.AsNoTracking().Of(me.Owner)
                .Where(t => t.Kind == WalletTxnKind.FarePayment && t.CreatedAt >= monthStart).SumAsync(t => (long?)t.Amount, ct) ?? 0)
        });
    }

    [HttpPost]
    public async Task<IActionResult> PayFare(int tripId, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(tripId, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            await flow.PayFareFromWalletAsync(trip, me.ToActor(), ct);
            TempData["ok"] = $"کرایهٔ سفر {trip.Code} ({Fa.Toman(trip.Fare)}) از کیف پول شرکت پرداخت شد و تا تحویل بار نزد بارگو امانت می‌ماند.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message.Contains("موجودی")
                ? $"{e.Message} برای پرداخت {Fa.Toman(trip.Fare)} ابتدا کیف پول شرکت را شارژ کنید."
                : e.Message;
        }
        return RedirectToAction(nameof(ShipperSettlements));
    }

    // ------------------------------------------------------------------
    //  تراکنش‌ها و فاکتورها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Transactions(string? kind, DateTime? from, DateTime? to, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تراکنش‌ها";
        var cid = me.CompanyId;
        kind = CompanyLabels.CompanyTxnKinds.Contains(kind) ? kind : null;

        var q = db.WalletTransactions.AsNoTracking().Of(me.Owner);
        if (kind is not null) q = q.Where(t => t.Kind == kind);
        // بایندر ظهرِ روز را می‌دهد؛ مرز فیلتر باید ابتدای روزِ «از» و پایان روزِ «تا» به وقت تهران باشد
        if (from is DateTime f) { var start = Fa.ToUtc(Fa.ToTehran(f).Date); q = q.Where(t => t.CreatedAt >= start); }
        if (to is DateTime tt) { var end = Fa.ToUtc(Fa.ToTehran(tt).Date.AddDays(1)); q = q.Where(t => t.CreatedAt < end); }

        ViewBag.Kind = kind;
        ViewBag.FromBox = from is DateTime fb ? Fa.DateBox(fb) : "";
        ViewBag.ToBox = to is DateTime tb ? Fa.DateBox(tb) : "";
        ViewBag.Balance = await wallet.BalanceAsync(OwnerKind.Company, cid, ct);
        ViewBag.In = await q.Where(t => t.Amount > 0).SumAsync(t => (long?)t.Amount, ct) ?? 0;
        ViewBag.Out = -(await q.Where(t => t.Amount < 0).SumAsync(t => (long?)t.Amount, ct) ?? 0);
        ViewBag.KindCounts = (await db.WalletTransactions.AsNoTracking().Of(me.Owner)
                .GroupBy(t => t.Kind).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.N);

        return View(await PageVm<TxnRow>.FromAsync(TxnRows(q.OrderByDescending(t => t.WalletTransactionId)), page, PageLink.For(Request), ct: ct));
    }

    public async Task<IActionResult> Invoices(string? kind, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "فاکتورها";
        kind = InvoiceKinds.Contains(kind) ? kind : null;
        var all = db.Invoices.AsNoTracking().Of(me.Owner);
        var q = kind is null ? all : all.Where(i => i.Kind == kind);

        ViewBag.Kind = kind;
        ViewBag.Sum = await q.SumAsync(i => (long?)i.Total, ct) ?? 0;
        ViewBag.Tax = await q.SumAsync(i => (long?)i.Tax, ct) ?? 0;
        ViewBag.KindCounts = (await all.GroupBy(i => i.Kind).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.N);

        var rows = q.OrderByDescending(i => i.IssuedAt).ThenByDescending(i => i.InvoiceId).Select(i => new InvoiceRow
        {
            InvoiceId = i.InvoiceId, No = i.No, Kind = i.Kind, Amount = i.Amount, Tax = i.Tax, Total = i.Total, Status = i.Status,
            IssuedAt = i.IssuedAt, TripId = i.TripId, TripCode = i.Trip != null ? i.Trip.Code : null
        });
        return View(await PageVm<InvoiceRow>.FromAsync(rows, page, PageLink.For(Request), ct: ct));
    }

    // ------------------------------------------------------------------
    //  گزارش مالی
    // ------------------------------------------------------------------

    public async Task<IActionResult> Report(CancellationToken ct)
    {
        ViewData["Title"] = "گزارش مالی";
        var months = await CompanyLedger.MonthlyAsync(db, me.CompanyId, PersianMonths.Last(6), ct);
        ViewBag.Balance = await wallet.BalanceAsync(OwnerKind.Company, me.CompanyId, ct);
        return View(new LedgerReportVm { Months = months });
    }

    // ------------------------------------------------------------------

    /// <summary>سفرهای تسویه‌شدهٔ شرکت که سهم راننده‌شان هنوز از کیف پول شرکت پرداخت نشده.</summary>
    private IQueryable<Trip> UnpaidShares(int cid) => db.Trips.AsNoTracking()
        .Where(t => t.CompanyId == cid && t.Status == TripStatus.Settled && t.DriverId != null
                    && t.DriverShare > 0 && t.DriverSharePaidAt == null);

    private IQueryable<TxnRow> TxnRows(IQueryable<WalletTransaction> q) => q.Select(t => new TxnRow
    {
        Id = t.WalletTransactionId, Kind = t.Kind, Amount = t.Amount, BalanceAfter = t.BalanceAfter, TripId = t.TripId,
        TripCode = t.TripId != null ? db.Trips.Where(x => x.TripId == t.TripId).Select(x => x.Code).FirstOrDefault() : null,
        Note = t.Note, CreatedAt = t.CreatedAt
    });
}
