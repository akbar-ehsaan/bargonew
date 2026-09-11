using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Models.ViewModels;
using Bargo.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Areas.CompanyPanel.Controllers;

/// <summary>
/// دفتر ماهانهٔ شرکت — مشترک «گزارش مالی» و «درآمد دوره‌ای». هر ستون از منبعِ
/// واقعیِ خودش خوانده می‌شود، نه از فرضِ روی سفر:
///   درآمد کرایه و سهم رانندگان و کرایهٔ پرداختی ← گردش کیف پول (تاریخ واقعی جابه‌جایی پول)
///   کرایهٔ مستقیم مشتریان ← سفرهای بارِ خودِ شرکت که تحویل شده‌اند (پولی از بارگو نمی‌گذرد)
///   کمیسیون ← سفرهای بازار که در همان ماه تسویه شده‌اند
///   هزینه‌ها ← CompanyExpenses بر پایهٔ تاریخ خرج
/// ردیف‌ها یک‌جا خوانده و در حافظه به ماه شمسی تقسیم می‌شوند؛ مرز ماه شمسی در SQL
/// قابل بیان نیست و حجمِ یک شرکت در چند ماه کوچک است.
/// </summary>
internal static class CompanyLedger
{
    public static async Task<List<LedgerMonth>> MonthlyAsync(BargoDbContext db, int companyId, List<PersianMonth> months,
        CancellationToken ct)
    {
        var rows = months.Select(m => new LedgerMonth { Month = m }).ToList();
        if (rows.Count == 0) return rows;
        var from = months[0].StartUtc;
        var to = months[^1].EndUtc;

        string[] kinds = [WalletTxnKind.FareIncome, WalletTxnKind.DriverShare, WalletTxnKind.FarePayment, WalletTxnKind.Refund];
        var txns = await db.WalletTransactions.AsNoTracking()
            .Where(t => t.OwnerKind == OwnerKind.Company && t.OwnerId == companyId && kinds.Contains(t.Kind)
                        && t.CreatedAt >= from && t.CreatedAt < to)
            .Select(t => new { t.Kind, t.Amount, t.CreatedAt })
            .ToListAsync(ct);

        var expenses = await db.CompanyExpenses.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.SpentAt >= from && e.SpentAt < to)
            .Select(e => new { e.Amount, e.SpentAt })
            .ToListAsync(ct);

        var settled = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.SettledAt >= from && t.SettledAt < to
                        && (t.Load!.CompanyId == null || t.Load.CompanyId != companyId))
            .Select(t => new { t.Commission, At = t.SettledAt!.Value })
            .ToListAsync(ct);

        var delivered = await db.Trips.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.DeliveredAt >= from && t.DeliveredAt < to
                        && (t.Status == TripStatus.Delivered || t.Status == TripStatus.Settled))
            .Select(t => new { t.Fare, At = t.DeliveredAt!.Value, Own = t.Load!.CompanyId == companyId })
            .ToListAsync(ct);

        LedgerMonth? Find(DateTime utc) => rows.FirstOrDefault(r => r.Month.Contains(utc));

        foreach (var t in txns)
        {
            if (Find(t.CreatedAt) is not { } r) continue;
            switch (t.Kind)
            {
                case WalletTxnKind.FareIncome: r.Income += t.Amount; break;
                case WalletTxnKind.DriverShare: r.DriverShares += -t.Amount; break;
                case WalletTxnKind.FarePayment: r.FarePaid += -t.Amount; break;
                // استردادِ کرایهٔ سفر لغوشده، کرایهٔ پرداختی همان ماه را کم می‌کند
                case WalletTxnKind.Refund: r.FarePaid -= t.Amount; break;
            }
        }
        foreach (var e in expenses)
            if (Find(e.SpentAt) is { } r) r.Expenses += e.Amount;
        foreach (var s in settled)
            if (Find(s.At) is { } r) r.Commission += s.Commission;
        foreach (var d in delivered)
        {
            if (Find(d.At) is not { } r) continue;
            r.TripsDone++;
            if (d.Own) r.Offline += d.Fare;
        }
        return rows;
    }

    /// <summary>
    /// ردیف نمایشیِ سفر. partyIsCarrier: طرف حساب حمل‌کننده است (بارهای خودِ شرکت که
    /// دیگری حمل کرده)؛ وگرنه صاحب بار (سفرهایی که شرکت حمل کرده).
    /// </summary>
    public static IQueryable<CompanyTripRow> AsRows(this IQueryable<Trip> q, bool partyIsCarrier) => partyIsCarrier
        ? q.Select(t => new CompanyTripRow
        {
            TripId = t.TripId, Code = t.Code, LoadCode = t.Load!.Code,
            Origin = t.Load.OriginCity!.Name, Dest = t.Load.DestCity!.Name, CargoTitle = t.Load.Title,
            Status = t.Status, Fare = t.Fare, Total = t.Fare + t.LoadingFee + t.UnloadingFee + t.WaybillFee + t.Vat,
            Commission = t.Commission, CarrierShare = t.CarrierShare, DriverShare = t.DriverShare,
            IsPaid = t.IsPaid, CreatedAt = t.CreatedAt, DeliveredAt = t.DeliveredAt, SettledAt = t.SettledAt, DriverSharePaidAt = t.DriverSharePaidAt,
            Party = !t.IsPaid ? Privacy.HiddenName
                : t.Company != null ? t.Company.Name : t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : "—",
            DriverId = t.DriverId,
            DriverName = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null
        })
        : q.Select(t => new CompanyTripRow
        {
            TripId = t.TripId, Code = t.Code, LoadCode = t.Load!.Code,
            Origin = t.Load.OriginCity!.Name, Dest = t.Load.DestCity!.Name, CargoTitle = t.Load.Title,
            Status = t.Status, Fare = t.Fare, Total = t.Fare + t.LoadingFee + t.UnloadingFee + t.WaybillFee + t.Vat,
            Commission = t.Commission, CarrierShare = t.CarrierShare, DriverShare = t.DriverShare,
            IsPaid = t.IsPaid, CreatedAt = t.CreatedAt, DeliveredAt = t.DeliveredAt, SettledAt = t.SettledAt, DriverSharePaidAt = t.DriverSharePaidAt,
            Party = t.Load.Shipper != null
                ? (!t.IsPaid ? Privacy.HiddenName
                    : t.Load.Shipper.Kind == "business" && t.Load.Shipper.BusinessName != null && t.Load.Shipper.BusinessName != ""
                        ? t.Load.Shipper.BusinessName : t.Load.Shipper.FullName)
                : "بار مشتریِ شرکت",
            DriverId = t.DriverId,
            DriverName = t.Driver != null ? t.Driver.FirstName + " " + t.Driver.LastName : null
        });

    /// <summary>مبلغی از کیف پول که در درخواست‌های برداشتِ باز (در انتظار / تأییدشده) قفل است.</summary>
    public static async Task<long> ReservedForPayoutAsync(BargoDbContext db, int companyId, CancellationToken ct) =>
        await db.PayoutRequests
            .Where(p => p.OwnerKind == OwnerKind.Company && p.OwnerId == companyId
                        && (p.Status == PayoutStatus.Pending || p.Status == PayoutStatus.Approved))
            .SumAsync(p => (long?)p.Amount, ct) ?? 0;
}
