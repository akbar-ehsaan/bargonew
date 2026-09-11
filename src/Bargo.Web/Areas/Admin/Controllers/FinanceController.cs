using Bargo.Web.Areas.AdminPanel.Backoffice;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.AdminPanel.Controllers;

/// <summary>
/// مالیِ کل سامانه: گردش کیف پول‌ها، کمیسیون بارگو، تسویه با رانندگان و شرکت‌ها،
/// پرداخت‌های ناموفق، استرداد و گزارش درآمد.
///
/// قاعدهٔ این کنترلر: هیچ موجودی‌ای مستقیم نوشته نمی‌شود. هر جابه‌جایی پول از
/// <see cref="WalletService"/> و داخل یک تراکنش پایگاه‌داده می‌گذرد تا ردیف
/// WalletTransaction، تغییر وضعیتِ درخواست و ردّ حسابرسی یا با هم بنشینند یا هیچ‌کدام.
/// </summary>
[Area("Admin")]
[Authorize(Roles = Roles.Admin)]
[RequireAdminPermission(AdminPermission.Finance)]
public class FinanceController(BargoDbContext db, WalletService wallet, NotificationService notify, AuditService audit, CurrentUser me) : Controller
{
    private static readonly string[] TxnKinds =
    [
        WalletTxnKind.Charge, WalletTxnKind.FarePayment, WalletTxnKind.FareIncome, WalletTxnKind.DriverShare,
        WalletTxnKind.Commission, WalletTxnKind.Payout, WalletTxnKind.Refund, WalletTxnKind.Subscription, WalletTxnKind.Adjustment
    ];

    // ------------------------------------------------------------------
    //  کل تراکنش‌ها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index(string? owner, int? ownerId, string? kind, DateTime? from, DateTime? to, string? trip, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "کل تراکنش‌ها";
        var q = db.WalletTransactions.AsNoTracking();

        if (owner is not null && (BackofficeLookup.WalletOwners.Contains(owner) || owner == OwnerKind.Platform))
        {
            q = q.Where(t => t.OwnerKind == owner);
            // «گردش حساب» یک کاربر از صفحهٔ کیف پول‌ها
            if (ownerId is int oid && owner != OwnerKind.Platform) q = q.Where(t => t.OwnerId == oid);
            else ownerId = null;
        }
        else { owner = null; ownerId = null; }

        if (kind is not null && TxnKinds.Contains(kind)) q = q.Where(t => t.Kind == kind);
        else kind = null;

        if (from is DateTime f) { var s = BackofficeLookup.DayStartUtc(f); q = q.Where(t => t.CreatedAt >= s); }
        if (to is DateTime tt) { var e = BackofficeLookup.DayStartUtc(tt).AddDays(1); q = q.Where(t => t.CreatedAt < e); }

        if (!string.IsNullOrWhiteSpace(trip))
        {
            // شناسهٔ عددی یا کد خوانا (T-050621-0042) — مدیر معمولاً کد را از پرونده کپی می‌کند
            var raw = Fa.Latin(trip).ToUpperInvariant();
            int? tripId = int.TryParse(raw, out var n) ? n
                : await db.Trips.Where(x => x.Code == raw).Select(x => (int?)x.TripId).FirstOrDefaultAsync(ct);
            q = q.Where(t => t.TripId == (tripId ?? -1));
        }

        // جمعِ کل مجموعهٔ فیلترشده در SQL — نه جمعِ همین صفحهٔ سی‌ردیفی
        var sumIn = await q.Where(t => t.Amount > 0).SumAsync(t => t.Amount, ct);
        var sumOut = await q.Where(t => t.Amount < 0).SumAsync(t => -t.Amount, ct);

        var pageVm = await PageVm<WalletTransaction>.FromAsync(q.OrderByDescending(t => t.WalletTransactionId), page, PageLink.For(Request), ct: ct);
        var vm = new FinanceTxnListVm
        {
            Page = pageVm,
            Names = await BackofficeLookup.NamesAsync(db, pageVm.Rows.Select(r => (r.OwnerKind, r.OwnerId)), ct),
            TripCodes = await BackofficeLookup.TripCodesAsync(db, pageVm.Rows.Select(r => r.TripId), ct),
            SumIn = sumIn, SumOut = sumOut,
            Owner = owner, Kind = kind, Trip = trip, From = from, To = to
        };
        ViewBag.Kinds = TxnKinds;
        ViewBag.OwnerId = ownerId;
        if (ownerId is int one)
            ViewBag.OwnerName = (await BackofficeLookup.NamesAsync(db, [(owner!, one)], ct)).Name(owner, one);
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  کمیسیون بارگو
    // ------------------------------------------------------------------

