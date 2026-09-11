using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

/// <summary>
/// درآمد و مالی راننده. موجودی فقط از WalletService خوانده می‌شود و این کنترلر هیچ‌وقت
/// کیف پول را کم و زیاد نمی‌کند: «درخواست برداشت» فقط یک PayoutRequest در انتظار
/// می‌سازد و کسر از کیف پول در لحظهٔ واریز بانکی توسط مدیر انجام می‌شود.
///
/// «موجودی قابل برداشت» = موجودی − جمع درخواست‌های باز (در انتظار یا تأییدشده ولی
/// هنوز واریزنشده)؛ وگرنه راننده می‌توانست یک مبلغ را دو بار درخواست کند.
/// </summary>
[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class FinanceController(BargoDbContext db, CurrentUser me, WalletService wallet, SettingsService settings) : Controller
{
    /// <summary>ردیف‌هایی که درآمد راننده‌اند: سهم کرایهٔ رانندهٔ مستقل و سهمی که شرکت به رانندهٔ عضوش می‌دهد.</summary>
    private static readonly string[] IncomeKinds = [WalletTxnKind.FareIncome, WalletTxnKind.DriverShare];

    /// <summary>نوع‌هایی که در گردش کیف پول راننده معنا دارند — فیلتر صفحهٔ تراکنش‌ها.</summary>
    private static readonly string[] TxnKinds =
    [
        WalletTxnKind.FareIncome, WalletTxnKind.DriverShare, WalletTxnKind.Payout,
        WalletTxnKind.Charge, WalletTxnKind.Refund, WalletTxnKind.Subscription, WalletTxnKind.Adjustment
    ];

    private static readonly string[] OpenPayout = [PayoutStatus.Pending, PayoutStatus.Approved];

    // ------------------------------------------------------------------ کیف پول

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "کیف پول";
        var vm = await WalletAsync(ct);
        vm.Recent = await Txns().OrderByDescending(t => t.WalletTransactionId).Take(10).ToRows(db).ToListAsync(ct);
        vm.OpenPayouts = await db.PayoutRequests.AsNoTracking().Of(me.Owner)
            .Where(p => OpenPayout.Contains(p.Status)).OrderBy(p => p.PayoutRequestId).ToListAsync(ct);
        // سفرهای شرکتیِ تمام‌شده که شرکت هنوز سهم راننده را نپرداخته — پول هست ولی در کیف پول نیست
        vm.UnpaidCompanyShares = await db.Trips.AsNoTracking()
            .CountAsync(t => t.DriverId == me.Id && t.CarrierKind == CarrierKind.Company && t.DriverShare > 0 &&
                             t.DriverSharePaidAt == null && TripStatus.Done.Contains(t.Status), ct);
        return View(vm);
    }

    // ------------------------------------------------------------------ درآمدها

    public async Task<IActionResult> Earnings(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "درآمدها";
        var income = Income();

        // شمار ردیف‌های درآمد از مرتبهٔ شمار سفرهاست؛ گروه‌بندی ماه شمسی در حافظه ساده‌تر و
        // دقیق‌تر از شکستن مرز ماه در SQL است.
        var all = await income.Select(t => new { t.CreatedAt, t.Amount }).ToListAsync(ct);
        var months = all.GroupBy(x => DriverMonth.Of(x.CreatedAt))
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
            .Select(g => new MonthIncomeRow(g.Key.Label, g.Count(), g.Sum(x => x.Amount)))
            .ToList();

        var monthStart = Fa.MonthStartUtc;
        var vm = new EarningsVm
        {
            Months = months,
            Page = await PageVm<TxnRowVm>.FromAsync(income.OrderByDescending(t => t.WalletTransactionId).ToRows(db), page, PageLink.For(Request), ct: ct),
            Total = all.Sum(x => x.Amount),
            ThisMonth = all.Where(x => x.CreatedAt >= monthStart).Sum(x => x.Amount),
            TripCount = await income.Where(t => t.TripId != null).Select(t => t.TripId).Distinct().CountAsync(ct)
        };
        return View(vm);
    }

    // ------------------------------------------------------------------ تسویه‌حساب

    public async Task<IActionResult> Settlements(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تسویه‌حساب";
        var mine = db.PayoutRequests.AsNoTracking().Of(me.Owner);
        var sheba = await db.Drivers.AsNoTracking().Where(d => d.DriverId == me.Id).Select(d => d.Sheba).FirstOrDefaultAsync(ct);
        var vm = new SettlementsVm
        {
            Page = await PageVm<PayoutRequest>.FromAsync(mine.OrderByDescending(p => p.PayoutRequestId), page, PageLink.For(Request), ct: ct),
            Balance = await wallet.BalanceAsync(OwnerKind.Driver, me.Id, ct),
            OpenSum = await mine.Where(p => OpenPayout.Contains(p.Status)).SumAsync(p => (long?)p.Amount, ct) ?? 0,
            PaidSum = await mine.Where(p => p.Status == PayoutStatus.Paid).SumAsync(p => (long?)p.Amount, ct) ?? 0,
            MinPayout = await settings.GetLongAsync(SettingsService.Keys.MinPayoutRial, ct),
            HasValidSheba = IranId.IsSheba(sheba)
        };
        return View(vm);
    }

    // ------------------------------------------------------------------ درخواست برداشت

    [HttpGet]
    public async Task<IActionResult> Withdraw(CancellationToken ct)
    {
        ViewData["Title"] = "درخواست برداشت";
        var vm = await WalletAsync(ct);
        vm.OpenPayouts = await db.PayoutRequests.AsNoTracking().Of(me.Owner)
            .Where(p => OpenPayout.Contains(p.Status)).OrderBy(p => p.PayoutRequestId).ToListAsync(ct);
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Withdraw(string? amountToman, CancellationToken ct)
    {
        var driver = await db.Drivers.AsNoTracking().FirstOrDefaultAsync(d => d.DriverId == me.Id, ct);
        if (driver is null) return NotFound();

        try
        {
            if (!IranId.IsSheba(driver.Sheba))
                throw new UserError("برای برداشت ابتدا شمارهٔ شبای حساب بانکی خود را در «حساب کاربری» ثبت کنید.");

            var rial = Fa.ParseToman(amountToman);
            if (rial is null or <= 0) throw new UserError("مبلغ برداشت را به تومان و فقط با رقم وارد کنید.");

            var min = await settings.GetLongAsync(SettingsService.Keys.MinPayoutRial, ct);
            if (rial < min) throw new UserError($"حداقل مبلغ هر درخواست برداشت {Fa.Toman(min)} است.");

            var reserved = await db.PayoutRequests.Of(me.Owner)
                .Where(p => OpenPayout.Contains(p.Status)).SumAsync(p => (long?)p.Amount, ct) ?? 0;
            var available = Math.Max(0, driver.WalletBalance - reserved);
            if (rial > available)
                throw new UserError(reserved > 0
                    ? $"موجودی قابل برداشت {Fa.Toman(available)} است ({Fa.Toman(reserved)} در درخواست‌های باز قبلی رزرو شده)."
                    : $"موجودی قابل برداشت {Fa.Toman(available)} است.");

            db.PayoutRequests.Add(new PayoutRequest
            {
                OwnerKind = OwnerKind.Driver,
                OwnerId = me.Id,
                OwnerName = driver.FullName,
                Amount = rial.Value,
                Sheba = IranId.NormSheba(driver.Sheba),
                Status = PayoutStatus.Pending
            });
            await db.SaveChangesAsync(ct);
            TempData["ok"] = $"درخواست برداشت {Fa.Toman(rial.Value)} ثبت شد. پس از بررسی مدیر، مبلغ به شبای {driver.Sheba} واریز و از کیف پول کسر می‌شود.";
            return RedirectToAction(nameof(Settlements));
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message;
            return RedirectToAction(nameof(Withdraw));
        }
    }

    /// <summary>پس‌گرفتن درخواستی که هنوز مدیر ندیده. درخواستِ تأییدشده در صف بانک است و دیگر برگشت‌پذیر نیست.</summary>
    [HttpPost]
    public async Task<IActionResult> CancelWithdraw(int id, CancellationToken ct)
    {
        var pr = await db.PayoutRequests.AsNoTracking().Of(me.Owner).FirstOrDefaultAsync(p => p.PayoutRequestId == id, ct);
        if (pr is null) return NotFound();

        var n = await db.PayoutRequests.Of(me.Owner)
            .Where(p => p.PayoutRequestId == id && p.Status == PayoutStatus.Pending).ExecuteDeleteAsync(ct);
        TempData[n == 0 ? "err" : "ok"] = n == 0
            ? "این درخواست بررسی شده و دیگر قابل پس‌گرفتن نیست."
            : $"درخواست برداشت {Fa.Toman(pr.Amount)} پس گرفته شد و مبلغ دوباره قابل برداشت است.";
        return RedirectToAction(nameof(Settlements));
    }

    // ------------------------------------------------------------------ تراکنش‌ها

    public async Task<IActionResult> Transactions(string? kind, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تراکنش‌های کیف پول";
        var q = Txns();
        if (!string.IsNullOrEmpty(kind) && TxnKinds.Contains(kind)) q = q.Where(t => t.Kind == kind);
        else kind = null;

        ViewBag.Balance = await wallet.BalanceAsync(OwnerKind.Driver, me.Id, ct);
        return View(new TransactionsVm
        {
            Kind = kind,
            Kinds = [.. TxnKinds],
            Page = await PageVm<TxnRowVm>.FromAsync(q.OrderByDescending(t => t.WalletTransactionId).ToRows(db), page, PageLink.For(Request), ct: ct)
        });
    }

    // ------------------------------------------------------------------ فاکتورها

    public async Task<IActionResult> Invoices(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "فاکتورها";
        var mine = db.Invoices.AsNoTracking().Of(me.Owner);
        var rows = mine.OrderByDescending(i => i.IssuedAt).ThenByDescending(i => i.InvoiceId).Select(i => new InvoiceRowVm
        {
            InvoiceId = i.InvoiceId,
            No = i.No,
            Kind = i.Kind,
            Amount = i.Amount,
            Tax = i.Tax,
            Total = i.Total,
            Status = i.Status,
            IssuedAt = i.IssuedAt,
            TripId = i.TripId,
            TripCode = i.Trip!.Code
        });
        ViewBag.Sum = await mine.SumAsync(i => (long?)i.Total, ct) ?? 0;
        return View(await PageVm<InvoiceRowVm>.FromAsync(rows, page, PageLink.For(Request), ct: ct));
    }

    // ------------------------------------------------------------------

    private IQueryable<WalletTransaction> Txns() => db.WalletTransactions.AsNoTracking().Of(me.Owner);

    private IQueryable<WalletTransaction> Income() => Txns().Where(t => IncomeKinds.Contains(t.Kind) && t.Amount > 0);

    /// <summary>عددهای بالای صفحهٔ کیف پول و فرم برداشت — یک‌جا تا دو صفحه یک رقم را متفاوت نشان ندهند.</summary>
    private async Task<WalletVm> WalletAsync(CancellationToken ct)
    {
        var driver = await db.Drivers.AsNoTracking().Where(d => d.DriverId == me.Id)
            .Select(d => new { d.WalletBalance, d.Sheba }).FirstOrDefaultAsync(ct);
        var monthStart = Fa.MonthStartUtc;
        return new WalletVm
        {
            Balance = driver?.WalletBalance ?? 0,
            Reserved = await db.PayoutRequests.AsNoTracking().Of(me.Owner)
                .Where(p => OpenPayout.Contains(p.Status)).SumAsync(p => (long?)p.Amount, ct) ?? 0,
            MonthIncome = await Income().Where(t => t.CreatedAt >= monthStart).SumAsync(t => (long?)t.Amount, ct) ?? 0,
            TotalIncome = await Income().SumAsync(t => (long?)t.Amount, ct) ?? 0,
            MinPayout = await settings.GetLongAsync(SettingsService.Keys.MinPayoutRial, ct),
            Sheba = driver?.Sheba,
            HasValidSheba = IranId.IsSheba(driver?.Sheba)
        };
    }
}

internal static class FinanceQueries
{
    /// <summary>ردیف تراکنش برای جدول‌ها؛ کد سفر با زیرپرس‌وجو تا Include روی جدول پرحجم لازم نباشد.</summary>
    public static IQueryable<TxnRowVm> ToRows(this IQueryable<WalletTransaction> q, BargoDbContext db) =>
        q.Select(t => new TxnRowVm
        {
            WalletTransactionId = t.WalletTransactionId,
            Amount = t.Amount,
            BalanceAfter = t.BalanceAfter,
            Kind = t.Kind,
            TripId = t.TripId,
            TripCode = db.Trips.Where(x => x.TripId == t.TripId).Select(x => x.Code).FirstOrDefault(),
            Note = t.Note,
            CreatedAt = t.CreatedAt
        });
}
