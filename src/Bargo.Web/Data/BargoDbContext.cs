using Bargo.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bargo.Web.Data;

public class BargoDbContext(DbContextOptions<BargoDbContext> options) : DbContext(options)
{
    // ---- حساب‌ها ----
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Shipper> Shippers => Set<Shipper>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyUser> CompanyUsers => Set<CompanyUser>();
    public DbSet<SavedAddress> SavedAddresses => Set<SavedAddress>();
    public DbSet<Otp> Otps => Set<Otp>();

    // ---- ناوگان و مدارک ----
    public DbSet<VehicleType> VehicleTypes => Set<VehicleType>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Document> Documents => Set<Document>();

    // ---- بار و پیشنهاد ----
    public DbSet<Load> Loads => Set<Load>();
    public DbSet<LoadPhoto> LoadPhotos => Set<LoadPhoto>();
    public DbSet<SavedLoad> SavedLoads => Set<SavedLoad>();
    public DbSet<Offer> Offers => Set<Offer>();

    // ---- سفر ----
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<TripEvent> TripEvents => Set<TripEvent>();
    public DbSet<TripAssignment> TripAssignments => Set<TripAssignment>();
    public DbSet<TrackingPoint> TrackingPoints => Set<TrackingPoint>();
    public DbSet<Waybill> Waybills => Set<Waybill>();
    public DbSet<TripDocument> TripDocuments => Set<TripDocument>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<FavoriteDriver> FavoriteDrivers => Set<FavoriteDriver>();

    // ---- مالی ----
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PayoutRequest> PayoutRequests => Set<PayoutRequest>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<CompanyExpense> CompanyExpenses => Set<CompanyExpense>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<DiscountCode> DiscountCodes => Set<DiscountCode>();
    public DbSet<Tariff> Tariffs => Set<Tariff>();

    // ---- پشتیبانی و ارتباطات ----
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<Complaint> Complaints => Set<Complaint>();
    public DbSet<Violation> Violations => Set<Violation>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Broadcast> Broadcasts => Set<Broadcast>();
    public DbSet<SmsLog> SmsLogs => Set<SmsLog>();

    // ---- داده پایه و سامانه ----
    public DbSet<Province> Provinces => Set<Province>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<RouteLane> RouteLanes => Set<RouteLane>();
    public DbSet<Geofence> Geofences => Set<Geofence>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<ContentItem> ContentItems => Set<ContentItem>();
    public DbSet<CompanyCustomer> CompanyCustomers => Set<CompanyCustomer>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // تناژ، درصد و ابعاد — سه رقم اعشار کافی است و پیش‌فرضِ (18,2) درصدِ ۷.۵۲۵ را می‌برید
        c.Properties<decimal>().HavePrecision(18, 3);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // ─────────────────────────────────────────────────────────────────────
        //  حذف آبشاری فقط برای فرزندانِ واقعی
        //
        //  SQL Server مسیرهای آبشاریِ چندگانه را نمی‌پذیرد (Trip → Load و Trip →
        //  Offer → Load). مهم‌تر: حذفِ یک راننده نباید سفرها و تراکنش‌هایش را با خود
        //  ببرد. پس همه‌چیز Restrict است و فقط فرزندانی که بدون والد معنا ندارند
        //  (تصویر بار، رویداد سفر، پیام تیکت) آبشاری می‌شوند — پایین‌تر.
        // ─────────────────────────────────────────────────────────────────────
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            if (!fk.IsOwnership) fk.DeleteBehavior = DeleteBehavior.Restrict;

