using Bargo.Web.Models.Entities;

namespace Bargo.Web.Services;

public sealed record MenuItem(string Href, string Icon, string Label);

/// <summary>گروه منو. Title خالی یعنی یک پیوند تکیِ سطح بالا (داشبورد، پیام‌ها).</summary>
public sealed record MenuGroup(string Title, string Icon, string Tone, MenuItem[] Items)
{
    public bool IsTop => Title.Length == 0;
}

/// <summary>
/// منوی چهار پنل — هر نقش فقط منوهای خودش را می‌بیند.
///
/// چرا اینجا و نه در لایوت (برخلاف رنگیو): چهار منوی دوسطحی با بیش از ۲۰۰ ردیف،
/// لایوت را غیرقابل خواندن می‌کرد. افزودن صفحهٔ تازه یک ردیف در همین فایل است؛ هر
/// Href باید به یک اکشن واقعی در Area همان نقش برسد.
///
/// رنگ گروه‌ها از معنایشان می‌آید نه ترتیب: «مالی» در هر چهار پنل کهربایی است.
/// </summary>
public static class PanelMenu
{
    private static MenuItem I(string href, string icon, string label) => new(href, icon, label);
    private static MenuGroup Top(string href, string icon, string label) => new("", icon, "", [new MenuItem(href, icon, label)]);
    private static MenuGroup G(string title, string icon, string tone, params MenuItem[] items) => new(title, icon, tone, items);

    // رنگ‌ها (کلاس‌های t-* در app.css)
    private const string Freight = "t-blue";      // بار و پیشنهاد
    private const string Ops = "t-green";         // سفر، عملیات، تخصیص
    private const string Fleet = "t-teal";        // خودرو، راننده، مدارک
    private const string Money = "t-amber";       // مالی
    private const string Watch = "t-red";         // رهگیری، کنترل زنده، شکایت
    private const string Docs = "t-brown";        // بارنامه و اسناد
    private const string Insight = "t-violet";    // گزارش، امتیاز، محتوا
    private const string Account = "t-slate";     // حساب، پشتیبانی، تنظیمات

    public static MenuGroup[] For(string role) => role switch
    {
        Roles.Driver => Driver,
        Roles.Shipper => Shipper,
        Roles.Company => Company,
        Roles.Admin => Admin,
        _ => []
    };

