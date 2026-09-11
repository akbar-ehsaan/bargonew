using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.ShipperPanel.Controllers;

/// <summary>
/// کیف پول و پرداخت‌های صاحب بار. موجودی فقط از WalletService و پرداخت کرایه فقط از
/// TripFlow تغییر می‌کند. شارژ تا اتصال درگاه واقعی «آزمایشی» است و صفحه صریحاً همین را می‌گوید.
/// </summary>
[Area("Shipper")]
[Authorize(Roles = Roles.Shipper)]
public class FinanceController(BargoDbContext db, CurrentUser me, WalletService wallet, TripFlow flow, SettingsService settings) : Controller
{
    private const long MaxChargeRial = 10_000_000_000; // ۱ میلیارد تومان در هر شارژ

    public static readonly string[] TxnKinds = [WalletTxnKind.Charge, WalletTxnKind.FarePayment, WalletTxnKind.Refund, WalletTxnKind.Adjustment];

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "کیف پول";
        var actor = me.ToActor();
        var unpaid = db.Trips.AsNoTracking().VisibleTo(actor).Where(t => !t.IsPaid && t.Status != TripStatus.Cancelled);
        var monthStart = Fa.MonthStartUtc;

        var vm = new ShipperFinanceVm
        {
            Balance = await wallet.BalanceAsync(OwnerKind.Shipper, me.Id, ct),
            UnpaidCount = await unpaid.CountAsync(ct),
            UnpaidTotal = await unpaid.SumAsync(t => (long?)(t.Fare + t.LoadingFee + t.UnloadingFee + t.WaybillFee + t.Vat), ct) ?? 0,
            PaidThisMonth = -(await db.WalletTransactions.AsNoTracking().Of(me.Owner)
                .Where(t => t.Kind == WalletTxnKind.FarePayment && t.CreatedAt >= monthStart)
                .SumAsync(t => (long?)t.Amount, ct) ?? 0),
            RefundedTotal = await db.Refunds.AsNoTracking().Of(me.Owner).Where(r => r.Status == "done").SumAsync(r => (long?)r.Amount, ct) ?? 0,
            DemoGateway = (await settings.GetAsync(SettingsService.Keys.GatewayProvider, ct)).Trim().ToLowerInvariant() is "" or "demo",
            Recent = await db.WalletTransactions.AsNoTracking().Of(me.Owner).OrderByDescending(t => t.WalletTransactionId).Take(10).ToListAsync(ct),
            Charges = await db.Payments.AsNoTracking()
                .Where(p => p.PayerKind == OwnerKind.Shipper && p.PayerId == me.Id && p.Purpose == "charge")
                .OrderByDescending(p => p.PaymentId).Take(5).ToListAsync(ct)
        };
        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> Pay(CancellationToken ct)
    {
        ViewData["Title"] = "پرداخت کرایه";
        return View(await PayVmAsync(ct));
    }

    [HttpPost]
    public async Task<IActionResult> Pay(int tripId, string? returnUrl, CancellationToken ct)
    {
        var trip = await flow.FindForAsync(tripId, me.ToActor(), ct);
        if (trip is null) return NotFound();
        try
        {
            await flow.PayFareFromWalletAsync(trip, me.ToActor(), ct);
            TempData["ok"] = $"کرایه و هزینه‌های سفر {trip.Code} ({Fa.Toman(trip.TotalPayable)}) پرداخت شد. مبلغ تا تحویل بار نزد بارگو امانت می‌ماند.";
        }
        catch (UserError e)
        {
            TempData["err"] = e.Message.Contains("موجودی")
                ? $"{e.Message} برای پرداخت {Fa.Toman(trip.TotalPayable)} ابتدا کیف پول را شارژ کنید."
                : e.Message;
        }
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : RedirectToAction(nameof(Pay));
    }

    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        ViewData["Title"] = "پرداخت‌های در انتظار";
        return View(await PayVmAsync(ct));
    }

    public async Task<IActionResult> Transactions(string? kind, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "تراکنش‌های کیف پول";
        var q = db.WalletTransactions.AsNoTracking().Of(me.Owner);
        if (!string.IsNullOrEmpty(kind) && TxnKinds.Contains(kind)) q = q.Where(t => t.Kind == kind);
        else kind = null;

        ViewBag.Kind = kind;
        ViewBag.Balance = await wallet.BalanceAsync(OwnerKind.Shipper, me.Id, ct);
        return View(await PageVm<WalletTransaction>.FromAsync(q.OrderByDescending(t => t.WalletTransactionId), page, PageLink.For(Request), 30, ct));
    }

    public async Task<IActionResult> Invoices(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "فاکتورها";
        var q = db.Invoices.AsNoTracking().Of(me.Owner).Include(i => i.Trip).OrderByDescending(i => i.IssuedAt);
        ViewBag.Sum = await db.Invoices.AsNoTracking().Of(me.Owner).SumAsync(i => (long?)i.Total, ct) ?? 0;
        return View(await PageVm<Invoice>.FromAsync(q, page, PageLink.For(Request), 30, ct));
    }

    public async Task<IActionResult> Refunds(int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "استرداد وجه";
        var q = db.Refunds.AsNoTracking().Of(me.Owner).OrderByDescending(r => r.RefundId)
            .Select(r => new ShipperRefundRow
            {
                RefundId = r.RefundId,
                TripId = r.TripId,
                TripCode = db.Trips.Where(t => t.TripId == r.TripId).Select(t => t.Code).FirstOrDefault(),
                Amount = r.Amount,
                Reason = r.Reason,
                Status = r.Status,
                CreatedAt = r.CreatedAt,
                DoneAt = r.DoneAt
            });
        return View(await PageVm<ShipperRefundRow>.FromAsync(q, page, PageLink.For(Request), 30, ct));
    }

    private async Task<ShipperPayVm> PayVmAsync(CancellationToken ct) => new ShipperPayVm
    {
        Balance = await wallet.BalanceAsync(OwnerKind.Shipper, me.Id, ct),
        Unpaid = await db.Trips.AsNoTracking().VisibleTo(me.ToActor())
            .Where(t => !t.IsPaid && t.Status != TripStatus.Cancelled)
            .OrderBy(t => t.ScheduledDepartureAt ?? t.CreatedAt)
            .Select(ShipperTripRow.FromTrip).ToListAsync(ct),
        PendingPayments = await db.Payments.AsNoTracking()
            .Where(p => p.PayerKind == OwnerKind.Shipper && p.PayerId == me.Id && p.Status == PaymentStatus.Pending)
            .OrderByDescending(p => p.PaymentId).ToListAsync(ct),
        FailedPayments = await db.Payments.AsNoTracking()
            .Where(p => p.PayerKind == OwnerKind.Shipper && p.PayerId == me.Id && p.Status == PaymentStatus.Failed)
            .OrderByDescending(p => p.PaymentId).Take(10).ToListAsync(ct)
    };
}
