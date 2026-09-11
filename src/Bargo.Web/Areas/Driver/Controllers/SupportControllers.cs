using Bargo.Web.Controllers.Shared;
using Bargo.Web.Data;
using Bargo.Web.Models.Entities;
using Bargo.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bargo.Web.Areas.DriverPanel.Controllers;

// ماژول‌های مشترک چهار پنل: رفتار و ویو در Controllers/Shared و Views/Shared/Panel است؛
// اینجا فقط Area و نقش تعیین می‌شود. [AllowUnapproved] از کلاس پایه به ارث می‌رسد تا
// رانندهٔ در انتظار تأیید هم بتواند تیکت و پیام بفرستد.

[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class TicketsController(BargoDbContext db, CurrentUser me, SettingsService settings)
    : TicketsControllerBase(db, me, settings);

[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class NotificationsController(BargoDbContext db, CurrentUser me)
    : NotificationsControllerBase(db, me);

[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class MessagesController(BargoDbContext db, CurrentUser me, NotificationService notify)
    : MessagesControllerBase(db, me, notify);

[Area("Driver")]
[Authorize(Roles = Roles.Driver)]
public class ComplaintsController(BargoDbContext db, CurrentUser me)
    : ComplaintsControllerBase(db, me);