    // =====================================================================
    //  ۱. پنل راننده
    // =====================================================================
    public static readonly MenuGroup[] Driver =
    [
        Top("/Driver", "bi-speedometer2", "داشبورد"),
        G("بارهای موجود", "bi-box-seam", Freight,
            I("/Driver/Loads/Nearby", "bi-geo", "بارهای نزدیک من"),
            I("/Driver/Loads", "bi-grid-3x3-gap", "همه بارها"),
            I("/Driver/Loads/Search", "bi-funnel", "جستجو و فیلتر بار"),
            I("/Driver/Loads/Suggested", "bi-magic", "بارهای پیشنهادی به من"),
            I("/Driver/Loads/Saved", "bi-bookmark-star", "بارهای ذخیره‌شده")),
        G("پیشنهادهای من", "bi-tags", Freight,
            I("/Driver/Offers/Create", "bi-plus-circle", "ثبت پیشنهاد قیمت"),
            I("/Driver/Offers?status=pending", "bi-hourglass-split", "پیشنهادهای در انتظار"),
            I("/Driver/Offers?status=accepted", "bi-check2-circle", "پیشنهادهای پذیرفته‌شده"),
            I("/Driver/Offers?status=rejected", "bi-x-circle", "پیشنهادهای ردشده")),
        G("سفرهای من", "bi-signpost-split", Ops,
            I("/Driver/Trips?tab=upcoming", "bi-calendar-event", "سفرهای آینده"),
            I("/Driver/Trips/Current", "bi-truck", "سفر در حال انجام"),
            I("/Driver/Trips?tab=done", "bi-flag", "سفرهای تکمیل‌شده"),
            I("/Driver/Trips?tab=cancelled", "bi-slash-circle", "سفرهای لغوشده"),
            I("/Driver/Trips/History", "bi-clock-history", "تاریخچه سفرها")),
        G("عملیات سفر", "bi-play-circle", Ops,
            I("/Driver/Operations?step=to_origin", "bi-sign-turn-right", "حرکت به سمت مبدا"),
            I("/Driver/Operations?step=at_origin", "bi-geo-alt", "اعلام حضور در مبدا"),
            I("/Driver/Operations?step=loaded", "bi-box-arrow-in-down", "تأیید بارگیری"),
            I("/Driver/Operations?step=in_transit", "bi-play-fill", "شروع سفر"),
            I("/Driver/Operations/Location", "bi-broadcast-pin", "اشتراک موقعیت"),
            I("/Driver/Operations?step=arrived", "bi-pin-map", "اعلام رسیدن"),
            I("/Driver/Operations?step=delivered", "bi-check2-square", "تأیید تخلیه و تحویل بار")),
        G("بارنامه و اسناد", "bi-file-earmark-text", Docs,
            I("/Driver/Waybills", "bi-files", "بارنامه‌های من"),
            I("/Driver/Waybills/Current", "bi-file-earmark-check", "مشاهده بارنامه"),
            I("/Driver/Waybills/Documents", "bi-folder2-open", "اسناد بار"),
            I("/Driver/Waybills/Receipts", "bi-receipt", "رسید تحویل")),
        G("درآمد و مالی", "bi-wallet2", Money,
            I("/Driver/Finance", "bi-wallet2", "کیف پول"),
            I("/Driver/Finance/Earnings", "bi-graph-up-arrow", "درآمدها"),
            I("/Driver/Finance/Settlements", "bi-bank", "تسویه‌حساب"),
            I("/Driver/Finance/Withdraw", "bi-cash-coin", "درخواست برداشت"),
            I("/Driver/Finance/Transactions", "bi-arrow-left-right", "تراکنش‌ها"),
            I("/Driver/Finance/Invoices", "bi-receipt-cutoff", "فاکتورها")),
        G("خودرو و مدارک", "bi-truck-front", Fleet,
            I("/Driver/Vehicle", "bi-truck-front", "مشخصات خودرو"),
            I("/Driver/Vehicle/Documents?kind=vehicle_card", "bi-credit-card-2-front", "کارت خودرو"),
            I("/Driver/Vehicle/Documents?kind=insurance", "bi-shield-check", "بیمه"),
            I("/Driver/Vehicle/Documents?kind=inspection", "bi-tools", "معاینه فنی"),
            I("/Driver/Vehicle/Documents?kind=smart_card", "bi-sim", "کارت هوشمند"),
            I("/Driver/Vehicle/Documents?kind=license", "bi-person-vcard", "گواهینامه"),
            I("/Driver/Vehicle/Alerts", "bi-alarm", "هشدار انقضای مدارک")),
        Top("/Driver/Messages", "bi-chat-dots", "پیام‌ها"),
        Top("/Driver/Notifications", "bi-bell", "اعلان‌ها"),
        G("امتیاز و نظرات", "bi-star", Insight,
            I("/Driver/Ratings", "bi-star-half", "امتیاز من"),
            I("/Driver/Ratings/Reviews", "bi-chat-quote", "نظرات صاحبان بار"),
            I("/Driver/Ratings/Performance", "bi-bar-chart", "سوابق عملکرد")),
        Top("/Driver/Profile", "bi-person-gear", "حساب کاربری"),
        G("پشتیبانی", "bi-life-preserver", Account,
            I("/Driver/Tickets/Create", "bi-plus-square", "ثبت تیکت"),
            I("/Driver/Complaints/Create", "bi-exclamation-octagon", "شکایت و گزارش مشکل"),
            I("/Driver/Tickets", "bi-inbox", "تیکت‌های من"),
            I("/Driver/Tickets/Contact", "bi-telephone", "تماس با پشتیبانی")),
    ];

