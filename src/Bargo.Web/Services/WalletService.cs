using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Services;

/// <summary>
/// تنها راه تغییر موجودی کیف پول. هر تغییر یک ردیف WalletTransaction با BalanceAfter
/// می‌سازد. SaveChanges را صدا نمی‌زند تا فراخواننده بتواند چند ثبت (کرایه، کمیسیون،
/// سهم راننده) را در یک تراکنش پایگاه‌داده ذخیره کند.
/// </summary>
public class WalletService(BargoDbContext db)
{
    public async Task<WalletTransaction> PostAsync(string ownerKind, int ownerId, long amount, string kind,
        int? tripId = null, string? note = null, CancellationToken ct = default)
    {
        long after;
        switch (ownerKind)
        {
            case OwnerKind.Driver:
            {
                var d = await db.Drivers.FirstAsync(x => x.DriverId == ownerId, ct);
                after = Guard(d.WalletBalance + amount, kind);
                d.WalletBalance = after;
                break;
            }
            case OwnerKind.Shipper:
            {
                var s = await db.Shippers.FirstAsync(x => x.ShipperId == ownerId, ct);
                after = Guard(s.WalletBalance + amount, kind);
                s.WalletBalance = after;
                break;
            }
            case OwnerKind.Company:
            {
                var c = await db.Companies.FirstAsync(x => x.CompanyId == ownerId, ct);
                after = Guard(c.WalletBalance + amount, kind);
                c.WalletBalance = after;
                break;
            }
            case OwnerKind.Platform:
            {
                // کیف پول بارگو ردیف حساب ندارد؛ مانده همان BalanceAfter آخرین ردیف است —
                // با احتساب ردیف‌هایی که در همین درخواست اضافه شده و هنوز ذخیره نشده‌اند.
                var pending = db.WalletTransactions.Local
                    .Where(t => t.OwnerKind == OwnerKind.Platform && t.WalletTransactionId == 0)
                    .Select(t => (long?)t.BalanceAfter).LastOrDefault();
                var last = pending ?? await PlatformBalanceAsync(ct);
                after = last + amount;
                break;
            }
            default:
                throw new InvalidOperationException($"صاحب کیف پول ناشناخته: {ownerKind}");
        }

        var tx = new WalletTransaction
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Amount = amount,
            BalanceAfter = after,
            Kind = kind,
            TripId = tripId,
            Note = note
        };
        db.WalletTransactions.Add(tx);
        return tx;
    }

    /// <summary>برداشتی که موجودی را منفی کند رد می‌شود؛ فقط اصلاح دستی مدیر اجازه دارد.</summary>
    private static long Guard(long after, string kind) =>
        after < 0 && kind != WalletTxnKind.Adjustment ? throw new UserError("موجودی کیف پول کافی نیست.") : after;

    public Task<long> PlatformBalanceAsync(CancellationToken ct = default) =>
        db.WalletTransactions.Where(t => t.OwnerKind == OwnerKind.Platform)
            .OrderByDescending(t => t.WalletTransactionId)
            .Select(t => t.BalanceAfter).FirstOrDefaultAsync(ct);

    public async Task<long> BalanceAsync(string ownerKind, int ownerId, CancellationToken ct = default) => ownerKind switch
    {
        OwnerKind.Driver => await db.Drivers.Where(x => x.DriverId == ownerId).Select(x => x.WalletBalance).FirstOrDefaultAsync(ct),
        OwnerKind.Shipper => await db.Shippers.Where(x => x.ShipperId == ownerId).Select(x => x.WalletBalance).FirstOrDefaultAsync(ct),
        OwnerKind.Company => await db.Companies.Where(x => x.CompanyId == ownerId).Select(x => x.WalletBalance).FirstOrDefaultAsync(ct),
        OwnerKind.Platform => await PlatformBalanceAsync(ct),
        _ => 0
    };

    /// <summary>
    /// شارژ کیف پول. ⚠️ تا وصل شدن درگاه واقعی، پرداخت «آزمایشی» است و بی‌درنگ موفق
    /// ثبت می‌شود — Gateway.Provider=demo. با درگاه واقعی، این متد Payment در انتظار
    /// می‌سازد و شارژ فقط در بازگشت موفق از درگاه انجام می‌شود.
    /// </summary>
    public async Task<Payment> ChargeAsync(string ownerKind, int ownerId, long amountRial, CancellationToken ct = default)
    {
        if (amountRial < 10_000) throw new UserError("حداقل مبلغ شارژ ۱,۰۰۰ تومان است.");
        var pay = new Payment
        {
            PayerKind = ownerKind,
            PayerId = ownerId,
            Amount = amountRial,
            Method = "gateway",
            Purpose = "charge",
            Gateway = "demo",
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow,
            RefId = "DEMO-" + Random.Shared.Next(100000, 999999)
        };
        db.Payments.Add(pay);
        await PostAsync(ownerKind, ownerId, amountRial, WalletTxnKind.Charge, note: "شارژ آزمایشی کیف پول", ct: ct);
        await db.SaveChangesAsync(ct);
        return pay;
    }
}
