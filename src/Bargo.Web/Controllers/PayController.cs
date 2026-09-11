using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Controllers;

/// <summary>
/// شارژ کیف پول از درگاه بانکی — برای هر سه نقش (صاحب بار، راننده، شرکت).
/// اگر Gateway.Provider=demo باشد شارژ آزمایشی و بی‌درنگ است؛ با zarinpal کاربر به
/// درگاه می‌رود و پول فقط پس از Verify موفق در Callback به کیف پول می‌نشیند.
/// رکورد Payment (با Authority) واسط این دو مرحله است و تکرار Callback دوبار شارژ نمی‌کند.
/// </summary>
[Authorize]
public class PayController(BargoDbContext db, CurrentUser me, WalletService wallet, SettingsService settings, ZarinPalService zarin) : Controller
{
    private const long MaxChargeRial = 10_000_000_000; // ۱ میلیارد تومان در هر شارژ

    /// <summary>صفحهٔ کیف پولِ پنل کاربر جاری — مقصد بازگشت پس از پرداخت.</summary>
    private string WalletUrl => me.Role switch
    {
        Roles.Driver => "/Driver/Finance",
        Roles.Company => "/Company/Finance",
        _ => "/Shipper/Finance"
    };

    [HttpPost]
    public async Task<IActionResult> Charge(string? amountToman, string? returnUrl, CancellationToken ct)
    {
        var back = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : WalletUrl;
        if (me.Role is not (Roles.Shipper or Roles.Driver or Roles.Company)) return Forbid();

        var rial = Fa.ParseToman(amountToman);
        if (rial is null or <= 0) { TempData["err"] = "مبلغ شارژ را به تومان و فقط با رقم وارد کنید."; return Redirect(back); }
        if (rial > MaxChargeRial) { TempData["err"] = $"حداکثر مبلغ هر شارژ {Fa.Toman(MaxChargeRial)} است."; return Redirect(back); }

        var (kind, ownerId) = me.Owner;
        var provider = (await settings.GetAsync(SettingsService.Keys.GatewayProvider, ct)).Trim().ToLowerInvariant();

        if (provider != "zarinpal")
        {
            try
            {
                var demo = await wallet.ChargeAsync(kind, ownerId, rial.Value, ct);
                TempData["ok"] = $"کیف پول {Fa.Toman(rial.Value)} شارژ شد (پرداخت آزمایشی · کد پیگیری {demo.RefId}).";
            }
            catch (UserError e) { TempData["err"] = e.Message; }
            return Redirect(back);
        }

        var pay = new Payment
        {
            PayerKind = kind, PayerId = ownerId, Amount = rial.Value,
            Method = "gateway", Purpose = "charge", Gateway = "zarinpal", Status = PaymentStatus.Pending
        };
        db.Payments.Add(pay);
        await db.SaveChangesAsync(ct);

        var callback = Url.Action(nameof(Callback), "Pay", new { id = pay.PaymentId }, Request.Scheme)!;
        var mobile = await PayerMobileAsync(kind, ownerId, ct);
        var req = await zarin.RequestAsync(rial.Value, callback, $"شارژ کیف پول بارگو — {Fa.Toman(rial.Value)}", mobile, ct);
        if (!req.Ok || string.IsNullOrEmpty(req.Authority))
        {
            pay.Status = PaymentStatus.Failed;
            pay.FailReason = req.Error;
            await db.SaveChangesAsync(ct);
            TempData["err"] = req.Error ?? "اتصال به درگاه ممکن نشد.";
            return Redirect(back);
        }

        pay.Authority = req.Authority;
        await db.SaveChangesAsync(ct);
        return Redirect(ZarinPalService.StartPayUrl(await zarin.SandboxAsync(ct), req.Authority));
    }

    /// <summary>
    /// بازگشت از زرین‌پال. AllowAnonymous چون مرورگر از دامنهٔ درگاه برمی‌گردد؛ مالکیت
    /// از خود رکورد Payment معلوم است و صفحهٔ نتیجه چیزی جز همان پرداخت نشان نمی‌دهد.
    /// </summary>
    [HttpGet("/Pay/Callback/{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(int id, string? Authority, string? Status, CancellationToken ct)
    {
        var pay = await db.Payments.FirstOrDefaultAsync(p => p.PaymentId == id && p.Purpose == "charge" && p.Gateway == "zarinpal", ct);
        if (pay is null) return NotFound();

        // تکرار یا رفرش صفحهٔ بازگشت: همان نتیجهٔ قبلی، بدون شارژ دوباره
        if (pay.Status != PaymentStatus.Pending) return View("Result", pay);

        if (string.IsNullOrEmpty(Authority) || Authority != pay.Authority ||
            !string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            pay.Status = PaymentStatus.Failed;
            pay.FailReason = "پرداخت توسط شما لغو شد یا در درگاه ناتمام ماند.";
            await db.SaveChangesAsync(ct);
            return View("Result", pay);
        }

        var verify = await zarin.VerifyAsync(pay.Amount, Authority, ct);
        if (!verify.Ok)
        {
            pay.Status = PaymentStatus.Failed;
            pay.FailReason = verify.Error;
            await db.SaveChangesAsync(ct);
            return View("Result", pay);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        pay.Status = PaymentStatus.Paid;
        pay.PaidAt = DateTime.UtcNow;
        pay.RefId = verify.RefId.ToString();
        var wtx = await wallet.PostAsync(pay.PayerKind, pay.PayerId, pay.Amount, WalletTxnKind.Charge,
            note: $"شارژ کیف پول از درگاه (پیگیری {verify.RefId})", ct: ct);
        wtx.PaymentId = pay.PaymentId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return View("Result", pay);
    }

    private async Task<string?> PayerMobileAsync(string kind, int ownerId, CancellationToken ct) => kind switch
    {
        OwnerKind.Driver => await db.Drivers.Where(d => d.DriverId == ownerId).Select(d => d.Mobile).FirstOrDefaultAsync(ct),
        OwnerKind.Shipper => await db.Shippers.Where(s => s.ShipperId == ownerId).Select(s => s.Mobile).FirstOrDefaultAsync(ct),
        _ => null
    };
}
