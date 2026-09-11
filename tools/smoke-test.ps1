# ---------------------------------------------------------------------------
#  آزمون دود پنل‌ها — همهٔ ردیف‌های منو + چند صفحهٔ جزئیات، برای هر چهار نقش
#
#  برنامه را از خروجی build اجرا می‌کند، با حساب‌های نمایشی وارد می‌شود و هر نشانی
#  را درخواست می‌کند. هر پاسخی جز ۲۰۰ (و ری‌دایرکتِ مورد انتظار) گزارش می‌شود؛ برای
#  خطای ۵۰۰ عنوان استثنا از صفحهٔ خطای Development خوانده می‌شود.
#
#  اجرا (پس از dotnet build، روی پایگاه‌داده‌ای که دادهٔ نمایشی دارد):
#     powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1
# ---------------------------------------------------------------------------
param(
    [string]$Base = "http://localhost:5810",
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$web = Join-Path $root "src\Bargo.Web"
Add-Type -AssemblyName System.Net.Http

$proc = $null
if (-not $NoStart) {
    $stamp = Get-Date -Format "HHmmss"
    $log = Join-Path $env:TEMP "bargo-smoke-$stamp.txt"
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $proc = Start-Process -FilePath "dotnet" -ArgumentList @("bin\Debug\net10.0\Bargo.Web.dll", "--urls", $Base) `
        -WorkingDirectory $web -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
    $up = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        if ((Test-Path $log) -and ((Get-Content $log -Raw) -match "Now listening")) { $up = $true; break }
        if ($proc.HasExited) { break }
    }
    if (-not $up) { Write-Host "برنامه بالا نیامد:"; if (Test-Path $log) { Get-Content $log -Tail 40 }; exit 1 }
}

function Q([string]$sql) {
    $r = sqlcmd -S . -E -d BargoDb -h -1 -W -Q "SET NOCOUNT ON; $sql" | Where-Object { $_ -match '\S' } | Select-Object -First 1
    if ($r) { $r.Trim() } else { "0" }
}

$ids = @{
    TripLive    = Q "SELECT TOP 1 TripId FROM Trips WHERE Status='in_transit'"
    TripCompany = Q "SELECT TOP 1 TripId FROM Trips WHERE CompanyId IS NOT NULL"
    TripDone    = Q "SELECT TOP 1 TripId FROM Trips WHERE Status='settled'"
    LoadOffer   = Q "SELECT TOP 1 LoadId FROM Loads WHERE Status='offering'"
    LoadDirect  = Q "SELECT TOP 1 LoadId FROM Loads WHERE TargetCompanyId IS NOT NULL"
    DriverPend  = Q "SELECT TOP 1 DriverId FROM Drivers WHERE Status='pending'"
    DriverOffer = Q "SELECT TOP 1 DriverId FROM Offers WHERE DriverId IS NOT NULL"
    Shipper     = Q "SELECT TOP 1 ShipperId FROM Shippers"
    Company     = Q "SELECT TOP 1 CompanyId FROM Companies"
    CompDriver  = Q "SELECT TOP 1 DriverId FROM Drivers WHERE CompanyId IS NOT NULL"
    CompVehicle = Q "SELECT TOP 1 VehicleId FROM Vehicles WHERE CompanyId IS NOT NULL"
    Complaint   = Q "SELECT TOP 1 ComplaintId FROM Complaints"
    Ticket      = Q "SELECT TOP 1 TicketId FROM Tickets"
    Token       = Q "SELECT TOP 1 TrackToken FROM Trips WHERE Status='in_transit'"
}

# ردیف‌های منو مستقیم از Services/PanelMenu.cs — منو و آزمون هیچ‌وقت از هم عقب نمی‌مانند
$menu = Get-Content (Join-Path $web "Services\PanelMenu.cs") -Raw -Encoding UTF8
$sections = @{}
foreach ($name in "Driver", "Shipper", "Company", "Admin") {
    $start = $menu.IndexOf("public static readonly MenuGroup[] $name =")
    $end = $menu.IndexOf("];", $start)
    $chunk = $menu.Substring($start, $end - $start)
    $sections[$name] = @([regex]::Matches($chunk, '(?:I|Top)\("([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
}

$extra = @{
    Driver  = @("/Driver/Trips/Detail/$($ids.TripLive)", "/Driver/Loads/Detail/$($ids.LoadOffer)", "/Driver/Messages/Thread?key=trip:$($ids.TripLive)")
    Shipper = @("/Shipper/Loads/Detail/$($ids.LoadOffer)", "/Shipper/Offers/Compare/$($ids.LoadOffer)", "/Shipper/Orders/Detail/$($ids.TripLive)",
                "/Shipper/Tracking?tripId=$($ids.TripLive)", "/Shipper/Tracking/History?tripId=$($ids.TripLive)",
                "/Shipper/Messages/Thread?key=trip:$($ids.TripLive)", "/Shipper/Offers/Driver/$($ids.DriverOffer)", "/Shipper/Loads/Create?copyOf=$($ids.LoadOffer)")
    Company = @("/Company/Trips/Detail/$($ids.TripCompany)", "/Company/Dispatch/Assign/$($ids.TripCompany)", "/Company/Loads/Detail/$($ids.LoadDirect)",
                "/Company/Loads/Detail/$($ids.LoadOffer)", "/Company/Monitoring/Data", "/Company/Monitoring/Route?tripId=$($ids.TripCompany)",
                "/Company/Drivers/Detail/$($ids.CompDriver)", "/Company/Fleet/Edit/$($ids.CompVehicle)", "/Company/Customers/History", "/Company/Customers/Contracts")
    Admin   = @("/Admin/Trips/Case/$($ids.TripLive)", "/Admin/Trips/Case/$($ids.TripDone)", "/Admin/Loads/Detail/$($ids.LoadOffer)",
                "/Admin/Users/Driver/$($ids.DriverPend)", "/Admin/Users/Shipper/$($ids.Shipper)", "/Admin/Users/Company/$($ids.Company)",
                "/Admin/Complaints/Detail/$($ids.Complaint)", "/Admin/Support/Thread/$($ids.Ticket)", "/Admin/Live/Data", "/Admin/Trips/Track?code=T")
}

# نشانی‌هایی که عمداً ری‌دایرکت می‌کنند: «سفر در حال انجام» → جزئیات سفر،
# «مقایسهٔ پیشنهادها» بدون بار → آخرین بارِ دارای پیشنهاد
$redirectOk = @("/Driver/Trips/Current", "/Shipper/Offers/Compare")

$accounts = [ordered]@{
    Driver  = @("driver", "09123333333", "1234")
    Shipper = @("shipper", "09121111111", "1234")
    Company = @("company", "09124444444", "1234")
    Admin   = @("admin", "09120000000", "Bargo@1405")
}

function New-Client {
    $h = New-Object System.Net.Http.HttpClientHandler
    $h.AllowAutoRedirect = $false
    $h.CookieContainer = New-Object System.Net.CookieContainer
    $c = New-Object System.Net.Http.HttpClient($h)
    $c.Timeout = [TimeSpan]::FromSeconds(90)
    return $c
}

function Get-Page($client, [string]$url) {
    $resp = $client.GetAsync("$Base$url").Result
    $body = $resp.Content.ReadAsStringAsync().Result
    $info = ""
    $code = [int]$resp.StatusCode
    if ($code -ge 300 -and $code -lt 400) { $info = "-> " + $resp.Headers.Location }
    elseif ($code -ge 500) {
        $m = [regex]::Match($body, 'class="titleerror">([^<]+)')
        if ($m.Success) { $info = [Net.WebUtility]::HtmlDecode($m.Groups[1].Value) }
    }
    return @($code, $info, $body)
}

$total = 0; $bad = 0
foreach ($role in $accounts.Keys) {
    $acc = $accounts[$role]
    $client = New-Client
    $login = Get-Page $client "/account/login"
    $token = [regex]::Match($login[2], '__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $form = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $form["Role"] = $acc[0]; $form["Mobile"] = $acc[1]; $form["Password"] = $acc[2]; $form["__RequestVerificationToken"] = $token
    $post = $client.PostAsync("$Base/account/login", (New-Object System.Net.Http.FormUrlEncodedContent($form))).Result
    Write-Host "== $role  (ورود → $($post.Headers.Location))"

    $urls = @($sections[$role]) + @($extra[$role]) | Select-Object -Unique
    $ok = 0
    foreach ($u in $urls) {
        $total++
        $r = Get-Page $client $u
        $path = $u.Split('?')[0]
        $isOk = $r[0] -eq 200 -or (($r[0] -eq 302) -and ($redirectOk -contains $path))
        if ($isOk) { $ok++ } else { $bad++; Write-Host ("   {0}  {1}  {2}" -f $r[0], $u, $r[1]) }
    }
    Write-Host "   OK: $ok / $($urls.Count)"
    $client.Dispose()
}

$pub = Get-Page (New-Client) "/t/$($ids.Token)"
Write-Host "== رهگیری عمومی /t/{token} → $($pub[0])"
Write-Host "TOTAL=$total BAD=$bad"

if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
if ($bad -gt 0) { exit 2 }