    // =====================================================================
    //  ۲. پنل صاحب بار
    // =====================================================================
    public static readonly MenuGroup[] Shipper =
    [
        Top("/Shipper", "bi-speedometer2", "داشبورد"),
        G("ثبت بار جدید", "bi-plus-square", Freight,
            I("/Shipper/Loads/Create?section=cargo", "bi-box", "مشخصات بار"),
            I("/Shipper/Loads/Create?section=route", "bi-signpost-2", "مبدا و مقصد"),
            I("/Shipper/Loads/Create?section=time", "bi-calendar-check", "زمان بارگیری"),
            I("/Shipper/Loads/Create?section=vehicle", "bi-truck", "نوع خودرو"),
            I("/Shipper/Loads/Create?section=size", "bi-rulers", "وزن و ابعاد"),
            I("/Shipper/Loads/Create?section=value", "bi-gem", "ارزش تقریبی بار"),
            I("/Shipper/Loads/Create?section=media", "bi-images", "توضیحات و تصاویر"),
            I("/Shipper/Loads/Create?section=insurance", "bi-shield-plus", "بیمه بار")),
        G("بارهای من", "bi-boxes", Freight,
            I("/Shipper/Loads?status=waiting", "bi-hourglass", "در انتظار راننده"),
            I("/Shipper/Loads?status=offering", "bi-tags", "در حال دریافت پیشنهاد"),
            I("/Shipper/Loads?status=selected", "bi-person-check", "راننده انتخاب‌شده"),
            I("/Shipper/Loads?status=loading", "bi-box-arrow-in-down", "در حال بارگیری"),
            I("/Shipper/Loads?status=transit", "bi-truck", "در حال حمل"),
            I("/Shipper/Loads?status=delivered", "bi-check2-all", "تحویل‌شده"),
            I("/Shipper/Loads?status=cancelled", "bi-x-octagon", "لغوشده")),
        G("پیشنهادهای رانندگان", "bi-people", Freight,
            I("/Shipper/Offers", "bi-list-ul", "مشاهده پیشنهادها"),
            I("/Shipper/Offers/Compare", "bi-bar-chart-steps", "مقایسه قیمت‌ها"),
            I("/Shipper/Offers/Compare?view=driver", "bi-person-badge", "مشاهده مشخصات راننده"),
            I("/Shipper/Offers/Compare?view=vehicle", "bi-truck-front", "مشاهده خودرو"),
            I("/Shipper/Offers/Compare?view=rating", "bi-star", "امتیاز راننده"),
            I("/Shipper/Offers?pending=1", "bi-check2-square", "قبول/رد پیشنهاد")),
        G("رهگیری بار", "bi-geo-alt", Watch,
            I("/Shipper/Tracking", "bi-truck", "موقعیت خودرو"),
            I("/Shipper/Tracking?view=route", "bi-map", "مسیر روی نقشه"),
            I("/Shipper/Tracking?view=status", "bi-activity", "وضعیت لحظه‌ای"),
            I("/Shipper/Tracking?view=eta", "bi-stopwatch", "زمان تقریبی رسیدن"),
            I("/Shipper/Tracking/History", "bi-clock-history", "سوابق موقعیت")),
        G("سفارش‌ها و سوابق", "bi-receipt", Ops,
            I("/Shipper/Orders?tab=current", "bi-lightning", "سفارش‌های جاری"),
            I("/Shipper/Orders?tab=done", "bi-check-circle", "سفارش‌های تکمیل‌شده"),
            I("/Shipper/Orders?tab=cancelled", "bi-x-circle", "سفارش‌های لغوشده")),
        G("اسناد و بارنامه", "bi-folder2-open", Docs,
            I("/Shipper/Documents?kind=waybill", "bi-file-earmark-text", "بارنامه‌ها"),
            I("/Shipper/Documents?kind=cargo_insurance", "bi-shield-check", "بیمه‌نامه‌ها"),
            I("/Shipper/Documents?kind=loading_receipt", "bi-box-arrow-in-down", "رسید بارگیری"),
            I("/Shipper/Documents?kind=delivery_receipt", "bi-receipt", "رسید تحویل"),
            I("/Shipper/Documents/Download", "bi-download", "دانلود اسناد")),
        G("مالی", "bi-wallet2", Money,
            I("/Shipper/Finance", "bi-wallet2", "کیف پول"),
            I("/Shipper/Finance/Pay", "bi-credit-card", "پرداخت کرایه"),
            I("/Shipper/Finance/Pending", "bi-hourglass-split", "پرداخت‌های در انتظار"),
            I("/Shipper/Finance/Transactions", "bi-arrow-left-right", "تراکنش‌ها"),
            I("/Shipper/Finance/Invoices", "bi-receipt-cutoff", "فاکتورها"),
            I("/Shipper/Finance/Refunds", "bi-arrow-counterclockwise", "استرداد وجه")),
        G("رانندگان منتخب", "bi-person-hearts", Fleet,
            I("/Shipper/Drivers", "bi-people", "رانندگان قبلی"),
            I("/Shipper/Drivers/Favorites", "bi-heart", "رانندگان مورد علاقه"),
            I("/Shipper/Drivers/Rate", "bi-star", "امتیازدهی به راننده")),
        Top("/Shipper/Messages", "bi-chat-dots", "پیام‌ها"),
        Top("/Shipper/Notifications", "bi-bell", "اعلان‌ها"),
        G("حساب کاربری", "bi-person-gear", Account,
            I("/Shipper/Profile", "bi-person", "مشخصات"),
            I("/Shipper/Profile/Verification", "bi-patch-check", "احراز هویت"),
            I("/Shipper/Profile/Addresses", "bi-geo", "آدرس‌های منتخب"),
            I("/Shipper/Profile/Bank", "bi-bank", "اطلاعات مالی"),
            I("/Shipper/Profile/Settings", "bi-gear", "تنظیمات")),
        G("پشتیبانی", "bi-life-preserver", Account,
            I("/Shipper/Tickets/Create", "bi-plus-square", "ثبت تیکت"),
            I("/Shipper/Complaints/Create", "bi-exclamation-octagon", "شکایت"),
            I("/Shipper/Tickets/Create?category=cargo_issue", "bi-box2-heart", "گزارش مشکل بار"),
            I("/Shipper/Tickets", "bi-inbox", "تیکت‌های من"),
            I("/Shipper/Tickets/Contact", "bi-telephone", "تماس با پشتیبانی")),
    ];