        b.Entity<LoadPhoto>().HasOne(p => p.Load).WithMany(l => l.Photos).HasForeignKey(p => p.LoadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TripEvent>().HasOne(e => e.Trip).WithMany(t => t.Events).HasForeignKey(e => e.TripId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TripDocument>().HasOne(d => d.Trip).WithMany(t => t.Documents).HasForeignKey(d => d.TripId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<TicketMessage>().HasOne(m => m.Ticket).WithMany(t => t.Messages).HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Trip>().HasOne(t => t.Waybill).WithOne(w => w.Trip).HasForeignKey<Waybill>(w => w.TripId);
        b.Entity<Company>().HasMany(c => c.Users).WithOne(u => u.Company).HasForeignKey(u => u.CompanyId);

        // ─── حساب‌ها ───
        // موبایل نام کاربری است، پس در هر جدولِ حساب یکتاست. یک شماره می‌تواند هم
        // راننده باشد و هم صاحب بار — این دو حساب مستقل‌اند و صفحهٔ ورود نقش را می‌پرسد.
        b.Entity<Admin>().HasIndex(x => x.Mobile).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.Mobile).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.TrackKey).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.NationalCode);
        b.Entity<Driver>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<Driver>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<Shipper>().HasIndex(x => x.Mobile).IsUnique();
        b.Entity<Company>().HasIndex(x => x.NationalId).IsUnique().HasFilter("[NationalId] <> ''");
        b.Entity<Company>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<CompanyUser>().HasIndex(x => x.Mobile).IsUnique();
        b.Entity<SavedAddress>().HasIndex(x => x.ShipperId);
        b.Entity<Otp>().HasIndex(x => new { x.Mobile, x.Purpose, x.CreatedAt });

        // ─── ناوگان و مدارک ───
        b.Entity<Vehicle>().HasIndex(x => x.PlateNo);
        b.Entity<Vehicle>().HasIndex(x => new { x.DriverId, x.Status });
        b.Entity<Vehicle>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<Document>().HasIndex(x => new { x.OwnerKind, x.OwnerId, x.Kind });
        b.Entity<Document>().HasIndex(x => new { x.Status, x.UploadedAt });
        b.Entity<Document>().HasIndex(x => x.ExpiresAt);

        // ─── بار ───
        b.Entity<Load>().HasIndex(x => x.Code).IsUnique().HasFilter("[Code] <> ''");
        // بازار بار: «منتشرشده، از این شهر، از این تاریخ»
        b.Entity<Load>().HasIndex(x => new { x.Status, x.OriginCityId, x.LoadingFrom });
        b.Entity<Load>().HasIndex(x => new { x.ShipperId, x.Status });
        b.Entity<Load>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<Load>().HasIndex(x => new { x.TargetCompanyId, x.Status });
        b.Entity<SavedLoad>().HasIndex(x => new { x.DriverId, x.LoadId }).IsUnique();

        b.Entity<Offer>().HasIndex(x => new { x.LoadId, x.Status });
        b.Entity<Offer>().HasIndex(x => new { x.DriverId, x.Status });
        b.Entity<Offer>().HasIndex(x => new { x.CompanyId, x.Status });
        // یک پیشنهادِ «در انتظار» برای هر حمل‌کننده روی هر بار — پیشنهاد دوم همان
        // ردیف را به‌روز می‌کند. یکتایی در پایگاه‌داده است تا دو کلیکِ هم‌زمان دو ردیف نسازد.
        b.Entity<Offer>().HasIndex(x => new { x.LoadId, x.DriverId }).IsUnique()
            .HasFilter("[Status] = 'pending' AND [DriverId] IS NOT NULL");
        b.Entity<Offer>().HasIndex(x => new { x.LoadId, x.CompanyId }).IsUnique()
            .HasFilter("[Status] = 'pending' AND [CompanyId] IS NOT NULL");

