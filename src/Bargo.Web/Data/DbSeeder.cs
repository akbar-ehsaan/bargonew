using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Data;

public static class DbSeeder
{
    // مختصات مرکز استان‌ها — برای بار نمونه و نقشه. شهرهای دیگر بی‌مختصات‌اند تا
    // مدیر از «مدیریت مناطق» تکمیل کند؛ نقطهٔ ساختگی در نقشه از نبودنش بدتر است.
    private static readonly Dictionary<string, (double Lat, double Lng)> Capitals = new()
    {
        ["تهران"] = (35.6892, 51.3890), ["اصفهان"] = (32.6546, 51.6680), ["مشهد"] = (36.2605, 59.6168),
        ["تبریز"] = (38.0800, 46.2919), ["شیراز"] = (29.5918, 52.5837), ["اهواز"] = (31.3183, 48.6706),
        ["کرج"] = (35.8400, 50.9391), ["قم"] = (34.6399, 50.8759), ["کرمانشاه"] = (34.3142, 47.0650),
        ["ارومیه"] = (37.5527, 45.0761), ["رشت"] = (37.2808, 49.5832), ["زاهدان"] = (29.4963, 60.8629),
        ["کرمان"] = (30.2839, 57.0834), ["همدان"] = (34.7983, 48.5148), ["یزد"] = (31.8974, 54.3569),
        ["اراک"] = (34.0954, 49.7013), ["اردبیل"] = (38.2498, 48.2933), ["بندرعباس"] = (27.1832, 56.2666),
        ["زنجان"] = (36.6736, 48.4787), ["سنندج"] = (35.3219, 46.9862), ["قزوین"] = (36.2688, 50.0041),
        ["ساری"] = (36.5633, 53.0601), ["گرگان"] = (36.8427, 54.4439), ["خرم‌آباد"] = (33.4878, 48.3558),
        ["بوشهر"] = (28.9234, 50.8203), ["بیرجند"] = (32.8649, 59.2262), ["بجنورد"] = (37.4747, 57.3290),
        ["ایلام"] = (33.6374, 46.4227), ["شهرکرد"] = (32.3256, 50.8644), ["یاسوج"] = (30.6684, 51.5870),
        ["سمنان"] = (35.5769, 53.3953),
    };

    public static async Task SeedAsync(BargoDbContext db, IConfiguration cfg, IWebHostEnvironment env)
    {
        await db.Database.MigrateAsync();

        await SeedGeoAsync(db);
        await SeedVehicleTypesAsync(db);
        await SeedSettingsAsync(db);
        await SeedAdminAsync(db, cfg);

        // دادهٔ نمایشی فقط با Seed:Demo=true — در appsettings.Development.json روشن است
        if (cfg.GetValue("Seed:Demo", false) && !await db.Drivers.AnyAsync())
            await DemoSeed.RunAsync(db);
    }