    // =====================================================================
    //  ۳. پنل شرکت حمل‌ونقل
    // =====================================================================
    public static readonly MenuGroup[] Company =
    [
        Top("/Company", "bi-speedometer2", "داشبورد مدیریتی"),
        G("مدیریت بارها", "bi-boxes", Freight,
            I("/Company/Loads/Create", "bi-plus-square", "ثبت بار"),
            I("/Company/Loads/Incoming", "bi-inbox", "بارهای دریافتی"),
            I("/Company/Loads/Market", "bi-shop", "بازار بار"),
            I("/Company/Loads?tab=unassigned", "bi-hourglass-split", "بارهای در انتظار تخصیص"),
            I("/Company/Loads?tab=active", "bi-lightning", "بارهای فعال"),
            I("/Company/Loads?tab=done", "bi-check2-all", "بارهای تکمیل‌شده"),
            I("/Company/Loads?tab=cancelled", "bi-x-octagon", "بارهای لغوشده")),
        G("مدیریت رانندگان", "bi-person-badge", Fleet,
            I("/Company/Drivers/Add", "bi-person-plus", "افزودن راننده"),
            I("/Company/Drivers", "bi-people", "لیست رانندگان"),
            I("/Company/Drivers/Documents", "bi-folder2-open", "مدارک رانندگان"),
            I("/Company/Drivers/Activity", "bi-activity", "وضعیت فعالیت"),
            I("/Company/Drivers/Ratings", "bi-star", "امتیاز رانندگان"),
            I("/Company/Drivers/Trips", "bi-clock-history", "سوابق سفر"),
            I("/Company/Drivers/Online", "bi-broadcast", "رانندگان آنلاین")),
        G("مدیریت ناوگان", "bi-truck-front", Fleet,
            I("/Company/Fleet/Add", "bi-plus-circle", "افزودن خودرو"),
            I("/Company/Fleet", "bi-truck", "خودروهای شرکت"),
            I("/Company/Fleet/Types", "bi-diagram-2", "نوع خودرو"),
            I("/Company/Fleet/Status", "bi-toggles", "وضعیت خودرو"),
            I("/Company/Fleet/Documents", "bi-folder2-open", "مدارک"),
            I("/Company/Fleet/Documents?kind=insurance", "bi-shield-check", "بیمه"),
            I("/Company/Fleet/Documents?kind=inspection", "bi-tools", "معاینه فنی"),
            I("/Company/Fleet/Alerts", "bi-alarm", "هشدار انقضا")),
        G("تخصیص بار", "bi-diagram-3", Ops,
            I("/Company/Dispatch", "bi-person-check", "تخصیص راننده"),
            I("/Company/Dispatch?focus=vehicle", "bi-truck", "تخصیص خودرو"),
            I("/Company/Dispatch/Change", "bi-arrow-repeat", "تغییر راننده"),
            I("/Company/Dispatch/Change?focus=vehicle", "bi-arrow-left-right", "تغییر خودرو"),
            I("/Company/Dispatch/Schedule", "bi-calendar-week", "برنامه‌ریزی حمل")),
        G("سفرها", "bi-signpost-split", Ops,
            I("/Company/Trips?tab=planned", "bi-calendar-event", "سفرهای برنامه‌ریزی‌شده"),
            I("/Company/Trips?tab=current", "bi-truck", "سفرهای جاری"),
            I("/Company/Trips?tab=done", "bi-flag", "سفرهای تکمیل‌شده"),
            I("/Company/Trips?tab=cancelled", "bi-slash-circle", "سفرهای لغوشده")),
        G("کنترل و رهگیری ناوگان", "bi-broadcast", Watch,
            I("/Company/Monitoring", "bi-map", "نقشه آنلاین"),
            I("/Company/Monitoring?layer=drivers", "bi-person-bounding-box", "موقعیت رانندگان"),
            I("/Company/Monitoring?layer=vehicles", "bi-truck", "موقعیت خودروها"),
            I("/Company/Monitoring/Route", "bi-bezier2", "مسیر سفر"),
            I("/Company/Monitoring/Loads", "bi-boxes", "وضعیت بارها"),
            I("/Company/Monitoring/History", "bi-clock-history", "سوابق مسیر")),
        G("بارنامه و اسناد", "bi-file-earmark-text", Docs,
            I("/Company/Waybills/Issue", "bi-file-earmark-plus", "صدور/ثبت بارنامه"),
            I("/Company/Waybills", "bi-files", "بارنامه‌های شرکت"),
            I("/Company/Waybills/Documents", "bi-folder2-open", "اسناد بار"),
            I("/Company/Waybills/Documents?kind=cargo_insurance", "bi-shield-check", "بیمه بار"),
            I("/Company/Waybills/Documents?kind=delivery_receipt", "bi-receipt", "رسید تحویل")),
        G("مشتریان", "bi-buildings", Freight,
            I("/Company/Customers", "bi-person-lines-fill", "صاحبان بار"),
            I("/Company/Customers?kind=corporate", "bi-building", "مشتریان شرکتی"),
            I("/Company/Customers/History", "bi-clock-history", "سوابق همکاری"),
            I("/Company/Customers/Contracts", "bi-file-earmark-medical", "قراردادها")),
        G("مالی و حسابداری", "bi-calculator", Money,
            I("/Company/Finance", "bi-wallet2", "کیف پول شرکت"),
            I("/Company/Finance/Income", "bi-graph-up-arrow", "درآمد"),
            I("/Company/Finance/Expenses", "bi-graph-down-arrow", "هزینه"),
            I("/Company/Finance/Receivables", "bi-box-arrow-in-left", "مطالبات"),
            I("/Company/Finance/Payables", "bi-box-arrow-right", "بدهی‌ها"),
            I("/Company/Finance/DriverSettlements", "bi-person-check", "تسویه با رانندگان"),
            I("/Company/Finance/ShipperSettlements", "bi-building-check", "تسویه با صاحبان بار"),
            I("/Company/Finance/Transactions", "bi-arrow-left-right", "تراکنش‌ها"),
            I("/Company/Finance/Invoices", "bi-receipt-cutoff", "فاکتورها"),
            I("/Company/Finance/Report", "bi-file-bar-graph", "گزارش مالی")),
        G("گزارش‌ها", "bi-graph-up", Insight,
            I("/Company/Reports/Drivers", "bi-person-badge", "عملکرد رانندگان"),
            I("/Company/Reports/Vehicles", "bi-truck", "عملکرد خودروها"),
            I("/Company/Reports/Trips", "bi-123", "تعداد سفر"),
            I("/Company/Reports/Revenue", "bi-cash-stack", "درآمد دوره‌ای"),
            I("/Company/Reports/Cargo", "bi-boxes", "بارهای حمل‌شده"),
            I("/Company/Reports/Routes", "bi-signpost-2", "مسیرهای پرتردد")),
        G("کاربران شرکت", "bi-people", Account,
            I("/Company/Users/Add", "bi-person-plus", "تعریف کاربر"),
            I("/Company/Users", "bi-person-gear", "تعیین نقش"),
            I("/Company/Users/Permissions", "bi-shield-lock", "تعیین سطح دسترسی")),
        G("پیام‌ها و اعلان‌ها", "bi-chat-dots", Account,
            I("/Company/Messages", "bi-chat-dots", "پیام‌ها"),
            I("/Company/Notifications", "bi-bell", "اعلان‌ها")),
        G("حساب شرکت", "bi-building-gear", Account,
            I("/Company/Account", "bi-building", "مشخصات شرکت"),
            I("/Company/Account/Licenses", "bi-award", "مجوزها"),
            I("/Company/Account/Documents", "bi-folder2-open", "مدارک"),
            I("/Company/Account/Bank", "bi-bank", "اطلاعات بانکی"),
            I("/Company/Account/Settings", "bi-gear", "تنظیمات")),
        G("پشتیبانی", "bi-life-preserver", Account,
            I("/Company/Tickets/Create", "bi-plus-square", "ثبت تیکت"),
            I("/Company/Complaints/Create", "bi-exclamation-octagon", "شکایت"),
            I("/Company/Tickets", "bi-inbox", "تیکت‌ها")),
    ];