        // ─── سفر ───
        b.Entity<Trip>().HasIndex(x => x.Code).IsUnique().HasFilter("[Code] <> ''");
        b.Entity<Trip>().HasIndex(x => x.TrackToken).IsUnique();
        b.Entity<Trip>().HasIndex(x => x.LoadId);
        b.Entity<Trip>().HasIndex(x => new { x.DriverId, x.Status });
        b.Entity<Trip>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<Trip>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<TripEvent>().HasIndex(x => new { x.TripId, x.CreatedAt });
        b.Entity<TripAssignment>().HasIndex(x => x.TripId);
        b.Entity<TrackingPoint>().HasIndex(x => new { x.TripId, x.RecordedAt });
        b.Entity<TrackingPoint>().HasIndex(x => new { x.DriverId, x.RecordedAt });
        b.Entity<Waybill>().HasIndex(x => x.Number);
        b.Entity<Rating>().HasIndex(x => new { x.TripId, x.FromKind, x.FromId, x.ToKind }).IsUnique();
        b.Entity<Rating>().HasIndex(x => new { x.ToKind, x.ToId });
        b.Entity<FavoriteDriver>().HasIndex(x => new { x.ShipperId, x.DriverId }).IsUnique();

        // ─── مالی ───
        b.Entity<WalletTransaction>().HasIndex(x => new { x.OwnerKind, x.OwnerId, x.CreatedAt });
        b.Entity<WalletTransaction>().HasIndex(x => new { x.Kind, x.CreatedAt });
        b.Entity<WalletTransaction>().HasIndex(x => x.TripId);
        b.Entity<Payment>().HasIndex(x => new { x.PayerKind, x.PayerId });
        b.Entity<Payment>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<Payment>().HasIndex(x => x.Authority);
        b.Entity<PayoutRequest>().HasIndex(x => new { x.OwnerKind, x.OwnerId });
        b.Entity<PayoutRequest>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<Invoice>().HasIndex(x => x.No).IsUnique().HasFilter("[No] <> ''");
        b.Entity<Invoice>().HasIndex(x => new { x.OwnerKind, x.OwnerId });
        b.Entity<Refund>().HasIndex(x => x.Status);
        b.Entity<CompanyExpense>().HasIndex(x => new { x.CompanyId, x.SpentAt });
        b.Entity<Subscription>().HasIndex(x => new { x.OwnerKind, x.OwnerId, x.EndsAt });
        b.Entity<DiscountCode>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Tariff>().HasIndex(x => x.Key).IsUnique();

        // ─── پشتیبانی ───
        b.Entity<Ticket>().HasIndex(x => new { x.OwnerKind, x.OwnerId, x.Status });
        b.Entity<Ticket>().HasIndex(x => new { x.Status, x.Priority, x.UpdatedAt });
        b.Entity<Complaint>().HasIndex(x => x.Code).IsUnique().HasFilter("[Code] <> ''");
        b.Entity<Complaint>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<Complaint>().HasIndex(x => new { x.FromKind, x.FromId });
        b.Entity<Violation>().HasIndex(x => x.DriverId);
        b.Entity<Notification>().HasIndex(x => new { x.OwnerKind, x.OwnerId, x.ReadAt });
        b.Entity<Message>().HasIndex(x => new { x.ThreadKey, x.CreatedAt });
        b.Entity<Message>().HasIndex(x => new { x.ToKind, x.ToId, x.ReadAt });
        b.Entity<SmsLog>().HasIndex(x => x.CreatedAt);

        // ─── داده پایه ───
        b.Entity<Province>().Property(p => p.ProvinceId).ValueGeneratedNever();
        b.Entity<City>().HasIndex(x => new { x.ProvinceId, x.Name }).IsUnique();
        b.Entity<Terminal>().HasIndex(x => x.CityId);
        b.Entity<RouteLane>().HasIndex(x => new { x.OriginCityId, x.DestCityId }).IsUnique();
        b.Entity<Setting>().HasIndex(x => x.Key).IsUnique();
        b.Entity<ContentItem>().HasIndex(x => new { x.Kind, x.IsPublished, x.SortOrder });
        b.Entity<ContentItem>().HasIndex(x => x.Slug);
        b.Entity<CompanyCustomer>().HasIndex(x => x.CompanyId);
        b.Entity<Contract>().HasIndex(x => x.CompanyId);
        b.Entity<AuditLog>().HasIndex(x => new { x.Entity, x.EntityId });
        b.Entity<AuditLog>().HasIndex(x => x.CreatedAt);
    }
}
