using System.Globalization;
using Bargo.Web.Data;
using Bargo.Web.Filters;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// پیام‌های راه‌اندازی فارسی‌اند؛ بدون این در لاگ IIS «????» ثبت می‌شوند.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

var builder = WebApplication.CreateBuilder(args);

// ---------- اسرار ----------
// رشتهٔ اتصال واقعی، رمز مدیر و کلید پیامک/درگاه در appsettings.Secrets.json (در .gitignore)
// یا متغیر محیطی با پیشوند BARGO_ (مثل BARGO_ConnectionStrings__Default).
builder.Configuration
    .AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("BARGO_");

// ---------- فرهنگ ----------
// بایند فرم با فرهنگ ثابت (نقطهٔ اعشار لاتین) تا «۲.۵ تن» روی هر سروری یکسان خوانده شود.
// نمایش فارسی (ارقام، تاریخ شمسی، تومان) کارِ Services/Fa.cs است، نه فرهنگ سرور.
var invariant = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = invariant;
CultureInfo.DefaultThreadCurrentUICulture = invariant;

// ---------- پایگاه‌داده ----------
builder.Services.AddDbContext<BargoDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default"),
        sql => sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)));

// ---------- احراز هویت ----------
// هر چهار پنل با کوکی. نقش در ClaimTypes.Role می‌نشیند و هر Area با
// [Authorize(Roles = ...)] فقط نقش خودش را راه می‌دهد.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/account/login";
        o.LogoutPath = "/account/logout";
        o.AccessDeniedPath = "/account/denied";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Cookie.Name = "bg_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

// ---------- DataProtection ----------
// کلیدها روی دیسک، وگرنه زیر IIS با هر ری‌استارت ساخته می‌شوند و توکن ضدجعلِ
// فرم‌های باز و کوکی ورود همه باطل می‌شوند (همان خطایی که در رنگیو پیدا شد).
var keysDir = Path.Combine(builder.Environment.ContentRootPath, "keys");
try
{
    Directory.CreateDirectory(keysDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
        .SetApplicationName("Bargo");
}
catch (Exception ex)
{
    Console.WriteLine($"[SECURITY] ⚠️ DataProtection روی دیسک ذخیره نشد ({ex.Message}). با هر ری‌استارت کاربران خارج می‌شوند.");
}

// ---------- سرویس‌ها ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<NotificationService>();
// پیامک: Sms.Provider=log فقط ثبت می‌کند؛ ictx واقعاً می‌فرستد (تنظیمات پنل مدیر)
builder.Services.AddScoped<ISmsSender, SmsSender>();
// درگاه زرین‌پال — فعال وقتی Gateway.Provider=zarinpal و شناسهٔ پذیرنده تنظیم شده باشد
builder.Services.AddScoped<ZarinPalService>();
builder.Services.AddScoped<DocumentStorage>();
builder.Services.AddScoped<DriverReadiness>();
// گردش کار بار ← سفر ← تحویل ← تسویه؛ تنها جایی که وضعیت‌ها عوض می‌شوند
builder.Services.AddScoped<TripFlow>();
builder.Services.AddHostedService<ExpiryAlertService>();
// کمپین پیامکی: لینک امضاشدهٔ «دیگر پیام نده» (کلیدش کنار کلیدهای DataProtection
// می‌ماند تا با انتشار پاک نشود) و موتور ارسال پس‌زمینه که صف کمپین را خالی می‌کند.
builder.Services.AddSingleton(new OptOutLinks(keysDir));
builder.Services.AddHostedService<CampaignSenderJob>();

// ---------- MVC ----------
// دروازهٔ تأیید سراسری است نه روی کنترلرها: هر Area تازه به‌طور پیش‌فرض بسته است.
// ضدجعل هم سراسری است؛ فقط API اپ (/api/track) با [IgnoreAntiforgeryToken] مستثناست.
builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add<ApprovalGateFilter>();
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    o.ModelBinderProviders.Insert(0, new ShamsiDateModelBinderProvider());
});

var app = builder.Build();

// ---------- ساخت پایگاه‌داده + داده‌های اولیه ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BargoDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await DbSeeder.SeedAsync(db, app.Configuration, app.Environment);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "اتصال یا مهاجرت پایگاه‌داده در راه‌اندازی ناموفق بود.");
        // روی سرور بهتر است همین‌جا بمیریم تا خرابیِ رشتهٔ اتصال فوراً دیده شود
        if (!app.Environment.IsDevelopment()) throw;
    }
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseStatusCodePagesWithReExecute("/Home/Status/{0}");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// API (attribute-routed) + پنل‌ها (هر نقش یک Area)
app.MapControllers();
app.MapControllerRoute(name: "areas", pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
