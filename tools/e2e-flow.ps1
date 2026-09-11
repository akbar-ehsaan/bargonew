# ---------------------------------------------------------------------------
#  آزمون سرتاسری گردش کار بارگو — از راه همان فرم‌هایی که کاربر پر می‌کند
#
#  صاحب بار بار ثبت می‌کند ← راننده پیشنهاد می‌دهد ← صاحب بار می‌پذیرد (سفر ساخته
#  می‌شود) ← کرایه از کیف پول ← حرکت، حضور، بارگیری ← شروع بدون بارنامه رد می‌شود
#  ← ثبت بارنامه ← شروع، رسیدن، تخلیه (کد تحویل پیامک می‌شود) ← کد نادرست رد می‌شود
#  ← کد درست ← تحویل و تسویهٔ خودکار ← امتیاز. هر گام با خواندن پایگاه‌داده سنجیده می‌شود.
#
#  ⚠️ یک بار و یک سفر واقعی به دادهٔ نمایشی اضافه می‌کند؛ فقط روی پایگاه‌دادهٔ توسعه.
#     powershell -ExecutionPolicy Bypass -File tools\e2e-flow.ps1
# ---------------------------------------------------------------------------
param(
    [string]$Base = "http://localhost:5810",
    [string]$SqlServer = $(if ($env:BARGO_SQL) { $env:BARGO_SQL } else { "." })
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root "src\Bargo.Web"
Add-Type -AssemblyName System.Net.Http

$stamp = Get-Date -Format "HHmmss"
$log = Join-Path $env:TEMP "bargo-e2e-$stamp.txt"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$proc = Start-Process -FilePath "dotnet" -ArgumentList @("bin\Debug\net10.0\Bargo.Web.dll", "--urls", $Base) `
    -WorkingDirectory $web -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
$up = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 2
    if ((Test-Path $log) -and ((Get-Content $log -Raw) -match "Now listening")) { $up = $true; break }
    if ($proc.HasExited) { break }
}
if (-not $up) { Write-Host "APP DID NOT START"; exit 1 }

function Q([string]$sql) {
    $r = sqlcmd -S $SqlServer -E -C -d BargoDb -h -1 -W -Q "SET NOCOUNT ON; $sql" | Where-Object { $_ -match '\S' } | Select-Object -First 1
    if ($r) { $r.Trim() } else { "" }
}

$script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "  PASS  $name  $detail" } else { $script:fail++; Write-Host "  FAIL  $name  $detail" }
}

function New-Client {
    $h = New-Object System.Net.Http.HttpClientHandler
    $h.AllowAutoRedirect = $false
    $h.CookieContainer = New-Object System.Net.CookieContainer
    $c = New-Object System.Net.Http.HttpClient($h)
    $c.Timeout = [TimeSpan]::FromSeconds(90)
    return $c
}

function Token($client, [string]$page) {
    $body = $client.GetAsync("$Base$page").Result.Content.ReadAsStringAsync().Result
    return [regex]::Match($body, '__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
}

function Post($client, [string]$url, [hashtable]$fields, [string]$tokenPage) {
    $form = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    foreach ($k in $fields.Keys) { $form[$k] = [string]$fields[$k] }
    $form["__RequestVerificationToken"] = Token $client $tokenPage
    $resp = $client.PostAsync("$Base$url", (New-Object System.Net.Http.FormUrlEncodedContent($form))).Result
    return $resp
}

function Login([string]$role, [string]$mobile, [string]$pass) {
    # صفحهٔ ورود فقط موبایل و گذرواژه است؛ نقش از روی شماره در سرور پیدا می‌شود
    $c = New-Client
    $body = $c.GetAsync("$Base/account/login").Result.Content.ReadAsStringAsync().Result
    $token = [regex]::Match($body, '__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $form = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $form["Mobile"] = $mobile; $form["Password"] = $pass; $form["__RequestVerificationToken"] = $token
    $r = $c.PostAsync("$Base/account/login", (New-Object System.Net.Http.FormUrlEncodedContent($form))).Result
    return @($c, [string]$r.Headers.Location)
}

try {
    Write-Host "== ورود"
    $sh = Login "shipper" "09121111111" "1234"; Check "shipper login" ($sh[1] -eq "/Shipper") $sh[1]
    $dr = Login "driver" "09123333333" "1234"; Check "driver login" ($dr[1] -eq "/Driver") $dr[1]
    $shipper = $sh[0]; $driver = $dr[0]

    $tehran = Q "SELECT CityId FROM Cities WHERE Name=N'تهران'"
    $shiraz = Q "SELECT CityId FROM Cities WHERE Name=N'شیراز'"
    $vtype = Q "SELECT TOP 1 v.VehicleTypeId FROM Vehicles v JOIN Drivers d ON d.DriverId=v.DriverId WHERE d.Mobile='09123333333'"
    $vehicle = Q "SELECT TOP 1 v.VehicleId FROM Vehicles v JOIN Drivers d ON d.DriverId=v.DriverId WHERE d.Mobile='09123333333'"
    $driverId = Q "SELECT DriverId FROM Drivers WHERE Mobile='09123333333'"
    $pc = New-Object System.Globalization.PersianCalendar
    $d = (Get-Date).AddDays(2)
    $jdate = "{0:0000}/{1:00}/{2:00}" -f $pc.GetYear($d), $pc.GetMonth($d), $pc.GetDayOfMonth($d)
    $title = "E2E-" + $stamp

    Write-Host "== ۱. ثبت بار (صاحب بار)"
    $r = Post $shipper "/Shipper/Loads/Create" @{
        Title = $title; CargoType = "مواد غذایی"; Packaging = "کارتن"
        OriginCityId = $tehran; OriginAddress = "تهران، انبار آزمون"; DestCityId = $shiraz; DestAddress = "شیراز، انبار مقصد"
        LoadingDate = $jdate; LoadingHour = "9"; VehicleTypeId = $vtype; WeightTon = "20"
        PriceMode = "negotiable"; PriceToman = "40,000,000"; Description = "آزمون سرتاسری"
        ReceiverName = "گیرندهٔ آزمون"; ReceiverMobile = "09131234567"; intent = "publish"
    } "/Shipper/Loads/Create"
    $loadId = Q "SELECT TOP 1 LoadId FROM Loads WHERE Title=N'$title'"
    Check "load created & published" (($loadId -ne "") -and ((Q "SELECT Status FROM Loads WHERE LoadId=$loadId") -eq "open")) "http=$([int]$r.StatusCode) -> $($r.Headers.Location) load=$loadId"

    Write-Host "== ۲. پیشنهاد (راننده)"
    $r = Post $driver "/Driver/Loads/Offer" @{ id = $loadId; amountToman = "38,000,000"; etaHours = "5"; note = "آزمون"; vehicleId = $vehicle } "/Driver/Loads/Detail/$loadId"
    $offerId = Q "SELECT TOP 1 OfferId FROM Offers WHERE LoadId=$loadId AND DriverId=$driverId AND Status='pending'"
    Check "offer placed, load offering" (($offerId -ne "") -and ((Q "SELECT Status FROM Loads WHERE LoadId=$loadId") -eq "offering")) "offer=$offerId amount=$(Q "SELECT Amount FROM Offers WHERE OfferId=$offerId")"

    Write-Host "== ۳. پذیرش پیشنهاد → ساخت سفر (صاحب بار)"
    $r = Post $shipper "/Shipper/Offers/Accept" @{ id = $offerId } "/Shipper/Offers/Compare/$loadId"
    $tripId = Q "SELECT TOP 1 TripId FROM Trips WHERE LoadId=$loadId"
    Check "trip created from offer" (($tripId -ne "") -and ((Q "SELECT Status FROM Trips WHERE TripId=$tripId") -eq "accepted")) "trip=$tripId load=$(Q "SELECT Status FROM Loads WHERE LoadId=$loadId")"
    Check "commission locked on trip" ((Q "SELECT Commission FROM Trips WHERE TripId=$tripId") -eq "30400000") "fare=$(Q "SELECT Fare FROM Trips WHERE TripId=$tripId") commission=$(Q "SELECT Commission FROM Trips WHERE TripId=$tripId")"

    Write-Host "== ۴. پرداخت کرایه از کیف پول (صاحب بار)"
    $balBefore = [long](Q "SELECT WalletBalance FROM Shippers WHERE Mobile='09121111111'")
    $r = Post $shipper "/Shipper/Finance/Pay" @{ tripId = $tripId; returnUrl = "/Shipper" } "/Shipper/Finance/Pay"
    $balAfter = [long](Q "SELECT WalletBalance FROM Shippers WHERE Mobile='09121111111'")
    Check "fare paid & escrowed" (((Q "SELECT IsPaid FROM Trips WHERE TripId=$tripId") -eq "1") -and ($balBefore - $balAfter -eq 380000000)) "wallet $balBefore -> $balAfter"

    # نه «Move»: در PowerShell نام مستعارِ Move-Item است و بر تابع مقدم می‌شود
    function TripStep([string]$to) { Post $driver "/Driver/Trips/Move" @{ id = $tripId; to = $to; lat = "35.70"; lng = "51.40" } "/Driver/Trips/Detail/$tripId" | Out-Null; return (Q "SELECT Status FROM Trips WHERE TripId=$tripId") }

    Write-Host "== ۵. مراحل سفر (راننده)"
    foreach ($s in "to_origin", "at_origin", "loaded") { $st = TripStep $s; Check "move -> $s" ($st -eq $s) "status=$st" }
    $st = TripStep "in_transit"; Check "start trip WITHOUT waybill is refused" ($st -eq "loaded") "status=$st"
    Post $driver "/Driver/Trips/Waybill" @{ id = $tripId; number = "BG-E2E-$stamp" } "/Driver/Trips/Detail/$tripId" | Out-Null
    Check "waybill registered" ((Q "SELECT COUNT(*) FROM Waybills WHERE TripId=$tripId") -eq "1")
    foreach ($s in "in_transit", "arrived", "unloaded") { $st = TripStep $s; Check "move -> $s" ($st -eq $s) "status=$st" }

    Write-Host "== ۶. تحویل با کد گیرنده"
    $sms = Q "SELECT TOP 1 Text FROM SmsLogs WHERE Purpose='delivery' AND Mobile='09131234567' ORDER BY SmsLogId DESC"
    $code = [regex]::Match($sms, ':\s*(\d{5})').Groups[1].Value
    Check "delivery OTP sent to receiver" ($code.Length -eq 5) "code=$code"
    Post $driver "/Driver/Trips/Deliver" @{ id = $tripId; code = "00000" } "/Driver/Trips/Detail/$tripId" | Out-Null
    Check "wrong code refused" (((Q "SELECT Status FROM Trips WHERE TripId=$tripId") -eq "unloaded") -and ((Q "SELECT DeliveryOtpAttempts FROM Trips WHERE TripId=$tripId") -eq "1"))
    Post $driver "/Driver/Trips/Deliver" @{ id = $tripId; code = $code; lat = "29.59"; lng = "52.58" } "/Driver/Trips/Detail/$tripId" | Out-Null
    $st = Q "SELECT Status FROM Trips WHERE TripId=$tripId"
    Check "delivered with code and auto-settled" ($st -eq "settled") "status=$st load=$(Q "SELECT Status FROM Loads WHERE LoadId=$loadId")"

    Write-Host "== ۷. تسویه"
    Check "driver credited carrier share" ((Q "SELECT SUM(Amount) FROM WalletTransactions WHERE TripId=$tripId AND OwnerKind='driver' AND Kind='fare_income'") -eq "349600000")
    Check "platform credited commission" ((Q "SELECT SUM(Amount) FROM WalletTransactions WHERE TripId=$tripId AND OwnerKind='platform' AND Kind='commission'") -eq "30400000")
    Check "two invoices issued" ((Q "SELECT COUNT(*) FROM Invoices WHERE TripId=$tripId") -eq "2")
    Check "event trail recorded" ([int](Q "SELECT COUNT(*) FROM TripEvents WHERE TripId=$tripId") -ge 10) "events=$(Q "SELECT COUNT(*) FROM TripEvents WHERE TripId=$tripId")"

    Write-Host "== ۸. امتیاز (صاحب بار)"
    Post $shipper "/Shipper/Drivers/Rate" @{ tripId = $tripId; score = "5"; comment = "آزمون سرتاسری" } "/Shipper/Drivers/Rate" | Out-Null
    Check "rating saved" ((Q "SELECT COUNT(*) FROM Ratings WHERE TripId=$tripId") -eq "1")

    Write-Host "== ۹. صفحه‌ها پس از چرخه"
    foreach ($p in @(@($shipper, "/Shipper/Orders/Detail/$tripId"), @($driver, "/Driver/Trips/Detail/$tripId"), @($driver, "/Driver/Finance/Earnings"))) {
        $code = [int]$p[0].GetAsync("$Base$($p[1])").Result.StatusCode
        Check "GET $($p[1])" ($code -eq 200) "http=$code"
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}

Write-Host "FAILURES=$script:fail"
if ($script:fail -gt 0) {
    Write-Host "--- app errors"
    Get-Content $log -Encoding UTF8 | Where-Object { $_ -match "Exception:|fail:" } | Select-Object -First 20
    exit 2
}