    public async Task<IActionResult> Commission(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "کمیسیون بارگو";
        var q = db.WalletTransactions.AsNoTracking().Where(t => t.Kind == WalletTxnKind.Commission);
        var month = Fa.MonthStartUtc;
        var today = Fa.TodayStartUtc;

        var rows = q.OrderByDescending(t => t.WalletTransactionId).Select(t => new CommissionRowVm
        {
            Id = t.WalletTransactionId,
            Amount = t.Amount,
            TripId = t.TripId,
            TripCode = db.Trips.Where(x => x.TripId == t.TripId).Select(x => x.Code).FirstOrDefault(),
            Fare = db.Trips.Where(x => x.TripId == t.TripId).Select(x => (long?)x.Fare).FirstOrDefault(),
            Percent = db.Trips.Where(x => x.TripId == t.TripId).Select(x => (decimal?)x.CommissionPercent).FirstOrDefault(),
            Note = t.Note,
            CreatedAt = t.CreatedAt
        });

        var vm = new CommissionVm
        {
            Page = await PageVm<CommissionRowVm>.FromAsync(rows, page, PageLink.For(Request), ct: ct),
            TodaySum = await q.Where(t => t.CreatedAt >= today).SumAsync(t => t.Amount, ct),
            MonthSum = await q.Where(t => t.CreatedAt >= month).SumAsync(t => t.Amount, ct),
            MonthCount = await q.CountAsync(t => t.CreatedAt >= month, ct),
            AllSum = await q.SumAsync(t => t.Amount, ct),
            PlatformBalance = await wallet.PlatformBalanceAsync(ct)
        };
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  کیف پول کاربران
    // ------------------------------------------------------------------

    public async Task<IActionResult> Wallets(string tab = "drivers", string? q = null, int? adjust = null, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "کیف پول کاربران";
        tab = tab is "shippers" or "companies" ? tab : "drivers";
        var term = (q ?? "").Trim();
        var digits = Fa.Latin(term);

        IQueryable<WalletRowVm> rows = tab switch
        {
            "shippers" => db.Shippers.AsNoTracking()
                .Where(s => term == "" || s.FullName.Contains(term) || (s.BusinessName != null && s.BusinessName.Contains(term)) || s.Mobile.Contains(digits))
                .Select(s => new WalletRowVm
                {
                    Kind = OwnerKind.Shipper, Id = s.ShipperId,
                    Name = s.Kind == "business" && s.BusinessName != null && s.BusinessName != "" ? s.BusinessName : s.FullName,
                    Mobile = s.Mobile, Balance = s.WalletBalance, Status = s.Status
                }),
            "companies" => db.Companies.AsNoTracking()
                .Where(c => term == "" || c.Name.Contains(term) || c.Mobile.Contains(digits))
                .Select(c => new WalletRowVm
                {
                    Kind = OwnerKind.Company, Id = c.CompanyId, Name = c.Name,
                    Mobile = c.Mobile, Balance = c.WalletBalance, Status = c.Status
                }),
            _ => db.Drivers.AsNoTracking()
                .Where(d => term == "" || d.FirstName.Contains(term) || d.LastName.Contains(term) || d.Mobile.Contains(digits) || d.NationalCode.Contains(digits))
                .Select(d => new WalletRowVm
                {
                    Kind = OwnerKind.Driver, Id = d.DriverId, Name = d.FirstName + " " + d.LastName,
                    Mobile = d.Mobile, Balance = d.WalletBalance, Status = d.Status
                })
        };

        WalletRowVm? adj = null;
        if (adjust is int aid)
        {
            var kind = tab switch { "shippers" => OwnerKind.Shipper, "companies" => OwnerKind.Company, _ => OwnerKind.Driver };
            var names = await BackofficeLookup.NamesAsync(db, [(kind, aid)], ct);
            if (names.Has(kind, aid))
                adj = new WalletRowVm
                {
                    Kind = kind, Id = aid, Name = names.Name(kind, aid), Mobile = names.Mobile(kind, aid),
                    Balance = await wallet.BalanceAsync(kind, aid, ct)
                };
        }

        var vm = new WalletsVm
        {
            Page = await PageVm<WalletRowVm>.FromAsync(rows.OrderByDescending(r => r.Balance).ThenBy(r => r.Id), page, PageLink.For(Request), ct: ct),
            Tab = tab, Q = term,
            DriversTotal = await db.Drivers.SumAsync(d => d.WalletBalance, ct),
            ShippersTotal = await db.Shippers.SumAsync(s => s.WalletBalance, ct),
            CompaniesTotal = await db.Companies.SumAsync(c => c.WalletBalance, ct),
            PlatformBalance = await wallet.PlatformBalanceAsync(ct),
            Adjust = adj
        };
        return View(vm);
    }

    /// <summary>
    /// اصلاح دستی موجودی. یادداشت اجباری است چون این تنها ردیفِ کیف پول است که پشتش
    /// سفر، پرداخت یا درخواستی نیست — بدون شرح، سالِ بعد هیچ‌کس نمی‌داند چرا ثبت شده.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Adjust(string ownerKind, int ownerId, string? amountToman, string direction, string? note, CancellationToken ct)
    {
        var tab = ownerKind switch { OwnerKind.Shipper => "shippers", OwnerKind.Company => "companies", _ => "drivers" };
        var back = RedirectToAction(nameof(Wallets), new { tab, adjust = ownerId });

        if (!BackofficeLookup.WalletOwners.Contains(ownerKind) || !await BackofficeLookup.OwnerExistsAsync(db, ownerKind, ownerId, ct))
            return NotFound();

        var amount = Fa.ParseToman(amountToman) ?? 0;
        note = (note ?? "").Trim();
        if (amount <= 0) { TempData["err"] = "مبلغ اصلاح را به تومان وارد کنید."; return back; }
        if (direction is not ("+" or "-")) { TempData["err"] = "افزایش یا کاهش موجودی را انتخاب کنید."; return back; }
        if (note.Length < 5) { TempData["err"] = "شرح اصلاح (دست‌کم ۵ نویسه) اجباری است."; return back; }

        var signed = direction == "-" ? -amount : amount;
        var names = await BackofficeLookup.NamesAsync(db, [(ownerKind, ownerId)], ct);
        var name = names.Name(ownerKind, ownerId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await wallet.PostAsync(ownerKind, ownerId, signed, WalletTxnKind.Adjustment, note: $"اصلاح دستی مدیر: {note}", ct: ct);
        audit.Add("Wallet", ownerId, "adjust",
            $"اصلاح موجودی {OwnerKind.Label(ownerKind)} «{name}»: {(signed > 0 ? "+" : "−")}{Fa.Toman(amount)}",
            new { ownerKind, ownerId, amount = signed, note, balanceAfter = row.BalanceAfter });
        notify.Add(ownerKind, ownerId,
            signed > 0 ? "افزایش موجودی کیف پول" : "کاهش موجودی کیف پول",
            $"{Fa.Toman(amount)} — {note}", $"/{BackofficeLookup.PanelOf(ownerKind)}/Finance", "finance");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        TempData["ok"] = $"موجودی «{name}» اصلاح شد. موجودی جدید: {Fa.Toman(row.BalanceAfter)}"
                         + (row.BalanceAfter < 0 ? " (منفی — تا جبران، برداشت برای این حساب ممکن نیست)" : "");
        return RedirectToAction(nameof(Wallets), new { tab });
    }

    // ------------------------------------------------------------------
    //  تسویه (درخواست‌های برداشت)
    // ------------------------------------------------------------------

    public async Task<IActionResult> Payouts(string? owner, string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = owner switch
        {
            OwnerKind.Driver => "تسویه رانندگان",
            OwnerKind.Company => "تسویه شرکت‌ها",
            OwnerKind.Shipper => "تسویه صاحبان بار",
            _ => "درخواست‌های تسویه"
        };
        var all = db.PayoutRequests.AsNoTracking();
        if (owner is not null && BackofficeLookup.WalletOwners.Contains(owner)) all = all.Where(p => p.OwnerKind == owner);
        else owner = null;

        status = status is PayoutStatus.Pending or PayoutStatus.Approved or PayoutStatus.Paid or PayoutStatus.Rejected or "all" ? status : "open";
        var counts = await all.GroupBy(p => p.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        counts["open"] = counts.GetValueOrDefault(PayoutStatus.Pending) + counts.GetValueOrDefault(PayoutStatus.Approved);
        counts["all"] = counts.Where(kv => kv.Key is not ("open" or "all")).Sum(kv => kv.Value);

        var q = status switch
        {
            "open" => all.Where(p => p.Status == PayoutStatus.Pending || p.Status == PayoutStatus.Approved),
            "all" => all,
            _ => all.Where(p => p.Status == status)
        };
        // صفِ باز از قدیمی‌ترین؛ سوابق از جدیدترین
        q = status is "open" or PayoutStatus.Pending or PayoutStatus.Approved
            ? q.OrderBy(p => p.PayoutRequestId)
            : q.OrderByDescending(p => p.PayoutRequestId);

        var pageVm = await PageVm<PayoutRequest>.FromAsync(q, page, PageLink.For(Request), ct: ct);

        // موجودیِ فعلی — ممکن است از زمان ثبت درخواست کم شده باشد
        var balances = new Dictionary<(string, int), long>();
        foreach (var o in pageVm.Rows.Select(r => (r.OwnerKind, r.OwnerId)).Distinct())
            balances[o] = await wallet.BalanceAsync(o.OwnerKind, o.OwnerId, ct);

        var vm = new PayoutsVm
        {
            Page = pageVm, Owner = owner, Status = status, Counts = counts, Balances = balances,
            Reviewers = await BackofficeLookup.AdminNamesAsync(db, pageVm.Rows.Select(r => r.ReviewedByAdminId), ct),
            OpenSum = await all.Where(p => p.Status == PayoutStatus.Pending || p.Status == PayoutStatus.Approved).SumAsync(p => p.Amount, ct)
        };
        return View(vm);
    }

    /// <summary>تأیید — درخواست به صف واریز بانکی می‌رود؛ هنوز پولی جابه‌جا نشده است.</summary>
    [HttpPost]
    public async Task<IActionResult> Approve(int id, string? owner, CancellationToken ct)
    {
        var pr = await db.PayoutRequests.AsNoTracking().FirstOrDefaultAsync(p => p.PayoutRequestId == id, ct);
        if (pr is null) return NotFound();
        var back = RedirectToAction(nameof(Payouts), new { owner });

        var balance = await wallet.BalanceAsync(pr.OwnerKind, pr.OwnerId, ct);
        if (balance < pr.Amount)
        {
            TempData["err"] = $"موجودی فعلی «{pr.OwnerName}» ({Fa.Toman(balance)}) از مبلغ درخواست کمتر است؛ درخواست را رد کنید.";
            return back;
        }

        var n = await db.PayoutRequests.Where(p => p.PayoutRequestId == id && p.Status == PayoutStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PayoutStatus.Approved).SetProperty(p => p.ReviewedByAdminId, me.Id), ct);
        if (n == 0) { TempData["err"] = "این درخواست پیش‌تر بررسی شده است."; return back; }

        audit.Add("PayoutRequest", id, "approve", $"تأیید درخواست برداشت {Fa.Toman(pr.Amount)} — {pr.OwnerName}", new { pr.OwnerKind, pr.OwnerId, pr.Amount, pr.Sheba });
        notify.Add(pr.OwnerKind, pr.OwnerId, "درخواست برداشت تأیید شد", $"{Fa.Toman(pr.Amount)} در صف واریز بانکی قرار گرفت.",
            $"/{BackofficeLookup.PanelOf(pr.OwnerKind)}/Finance", "finance");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = $"درخواست {Fa.Toman(pr.Amount)} «{pr.OwnerName}» تأیید شد. پس از واریز، شمارهٔ پیگیری بانک را ثبت کنید.";
        return back;
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int id, string? note, string? owner, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Payouts), new { owner });
        note = (note ?? "").Trim();
        if (note.Length < 3) { TempData["err"] = "علت رد درخواست را بنویسید؛ برای درخواست‌کننده ارسال می‌شود."; return back; }

        var pr = await db.PayoutRequests.AsNoTracking().FirstOrDefaultAsync(p => p.PayoutRequestId == id, ct);
        if (pr is null) return NotFound();

        var n = await db.PayoutRequests
            .Where(p => p.PayoutRequestId == id && (p.Status == PayoutStatus.Pending || p.Status == PayoutStatus.Approved))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PayoutStatus.Rejected)
                .SetProperty(p => p.ReviewNote, note).SetProperty(p => p.ReviewedByAdminId, me.Id), ct);
        if (n == 0) { TempData["err"] = "این درخواست دیگر قابل رد نیست."; return back; }

        audit.Add("PayoutRequest", id, "reject", $"رد درخواست برداشت {Fa.Toman(pr.Amount)} — {pr.OwnerName}", new { pr.OwnerKind, pr.OwnerId, pr.Amount, note });
        notify.Add(pr.OwnerKind, pr.OwnerId, "درخواست برداشت رد شد", note, $"/{BackofficeLookup.PanelOf(pr.OwnerKind)}/Finance", "finance");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "درخواست رد شد؛ موجودی کیف پول دست‌نخورده ماند.";
        return back;
    }

    /// <summary>
    /// ثبت واریز بانکی. کسر از کیف پول همین‌جا انجام می‌شود، نه هنگام ثبت درخواست:
    /// تا پول واقعاً از حساب بارگو خارج نشده، موجودی کاربر نباید کم شود.
    /// تغییر وضعیت با شرط «هنوز واریز نشده» در همان تراکنش است تا دو کلیکِ هم‌زمانِ
    /// دو مدیر، دو بار کسر نکند.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> MarkPaid(int id, string? bankTrackingNo, string? owner, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Payouts), new { owner });
        var tracking = Fa.Latin(bankTrackingNo).Trim();
        if (tracking.Length is < 4 or > 40) { TempData["err"] = "شمارهٔ پیگیری بانک (۴ تا ۴۰ نویسه) را وارد کنید."; return back; }

        var pr = await db.PayoutRequests.AsNoTracking().FirstOrDefaultAsync(p => p.PayoutRequestId == id, ct);
        if (pr is null) return NotFound();

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var n = await db.PayoutRequests
                .Where(p => p.PayoutRequestId == id && (p.Status == PayoutStatus.Pending || p.Status == PayoutStatus.Approved))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PayoutStatus.Paid)
                    .SetProperty(p => p.PaidAt, DateTime.UtcNow)
                    .SetProperty(p => p.BankTrackingNo, tracking)
                    .SetProperty(p => p.ReviewedByAdminId, me.Id), ct);
            if (n == 0) { TempData["err"] = "این درخواست پیش‌تر واریز یا رد شده است."; return back; }

            var row = await wallet.PostAsync(pr.OwnerKind, pr.OwnerId, -pr.Amount, WalletTxnKind.Payout,
                note: $"واریز به شبا {pr.Sheba} — پیگیری {tracking}", ct: ct);
            row.PayoutRequestId = pr.PayoutRequestId;

            audit.Add("PayoutRequest", id, "paid", $"واریز {Fa.Toman(pr.Amount)} به {pr.OwnerName}",
                new { pr.OwnerKind, pr.OwnerId, pr.Amount, pr.Sheba, tracking, balanceAfter = row.BalanceAfter });
            notify.Add(pr.OwnerKind, pr.OwnerId, "مبلغ برداشت واریز شد",
                $"{Fa.Toman(pr.Amount)} به حساب {pr.Sheba} واریز شد. شمارهٔ پیگیری: {tracking}",
                $"/{BackofficeLookup.PanelOf(pr.OwnerKind)}/Finance", "finance");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (UserError e)
        {
            // موجودی کافی نبود — تراکنش بدون Commit برگشت می‌خورد و وضعیت «واریزشده» هم نمی‌ماند
            TempData["err"] = $"{e.Message} واریز «{pr.OwnerName}» ثبت نشد.";
            return back;
        }

        TempData["ok"] = $"واریز {Fa.Toman(pr.Amount)} به «{pr.OwnerName}» ثبت و از کیف پول کسر شد.";
        return back;
    }

    // ------------------------------------------------------------------
    //  پرداخت‌های ناموفق
    // ------------------------------------------------------------------

    public async Task<IActionResult> Failed(string? purpose, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "پرداخت‌های ناموفق";
        var all = db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Failed);
        var q = purpose is "charge" or "fare" or "subscription" ? all.Where(p => p.Purpose == purpose) : all;
        if (purpose is not ("charge" or "fare" or "subscription")) purpose = null;

        var month = Fa.MonthStartUtc;
        var pageVm = await PageVm<Payment>.FromAsync(q.OrderByDescending(p => p.PaymentId), page, PageLink.For(Request), ct: ct);
        var vm = new FailedPaymentsVm
        {
            Page = pageVm,
            Names = await BackofficeLookup.NamesAsync(db, pageVm.Rows.Select(r => (r.PayerKind, r.PayerId)), ct),
            TripCodes = await BackofficeLookup.TripCodesAsync(db, pageVm.Rows.Select(r => r.TripId), ct),
            Purpose = purpose,
            MonthCount = await all.CountAsync(p => p.CreatedAt >= month, ct),
            MonthSum = await all.Where(p => p.CreatedAt >= month).SumAsync(p => p.Amount, ct),
            AllCount = await all.CountAsync(ct)
        };
        return View(vm);
    }

    // ------------------------------------------------------------------
    //  استردادها
    // ------------------------------------------------------------------

    public async Task<IActionResult> Refunds(string? status, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "استردادها";
        status = status is "done" or "rejected" or "all" ? status : "pending";
        var all = db.Refunds.AsNoTracking();
        var counts = await all.GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        counts["all"] = counts.Values.Sum();

        var q = status == "all" ? all : all.Where(r => r.Status == status);
        q = status == "pending" ? q.OrderBy(r => r.RefundId) : q.OrderByDescending(r => r.RefundId);
        var pageVm = await PageVm<Refund>.FromAsync(q, page, PageLink.For(Request), ct: ct);

        var vm = new RefundsVm
        {
            Page = pageVm,
            Names = await BackofficeLookup.NamesAsync(db, pageVm.Rows.Select(r => (r.OwnerKind, r.OwnerId)), ct),
            TripCodes = await BackofficeLookup.TripCodesAsync(db, pageVm.Rows.Select(r => r.TripId), ct),
            Reviewers = await BackofficeLookup.AdminNamesAsync(db, pageVm.Rows.Select(r => r.ReviewedByAdminId), ct),
            Status = status, Counts = counts,
            PendingSum = await all.Where(r => r.Status == "pending").SumAsync(r => r.Amount, ct)
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> CompleteRefund(int id, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Refunds));
        var r = await db.Refunds.AsNoTracking().FirstOrDefaultAsync(x => x.RefundId == id, ct);
        if (r is null) return NotFound();
        if (r.Status != "pending") { TempData["err"] = "این درخواست استرداد پیش‌تر بررسی شده است."; return back; }
        if (!BackofficeLookup.WalletOwners.Contains(r.OwnerKind)) { TempData["err"] = "صاحب این استرداد کیف پول ندارد."; return back; }

        // لغو سفرِ پرداخت‌شده، کرایه را خودکار برمی‌گرداند (TripFlow.CancelAsync)؛ درخواستِ
        // دستیِ هم‌زمان برای همان سفر یعنی استرداد دوباره.
        if (r.TripId is int tid && await db.Refunds.AnyAsync(x => x.TripId == tid && x.Status == "done" && x.OwnerKind == r.OwnerKind && x.OwnerId == r.OwnerId, ct))
        {
            TempData["err"] = "کرایهٔ این سفر پیش‌تر به همین کاربر مسترد شده است؛ این درخواست را رد کنید.";
            return back;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var n = await db.Refunds.Where(x => x.RefundId == id && x.Status == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "done").SetProperty(x => x.DoneAt, DateTime.UtcNow)
                .SetProperty(x => x.ReviewedByAdminId, me.Id), ct);
        if (n == 0) { TempData["err"] = "این درخواست استرداد پیش‌تر بررسی شده است."; return back; }

        var row = await wallet.PostAsync(r.OwnerKind, r.OwnerId, r.Amount, WalletTxnKind.Refund, r.TripId, $"استرداد #{r.RefundId}: {r.Reason}", ct);
        row.PaymentId = r.PaymentId;
        if (r.PaymentId is int pid)
            await db.Payments.Where(p => p.PaymentId == pid && p.Status == PaymentStatus.Paid)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Refunded), ct);

        audit.Add("Refund", id, "complete", $"استرداد {Fa.Toman(r.Amount)} به کیف پول", new { r.OwnerKind, r.OwnerId, r.Amount, r.TripId, r.PaymentId });
        notify.Add(r.OwnerKind, r.OwnerId, "استرداد وجه انجام شد", $"{Fa.Toman(r.Amount)} به کیف پول شما برگشت.",
            $"/{BackofficeLookup.PanelOf(r.OwnerKind)}/Finance", "finance");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        TempData["ok"] = $"{Fa.Toman(r.Amount)} به کیف پول مسترد شد.";
        return back;
    }

    [HttpPost]
    public async Task<IActionResult> RejectRefund(int id, string? note, CancellationToken ct)
    {
        var back = RedirectToAction(nameof(Refunds));
        note = (note ?? "").Trim();
        if (note.Length < 3) { TempData["err"] = "علت رد استرداد را بنویسید."; return back; }

        var r = await db.Refunds.FirstOrDefaultAsync(x => x.RefundId == id, ct);
        if (r is null) return NotFound();
        if (r.Status != "pending") { TempData["err"] = "این درخواست استرداد پیش‌تر بررسی شده است."; return back; }

        // جدول Refund ستونِ یادداشت بررسی ندارد؛ علت رد زیر شرحِ درخواست می‌نشیند تا در
        // پنل کاربر هم دیده شود (و در ردّ حسابرسی کامل ثبت است).
        r.Status = "rejected";
        r.ReviewedByAdminId = me.Id;
        r.DoneAt = DateTime.UtcNow;
        r.Reason = $"{r.Reason}\n— علت رد: {note}";
        audit.Add("Refund", id, "reject", $"رد درخواست استرداد {Fa.Toman(r.Amount)}", new { r.OwnerKind, r.OwnerId, r.Amount, note });
        notify.Add(r.OwnerKind, r.OwnerId, "درخواست استرداد رد شد", note, $"/{BackofficeLookup.PanelOf(r.OwnerKind)}/Finance", "finance");
        await db.SaveChangesAsync(ct);

        TempData["ok"] = "درخواست استرداد رد شد.";
        return back;
    }

    // ------------------------------------------------------------------
    //  گزارش درآمد — ۱۲ ماه شمسی
    // ------------------------------------------------------------------

    public async Task<IActionResult> Revenue(CancellationToken ct)
    {
        ViewData["Title"] = "گزارش درآمد";
        var months = BackofficeLookup.LastPersianMonths(12);
        var start = months[0].StartUtc;
        var off = BackofficeLookup.TehranOffsetMinutes;

        // تجمیع روزانه در SQL، سپس جمعِ روزها در ماه شمسی در حافظه
        var days = await db.Trips.AsNoTracking()
            .Where(t => t.SettledAt != null && t.SettledAt >= start)
            .GroupBy(t => t.SettledAt!.Value.AddMinutes(off).Date)
            .Select(g => new DayAgg { Day = g.Key, N = g.Count(), A = g.Sum(t => t.Fare), B = g.Sum(t => t.Commission) })
            .ToListAsync(ct);

        var rows = months.Select(m =>
        {
            var inMonth = days.Where(d => BackofficeLookup.Pc.GetYear(d.Day) == m.Y && BackofficeLookup.Pc.GetMonth(d.Day) == m.M).ToList();
            return new RevenueRowVm
            {
                Label = BackofficeLookup.MonthLabel(m.Y, m.M),
                Fare = inMonth.Sum(d => d.A),
                Commission = inMonth.Sum(d => d.B),
                Trips = inMonth.Sum(d => d.N)
            };
        }).ToList();

        return View(new RevenueVm { Rows = rows });
    }
}
