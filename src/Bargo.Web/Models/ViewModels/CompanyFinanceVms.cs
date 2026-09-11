using Bargo.Web.Models.Entities;

namespace Bargo.Web.Models.ViewModels;

// ---------------------------------------------------------------------------
//  پنل شرکت: مالی و حسابداری + گزارش‌ها (مکمّل CompanyBackofficeVms — فقط افزوده)
// ---------------------------------------------------------------------------

/// <summary>نوار افقی CSS: <c>&lt;partial name="_Bar" model="new BarVm(v, max)"/&gt;</c></summary>
public sealed record BarVm(double Value, double Max, string Color = "var(--primary)");

/// <summary>«کیف پول شرکت»</summary>
public sealed class CompanyWalletVm
{
    public long Balance { get; init; }
    /// <summary>مبلغ قفل‌شده در درخواست‌های برداشتِ باز.</summary>
    public long Reserved { get; init; }
    public long Available => Math.Max(0, Balance - Reserved);
    public long MinPayout { get; init; }
    public string? Sheba { get; init; }
    public string? BankName { get; init; }
    public long IncomeThisMonth { get; init; }
    public long DriverSharesThisMonth { get; init; }
    public long ExpensesThisMonth { get; init; }
    public long DriverDues { get; init; }
    public List<TxnRow> Recent { get; init; } = [];
    public List<PayoutRequest> Payouts { get; init; } = [];

    public bool CanWithdraw => !string.IsNullOrEmpty(Sheba) && Available >= MinPayout;
}

/// <summary>«درآمد»</summary>
public sealed class CompanyIncomeVm
{
    public List<LedgerMonth> Months { get; init; } = [];
    public required PageVm<TxnRow> Page { get; init; }
    public long ThisMonth { get; init; }
    public long Total => Months.Sum(m => m.Revenue);
    public long TotalOnline => Months.Sum(m => m.Income);
    public long TotalOffline => Months.Sum(m => m.Offline);
    public int TripsDone => Months.Sum(m => m.TripsDone);
    public long MaxRevenue => Months.Count == 0 ? 0 : Months.Max(m => m.Revenue);
}

/// <summary>«هزینه»</summary>
public sealed class CompanyExpensesVm
{
    public required PageVm<ExpenseRow> Page { get; init; }
    public required ReportRange Range { get; init; }
    public string? Kind { get; init; }
    public List<LabelCount> ByKind { get; init; } = [];
    public long Total => ByKind.Sum(k => k.Amount);
    public int Count => ByKind.Sum(k => k.Count);
    public long MaxKind => ByKind.Count == 0 ? 0 : ByKind.Max(k => k.Amount);
    public List<SelectItem> Drivers { get; init; } = [];
    public List<SelectItem> Vehicles { get; init; } = [];
}

/// <summary>«مطالبات»</summary>
public sealed class CompanyReceivablesVm
{
    /// <summary>سفرهای بازار که شرکت حمل می‌کند و صاحب بار هنوز کرایه را نداده.</summary>
    public required PageVm<CompanyTripRow> Unpaid { get; init; }
    public long UnpaidTotal { get; init; }
    public long UnpaidShare { get; init; }
    /// <summary>بارهای مشتریانِ خود شرکت که با ناوگان خودش تحویل داده — کرایه بیرون از بارگو.</summary>
    public List<CompanyTripRow> Offline { get; init; } = [];
    public long OfflineTotal { get; init; }
}

/// <summary>«بدهی‌ها»</summary>
public sealed class CompanyPayablesVm
{
    /// <summary>سهم رانندگان از سفرهای تسویه‌شده که هنوز پرداخت نشده.</summary>
    public List<CompanyTripRow> DriverShares { get; init; } = [];
    public long DriverSharesTotal { get; init; }
    /// <summary>بارهای شرکت که حمل‌کنندهٔ دیگری می‌برد و کرایه‌شان پرداخت نشده.</summary>
    public List<CompanyTripRow> Fares { get; init; } = [];
    public long FaresTotal { get; init; }
    public long Balance { get; init; }
    public long Total => DriverSharesTotal + FaresTotal;
}

/// <summary>«تسویه با رانندگان»</summary>
public sealed class DriverSettlementsVm
{
    public long Balance { get; init; }
    public List<DriverDueRow> Due { get; init; } = [];
    public long TotalDue => Due.Sum(d => d.Amount);
    public int TotalTrips => Due.Sum(d => d.Trips);
    public DriverDueRow? Selected { get; init; }
    /// <summary>سفرهای پرداخت‌نشدهٔ رانندهٔ انتخاب‌شده.</summary>
    public List<CompanyTripRow> Trips { get; init; } = [];
    /// <summary>پرداخت‌های اخیرِ سهم راننده (گردش کیف پول شرکت).</summary>
    public List<TxnRow> History { get; init; } = [];
    public long PaidThisMonth { get; init; }
}

/// <summary>«تسویه با صاحبان بار» — بارهای خودِ شرکت که حمل‌کنندهٔ دیگری می‌برد.</summary>
public sealed class ShipperSettlementsVm
{
    public long Balance { get; init; }
    public string Tab { get; init; } = "unpaid";
    public required PageVm<CompanyTripRow> Rows { get; init; }
    public int UnpaidCount { get; init; }
    public long UnpaidTotal { get; init; }
    public int PaidCount { get; init; }
    public long PaidThisMonth { get; init; }
}

/// <summary>«گزارش مالی» و «درآمد دوره‌ای»</summary>
public sealed class LedgerReportVm
{
    public List<LedgerMonth> Months { get; init; } = [];
    public ReportRange? Range { get; init; }
    public long Income => Months.Sum(m => m.Income);
    public long Offline => Months.Sum(m => m.Offline);
    public long Revenue => Months.Sum(m => m.Revenue);
    public long Commission => Months.Sum(m => m.Commission);
    public long FarePaid => Months.Sum(m => m.FarePaid);
    public long DriverShares => Months.Sum(m => m.DriverShares);
    public long Expenses => Months.Sum(m => m.Expenses);
    public long Costs => Months.Sum(m => m.Costs);
    public long Net => Revenue - Costs;
    public int TripsDone => Months.Sum(m => m.TripsDone);
    public long MaxBar => Months.Count == 0 ? 0 : Math.Max(Months.Max(m => m.Revenue), Months.Max(m => m.Costs));
}

/// <summary>«تعداد سفر»</summary>
public sealed class TripsReportVm
{
    public required ReportRange Range { get; init; }
    public int Total { get; init; }
    public int Done { get; init; }
    public int Cancelled { get; init; }
    public int Active { get; init; }
    public long Fare { get; init; }
    public long CarrierShare { get; init; }
    public double Km { get; init; }
    public decimal Tons { get; init; }
    public List<LabelCount> ByStatus { get; init; } = [];
    public List<LabelCount> ByMonth { get; init; } = [];
    public List<LabelCount> BySource { get; init; } = [];
    public List<LabelCount> ByWeekday { get; init; } = [];

    public double DonePct => Total == 0 ? 0 : Done * 100.0 / Total;
    public double CancelPct => Total == 0 ? 0 : Cancelled * 100.0 / Total;
    public long AvgFare => Done == 0 ? 0 : Fare / Done;
    public double PerDay => Total / (double)Range.Days;
}