    // =====================================================================
    //  ۴. پنل مدیر سیستم
    // =====================================================================
    public static readonly MenuGroup[] Admin =
    [
        Top("/Admin", "bi-speedometer2", "داشبورد کل سامانه"),
        G("مدیریت کاربران", "bi-people", Fleet,
            I("/Admin/Users/Drivers", "bi-person-badge", "رانندگان"),
            I("/Admin/Users/Shippers", "bi-person-workspace", "صاحبان بار"),
            I("/Admin/Users/Companies", "bi-buildings", "شرکت‌های حمل‌ونقل"),
            I("/Admin/Users/Blocked", "bi-person-slash", "کاربران مسدودشده"),
            I("/Admin/Users/Verifications", "bi-patch-check", "احراز هویت‌ها")),
        G("مدیریت بارها", "bi-boxes", Freight,
            I("/Admin/Loads", "bi-list-ul", "بارهای ثبت‌شده"),
            I("/Admin/Loads?status=waiting", "bi-hourglass", "در انتظار"),
            I("/Admin/Loads?status=active", "bi-lightning", "فعال"),
            I("/Admin/Loads?status=transit", "bi-truck", "در حال حمل"),
            I("/Admin/Loads?status=completed", "bi-check2-all", "تکمیل‌شده"),
            I("/Admin/Loads?status=cancelled", "bi-x-octagon", "لغوشده"),
            I("/Admin/Loads/Reported", "bi-flag", "بارهای گزارش‌شده")),
        G("مدیریت سفرها", "bi-signpost-split", Ops,
            I("/Admin/Trips", "bi-truck", "سفرهای جاری"),
            I("/Admin/Trips?tab=done", "bi-flag", "سفرهای تکمیل‌شده"),
            I("/Admin/Trips/Problems", "bi-exclamation-triangle", "سفرهای مشکل‌دار"),
            I("/Admin/Trips/Track", "bi-geo-alt", "رهگیری سفر")),
        G("مدیریت شرکت‌های حمل‌ونقل", "bi-buildings", Fleet,
            I("/Admin/Companies/Requests", "bi-envelope-paper", "درخواست عضویت"),
            I("/Admin/Companies", "bi-building-check", "تأیید شرکت"),
            I("/Admin/Companies/Licenses", "bi-award", "مجوزها"),
            I("/Admin/Companies/Contracts", "bi-file-earmark-medical", "قراردادها"),
            I("/Admin/Companies/Activity", "bi-activity", "وضعیت فعالیت")),
        G("مدیریت رانندگان", "bi-person-badge", Fleet,
            I("/Admin/Drivers/Pending", "bi-person-check", "تأیید راننده"),
            I("/Admin/Drivers/Documents", "bi-folder2-open", "مدارک"),
            I("/Admin/Drivers/Vehicles", "bi-truck", "خودروها"),
            I("/Admin/Drivers", "bi-clock-history", "سوابق"),
            I("/Admin/Drivers/Ratings", "bi-star", "امتیاز"),
            I("/Admin/Drivers/Violations", "bi-exclamation-octagon", "تخلفات"),
            I("/Admin/Drivers/Suspended", "bi-pause-circle", "تعلیق حساب")),
        G("مدیریت ناوگان و خودروها", "bi-truck-front", Fleet,
            I("/Admin/Fleet", "bi-truck", "همه خودروها"),
            I("/Admin/Fleet?verify=pending", "bi-hourglass-split", "خودروهای در انتظار تأیید"),
            I("/Admin/Fleet/Types", "bi-diagram-2", "انواع خودرو")),
        G("احراز هویت و مدارک", "bi-patch-check", Fleet,
            I("/Admin/Documents", "bi-hourglass-split", "مدارک در انتظار بررسی"),
            I("/Admin/Documents?status=approved", "bi-check2-circle", "تأییدشده"),
            I("/Admin/Documents?status=rejected", "bi-x-circle", "ردشده"),
            I("/Admin/Documents/Expired", "bi-calendar-x", "مدارک منقضی"),
            I("/Admin/Documents/Alerts", "bi-alarm", "هشدارها")),
        G("بارنامه و اسناد", "bi-file-earmark-text", Docs,
            I("/Admin/Waybills", "bi-files", "بارنامه‌ها"),
            I("/Admin/Waybills/Documents", "bi-folder2-open", "اسناد سفرها")),
        G("کنترل زنده", "bi-broadcast", Watch,
            I("/Admin/Live", "bi-map", "نقشه کشور"),
            I("/Admin/Live/Trips", "bi-signpost-split", "سفرهای فعال"),
            I("/Admin/Live/Vehicles", "bi-truck", "خودروهای فعال"),
            I("/Admin/Live/Loads", "bi-boxes", "بارهای در حال حمل"),
            I("/Admin/Live/Alerts", "bi-bell", "هشدارهای سیستمی")),
        G("مالی", "bi-cash-stack", Money,
            I("/Admin/Finance", "bi-arrow-left-right", "کل تراکنش‌ها"),
            I("/Admin/Finance/Commission", "bi-percent", "کمیسیون بارگو"),
            I("/Admin/Finance/Wallets", "bi-wallet2", "کیف پول کاربران"),
            I("/Admin/Finance/Payouts?owner=driver", "bi-person-check", "تسویه رانندگان"),
            I("/Admin/Finance/Payouts?owner=company", "bi-building-check", "تسویه شرکت‌ها"),
            I("/Admin/Finance/Failed", "bi-x-octagon", "پرداخت‌های ناموفق"),
            I("/Admin/Finance/Refunds", "bi-arrow-counterclockwise", "استردادها"),
            I("/Admin/Finance/Revenue", "bi-graph-up-arrow", "گزارش درآمد")),
        G("تعرفه و کمیسیون", "bi-percent", Money,
            I("/Admin/Pricing", "bi-percent", "درصد کمیسیون"),
            I("/Admin/Pricing/Tariffs", "bi-tags", "تعرفه خدمات"),
            I("/Admin/Pricing/Membership", "bi-person-vcard", "هزینه عضویت"),
            I("/Admin/Pricing/Plans", "bi-stars", "پلن‌های اشتراک"),
            I("/Admin/Pricing/Discounts", "bi-ticket-perforated", "کد تخفیف")),
        G("شکایات و تخلفات", "bi-exclamation-diamond", Watch,
            I("/Admin/Complaints?from=shipper", "bi-person-workspace", "شکایات صاحبان بار"),
            I("/Admin/Complaints?from=driver", "bi-person-badge", "شکایات رانندگان"),
            I("/Admin/Complaints?kind=financial", "bi-cash-coin", "اختلافات مالی"),
            I("/Admin/Complaints/Violations", "bi-exclamation-octagon", "تخلفات"),
            I("/Admin/Complaints?status=reviewing", "bi-hammer", "رسیدگی و تعیین نتیجه")),
        G("پشتیبانی", "bi-life-preserver", Account,
            I("/Admin/Support", "bi-inbox", "تیکت‌ها"),
            I("/Admin/Support/Operators", "bi-headset", "اپراتورها"),
            I("/Admin/Support?sort=priority", "bi-sort-down", "اولویت‌بندی"),
            I("/Admin/Support/History", "bi-clock-history", "سوابق پاسخ‌ها")),
        G("پیام‌ها و اعلان‌ها", "bi-megaphone", Insight,
            I("/Admin/Broadcast", "bi-megaphone", "ارسال اعلان عمومی"),
            I("/Admin/Broadcast?audience=drivers", "bi-person-badge", "پیام به رانندگان"),
            I("/Admin/Broadcast?audience=shippers", "bi-person-workspace", "پیام به صاحبان بار"),
            I("/Admin/Broadcast?audience=companies", "bi-buildings", "پیام به شرکت‌ها"),
            I("/Admin/Broadcast/Sms", "bi-phone", "پیامک")),
        G("کمپین پیامکی", "bi-send", Insight,
            I("/Admin/Marketing/Dashboard", "bi-speedometer", "داشبورد پیامک"),
            I("/Admin/Marketing/Sms", "bi-send-plus", "کمپین جدید"),
            I("/Admin/Marketing", "bi-clock-history", "کمپین‌ها"),
            I("/Admin/Marketing/Contacts", "bi-person-lines-fill", "بانک مخاطبان"),
            I("/Admin/Marketing/Categories", "bi-tags", "گروه‌های مخاطبان"),
            I("/Admin/Marketing/Import", "bi-file-earmark-arrow-up", "ورود از فایل")),
        G("گزارش‌ها و آمار", "bi-graph-up", Insight,
            I("/Admin/Reports/Finance", "bi-cash-stack", "مالی"),
            I("/Admin/Reports/Users", "bi-people", "کاربران"),
            I("/Admin/Reports/Loads", "bi-boxes", "بارها"),
            I("/Admin/Reports/Trips", "bi-signpost-split", "سفرها"),
            I("/Admin/Reports/Drivers", "bi-person-badge", "رانندگان"),
            I("/Admin/Reports/Companies", "bi-buildings", "شرکت‌ها"),
            I("/Admin/Reports/Regions", "bi-map", "استان و شهر"),
            I("/Admin/Reports/Routes", "bi-signpost-2", "مسیرهای پرتردد")),
        G("مدیریت محتوا", "bi-layout-text-window", Insight,
            I("/Admin/Content?kind=banner", "bi-image", "بنرها"),
            I("/Admin/Content?kind=news", "bi-newspaper", "اخبار"),
            I("/Admin/Content?kind=faq", "bi-question-circle", "سوالات متداول"),
            I("/Admin/Content?kind=rule", "bi-journal-text", "قوانین"),
            I("/Admin/Content?kind=page", "bi-file-richtext", "صفحات ثابت")),
        G("مدیریت مناطق", "bi-map", Account,
            I("/Admin/Regions", "bi-map", "استان"),
            I("/Admin/Regions/Cities", "bi-buildings", "شهر"),
            I("/Admin/Regions/Terminals", "bi-sign-stop", "پایانه"),
            I("/Admin/Regions/Lanes", "bi-signpost-2", "مبادی و مقاصد"),
            I("/Admin/Regions/Geofences", "bi-bullseye", "محدوده‌های جغرافیایی")),
        G("تنظیمات سیستم", "bi-sliders", Account,
            I("/Admin/Settings/Roles", "bi-shield-lock", "نقش‌ها و دسترسی‌ها"),
            I("/Admin/Settings/Sms", "bi-chat-square-text", "تنظیمات پیامک"),
            I("/Admin/Settings/Gateway", "bi-credit-card", "درگاه پرداخت"),
            I("/Admin/Settings/Map", "bi-geo", "نقشه و GPS"),
            I("/Admin/Settings/Waybill", "bi-file-earmark-text", "تنظیمات بارنامه"),
            I("/Admin/Settings/Finance", "bi-cash", "تنظیمات مالی"),
            I("/Admin/Settings/Logs", "bi-journal-code", "لاگ سیستم"),
            I("/Admin/Settings/Version", "bi-git", "نسخه و به‌روزرسانی")),
    ];

