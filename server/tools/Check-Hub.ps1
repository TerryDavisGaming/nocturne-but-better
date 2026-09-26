<#
.SYNOPSIS
  checks a deployed nocturne but better hub from the outside (DESIGN-HUB 4.3). it only reads, and sends two
  writes that the hub must refuse. no key is needed or sent.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Check-Hub.ps1
  powershell -ExecutionPolicy Bypass -File tools\Check-Hub.ps1 -Url https://hub.nocturnbutbetter.com -WorkersDev nbb-hub.example.workers.dev
#>
param(
  [string]$Url = "https://hub.nocturnbutbetter.com",
  # optional: the worker's workers.dev name, which must NOT answer (workers.dev is turned off)
  [string]$WorkersDev = ""
)

$ErrorActionPreference = "Stop"
# windows powershell 5.1 may not offer tls 1.2 by default
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Url = $Url.TrimEnd("/")
$failures = 0

function Send([string]$Method, [string]$Path, [hashtable]$Headers = @{}, [string]$Body = $null, [string]$Base = $Url) {
  $request = [Net.HttpWebRequest]::Create($Base + $Path)
  $request.Method = $Method
  $request.AllowAutoRedirect = $false
  $request.UserAgent = "Check-Hub/1"
  $request.Timeout = 20000
  foreach ($k in $Headers.Keys) {
    if ($k -eq "Content-Type") { $request.ContentType = $Headers[$k] } else { $request.Headers[$k] = $Headers[$k] }
  }
  if ($Body -ne $null -and $Body -ne "") {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Body)
    $request.ContentLength = $bytes.Length
    $stream = $request.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
  }
  try { $response = $request.GetResponse() }
  catch [Net.WebException] {
    if ($_.Exception.Response -eq $null) { return [pscustomobject]@{ Status = 0; Headers = @{}; Text = $_.Exception.Message } }
    $response = $_.Exception.Response
  }
  $reader = New-Object IO.StreamReader($response.GetResponseStream())
  $text = $reader.ReadToEnd()
  $reader.Close()
  $headers = @{}
  foreach ($k in $response.Headers.AllKeys) { $headers[$k.ToLowerInvariant()] = $response.Headers[$k] }
  $status = [int]$response.StatusCode
  $response.Close()
  return [pscustomobject]@{ Status = $status; Headers = $headers; Text = $text }
}

function Check([string]$What, [bool]$Ok, [string]$Detail = "") {
  if ($Ok) { Write-Host ("  ok    " + $What) }
  else { Write-Host ("  FAIL  " + $What + $(if ($Detail) { ": " + $Detail } else { "" })) -ForegroundColor Red; $script:failures++ }
}

function NoCors($r) {
  foreach ($k in $r.Headers.Keys) { if ($k.StartsWith("access-control-")) { return $false } }
  return $true
}

Write-Host "checking $Url"

$info = Send "GET" "/v1/info"
Check "GET /v1/info answers 200" ($info.Status -eq 200) "status $($info.Status)"
$json = $null
try { $json = $info.Text | ConvertFrom-Json } catch { }
Check "/v1/info is the hub's json with api 1" ($json -ne $null -and $json.api -eq 1) $info.Text.Substring(0, [Math]::Min(120, $info.Text.Length))
Check "/v1/info is cached 5 minutes" ($info.Headers["cache-control"] -eq "public, max-age=300") $info.Headers["cache-control"]
Check "security headers (nosniff, no-referrer)" ($info.Headers["x-content-type-options"] -eq "nosniff" -and $info.Headers["referrer-policy"] -eq "no-referrer")
Check "no cors headers" (NoCors $info)
if ($json -ne $null -and $json.legalUrl) { Check "legalUrl is on this address" ($json.legalUrl -eq "$Url/legal") $json.legalUrl }
if ($json -ne $null) { Check "the game gets the takedown contact" ([string]$json.takedownContact -ne "") "fill in takedown_contact on /admin" }
if ($Url.StartsWith("https://")) {
  $plain = Send "GET" "/v1/info" @{} $null ("http://" + $Url.Substring(8))
  $location = [string]$plain.Headers["location"]
  Check "plain http is sent to https (always use https)" ($plain.Status -ge 300 -and $plain.Status -lt 400 -and $location.StartsWith("https://")) "status $($plain.Status): turn on ssl/tls -> edge certificates -> always use https"
}

$list = Send "GET" "/v1/packages"
Check "GET /v1/packages answers 200" ($list.Status -eq 200) "status $($list.Status)"
$cc = [string]$list.Headers["cache-control"]
Check "lists: max-age 60 with stale-while-revalidate, no s-maxage" ($cc -eq "public, max-age=60, stale-while-revalidate=300") $cc

$junk = Send "GET" "/v1/packages?x=1"
Check "a junk query string is 400 bad_query" ($junk.Status -eq 400 -and $junk.Text -match "bad_query") "status $($junk.Status)"
Check "errors are never cached" ($junk.Headers["cache-control"] -eq "no-store") $junk.Headers["cache-control"]

$write = Send "PUT" "/v1/me" @{ "Content-Type" = "application/json" } '{"name":"check"}'
Check "a write without X-NBB-Client is 400" ($write.Status -eq 400 -and $write.Text -match "bad_client") "status $($write.Status)"
$simple = Send "POST" "/v1/uploads" @{ "Content-Type" = "text/plain"; "X-NBB-Client" = "1" } '{}'
Check "a text/plain write is 400" ($simple.Status -eq 400) "status $($simple.Status)"
$options = Send "OPTIONS" "/v1/info" @{ "Origin" = "https://example.org"; "Access-Control-Request-Method" = "POST" }
Check "OPTIONS is 405 with no cors headers" ($options.Status -eq 405 -and (NoCors $options)) "status $($options.Status)"

$robots = Send "GET" "/robots.txt"
Check "robots.txt says Disallow: /" ($robots.Status -eq 200 -and $robots.Text -match "Disallow: /")
$legal = Send "GET" "/legal"
Check "/legal answers with its content security policy" ($legal.Status -eq 200 -and ([string]$legal.Headers["content-security-policy"]).StartsWith("default-src 'none'"))
Check "/legal shows a takedown contact" ($legal.Text -notmatch "NOT SET UP YET") "fill in takedown_contact on /admin"
$admin = Send "GET" "/admin"
Check "/admin answers, never cached" ($admin.Status -eq 200 -and $admin.Headers["cache-control"] -eq "no-store")
$overview = Send "GET" "/v1/admin/overview"
Check "the owner's api refuses a request without the key" ($overview.Status -eq 401) "status $($overview.Status) (503 admin_off means ADMIN_KEY isn't set as a secret)"
$v2 = Send "GET" "/v2/info"
Check "other api versions are 404" ($v2.Status -eq 404)

if ($WorkersDev) {
  $dev = Send "GET" "/v1/info" @{} $null ("https://" + $WorkersDev.Trim("/"))
  Check "workers.dev does NOT answer with the hub" ($dev.Status -ne 200 -or $dev.Text -notmatch '"api"') "turn workers.dev off (Worker -> Settings -> Domains & Routes)"
}

if ($failures -eq 0) { Write-Host "all checks passed" -ForegroundColor Green; exit 0 }
Write-Host "$failures check(s) failed" -ForegroundColor Red
exit 1