    private static async Task SeedGeoAsync(BargoDbContext db)
    {
        // افزایشی، نه «اگر خالی بود» — شهر تازه در IranGeo به پایگاه‌دادهٔ موجود هم می‌رسد
        var provinces = IranGeo.Provinces;
        var existingProv = await db.Provinces.Select(p => p.ProvinceId).ToListAsync();
        for (var i = 0; i < provinces.Count; i++)
            if (!existingProv.Contains(i + 1))
                db.Provinces.Add(new Province { ProvinceId = i + 1, Name = provinces[i] });
        await db.SaveChangesAsync();

        var existingCities = (await db.Cities.Select(c => new { c.ProvinceId, c.Name }).ToListAsync())
            .Select(c => (c.ProvinceId, c.Name)).ToHashSet();
        for (var i = 0; i < provinces.Count; i++)
        {
            foreach (var name in IranGeo.Cities(provinces[i]))
            {
                if (existingCities.Contains((i + 1, name))) continue;
                var ll = Capitals.TryGetValue(name, out var p) ? p : ((double, double)?)null;
                db.Cities.Add(new City { ProvinceId = i + 1, Name = name, Lat = ll?.Item1, Lng = ll?.Item2 });
            }
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedVehicleTypesAsync(BargoDbContext db)
    {
        if (await db.VehicleTypes.AnyAsync()) return;
        (string Name, string Body, decimal Ton)[] types =
        [
            ("وانت", "کفی", 1.5m), ("نیسان", "کفی", 2.5m), ("کامیونت", "مسقف", 5m),
            ("کامیون تک", "کفی", 10m), ("کامیون تک", "یخچالی", 10m), ("کامیون جفت", "بغل‌باز", 18m),
            ("تریلی", "کفی", 24m), ("تریلی", "چادری", 24m), ("تریلی", "یخچالی", 22m),
            ("تریلی", "کمپرسی", 25m), ("تریلی", "تانکر", 30m), ("تریلی", "کانتینری", 28m),
        ];
        var order = 0;
        foreach (var t in types)
            db.VehicleTypes.Add(new VehicleType { Name = $"{t.Name} {t.Body}", BodyKind = t.Body, CapacityTon = t.Ton, SortOrder = ++order });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSettingsAsync(BargoDbContext db)
    {
        var have = await db.Settings.Select(s => s.Key).ToListAsync();
        foreach (var (key, def) in SettingsService.Defaults)
            if (!have.Contains(key)) db.Settings.Add(new Setting { Key = key, Value = def.Default });

        if (!await db.Tariffs.AnyAsync())
        {
            db.Tariffs.AddRange(
                new Tariff { Key = "cargo_insurance", Title = "بیمه بار (درصد از ارزش)", Percent = 0.15m },
                new Tariff { Key = "company_membership", Title = "هزینه عضویت سالانهٔ شرکت", Amount = 50_000_000 },
                new Tariff { Key = "urgent_listing", Title = "نمایش ویژهٔ بار فوری", Amount = 1_000_000 });
        }
        if (!await db.SubscriptionPlans.AnyAsync())
        {
            db.SubscriptionPlans.AddRange(
                new SubscriptionPlan { Title = "شرکت — پایه", Audience = "company", DurationDays = 30, Price = 15_000_000, MaxDrivers = 10, MaxVehicles = 10, SortOrder = 1 },
                new SubscriptionPlan { Title = "شرکت — حرفه‌ای", Audience = "company", DurationDays = 30, Price = 40_000_000, MaxDrivers = 50, MaxVehicles = 50, SortOrder = 2 },
                new SubscriptionPlan { Title = "شرکت — سالانه", Audience = "company", DurationDays = 365, Price = 400_000_000, SortOrder = 3 });
        }
        if (!await db.ContentItems.AnyAsync())
        {
            db.ContentItems.AddRange(
                new ContentItem { Kind = ContentKind.Rule, Slug = "terms", Title = "قوانین و مقررات بارگو", Body = "متن قوانین و مقررات را از «مدیریت محتوا ← قوانین» تکمیل کنید." },
                new ContentItem { Kind = ContentKind.Faq, Title = "کد تحویل چیست؟", Body = "پس از تخلیهٔ بار، کدی برای گیرنده پیامک می‌شود. راننده فقط با وارد کردن همین کد می‌تواند تحویل را ثبت کند.", SortOrder = 1 },
                new ContentItem { Kind = ContentKind.Faq, Title = "کرایه چه زمانی به کیف پول راننده می‌رسد؟", Body = "کرایه پس از پرداخت توسط صاحب بار نزد بارگو امانت می‌ماند و با ثبت تحویل، پس از کسر کمیسیون به کیف پول حمل‌کننده واریز می‌شود.", SortOrder = 2 });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedAdminAsync(BargoDbContext db, IConfiguration cfg)
    {
        var mobile = Fa.NormMobile(cfg["Seed:AdminMobile"]);
        if (mobile.Length == 0 || await db.Admins.AnyAsync()) return;

        var pass = cfg["Seed:AdminPass"];
        if (string.IsNullOrWhiteSpace(pass))
        {
            pass = System.Security.Cryptography.RandomNumberGenerator.GetString(
                "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789@#%", 16);
            Console.WriteLine("=========================================================");
            Console.WriteLine($"رمز مدیر بارگو تصادفی ساخته شد: {pass}");
            Console.WriteLine("برای رمز دلخواه، Seed:AdminPass را در appsettings.Secrets.json بگذارید.");
            Console.WriteLine("=========================================================");
        }

        db.Admins.Add(new Admin
        {
            Name = "مدیر سامانه",
            Mobile = mobile,
            PassHash = PasswordHasher.Hash(pass),
            IsSuper = true,
            Permissions = AdminPermission.All
        });
        await db.SaveChangesAsync();
    }
}