    // ------------------------------------------------------------------
    //  تشخیص ردیف فعال
    // ------------------------------------------------------------------

    /// <summary>
    /// دقیق‌ترین ردیفِ منطبق با نشانی فعلی. «/Driver/Offers?status=pending» فقط وقتی
    /// فعال است که کوئری هم بخواند؛ صفحهٔ جزئیات (/Driver/Trips/Detail/5) ردیفِ
    /// کنترلرِ والدش (/Driver/Trips) را فعال می‌کند.
    /// </summary>
    public static string? ActiveHref(IEnumerable<MenuGroup> groups, string path, IQueryCollection query)
    {
        var p = Norm(path);
        string? best = null;
        var bestScore = -1;

        foreach (var item in groups.SelectMany(g => g.Items))
        {
            var parts = item.Href.Split('?', 2);
            var hp = Norm(parts[0]);
            int score;
            if (hp == p) score = 1000 + hp.Length;
            else if (hp.Count(c => c == '/') >= 2 && p.StartsWith(hp + "/")) score = hp.Length;
            else continue;

            if (parts.Length == 2)
            {
                var pairs = parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToList();
                if (!pairs.All(kv => string.Equals(query[kv[0]].ToString(), kv[1], StringComparison.OrdinalIgnoreCase)))
                    continue;
                score += 500 + pairs.Count;
            }
            if (score > bestScore) { bestScore = score; best = item.Href; }
        }
        return best;
    }

    /// <summary>گروهی که باید باز باشد: گروهِ ردیفِ فعال، وگرنه گروهی که مسیرش با صفحه یکی است.</summary>
    public static MenuGroup? OpenGroup(IEnumerable<MenuGroup> groups, string? activeHref, string path)
    {
        var list = groups.ToList();
        if (activeHref is not null) return list.FirstOrDefault(g => g.Items.Any(i => i.Href == activeHref));
        var p = Norm(path);
        return list.FirstOrDefault(g => !g.IsTop && g.Items.Any(i => p.StartsWith(Norm(i.Href.Split('?')[0]))));
    }

    private static string Norm(string path)
    {
        var s = path.ToLowerInvariant().TrimEnd('/');
        if (s.EndsWith("/index")) s = s[..^6];
        var seg = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length == 2 && seg[1] == "dashboard") s = "/" + seg[0];
        return s.Length == 0 ? "/" : s;
    }
}
