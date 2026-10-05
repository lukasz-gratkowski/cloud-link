#requires -Version 7.0
<#
.SYNOPSIS
    Checks, without signing in, whether a Microsoft Entra app registration is set up the way CloudLink needs.

.DESCRIPTION
    Asks Microsoft's sign-in service for a sign-in page for the application and reads the answer. This shows
    whether the application ID exists, allows personal Microsoft accounts, and has the redirect address
    http://localhost. Nothing is signed in and nothing is changed.

    The answer is OK only when Microsoft actually returns its sign-in form. Anything unrecognised is reported
    as UNKNOWN rather than guessed at.

    Work and school accounts cannot be checked this way: their endpoint reports most problems only after
    sign-in. The first real sign-in remains the final test.

.PARAMETER ClientId
    The Application (client) ID. Defaults to the one in Directory.Build.props.
#>
param([string]$ClientId)
$ErrorActionPreference = 'Stop'

if (-not $ClientId) {
    $props = [xml](Get-Content (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw)
    $node = $props.SelectSingleNode('/Project/PropertyGroup/CloudLinkMicrosoftClientId')
    if ($node) { $ClientId = $node.InnerText.Trim() }
}
if (-not $ClientId) { throw 'No application ID given and none in Directory.Build.props.' }
$parsed = [guid]::Empty
if (-not [guid]::TryParse($ClientId, [ref]$parsed)) { throw "Not an application ID: $ClientId" }
$ClientId = $parsed.ToString()

$accountType = '"Any Entra ID Tenant + Personal Microsoft accounts" (older pages: "Accounts in any organizational directory and personal Microsoft accounts")'

function Get-SignInPage([string]$authority) {
    $query = [ordered]@{
        client_id             = $ClientId
        response_type         = 'code'
        redirect_uri          = 'http://localhost:53127'
        scope                 = 'Files.ReadWrite.All User.Read offline_access'
        code_challenge        = 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM'
        code_challenge_method = 'S256'
        state                 = 'check'
    }
    $url = "https://login.microsoftonline.com/$authority/oauth2/v2.0/authorize?" +
        (($query.GetEnumerator() | ForEach-Object { "$($_.Key)=$([uri]::EscapeDataString($_.Value))" }) -join '&')
    try {
        $response = Invoke-WebRequest -Uri $url -MaximumRedirection 5 -SkipHttpErrorCheck -UseBasicParsing -TimeoutSec 30
    }
    catch {
        return [pscustomobject]@{ Status = 0; Host = ''; Raw = ''; Text = ''; Error = $_.Exception.Message }
    }
    [pscustomobject]@{
        Error  = $null
        Status = [int]$response.StatusCode
        Host   = $response.BaseResponse.RequestMessage.RequestUri.Host
        Raw    = $response.Content
        Text   = [System.Net.WebUtility]::HtmlDecode(($response.Content -replace '<script[\s\S]*?</script>', '' -replace '<[^>]+>', ' ' -replace '\s+', ' '))
    }
}

# Personal accounts: this endpoint checks the application and the redirect address before any sign-in.
$personal = Get-SignInPage 'consumers'
$verdict, $code = if ($personal.Error) {
    "UNKNOWN: Microsoft's sign-in service could not be reached ($($personal.Error)). Check the connection and try again.", 2
}
elseif ($personal.Text -match 'unauthorized_client') {
    "NOT READY: Microsoft does not know this application ID, or the registration does not allow personal Microsoft accounts. Supported account types must be $accountType.", 1
}
elseif ($personal.Text -match "redirect_uri' is not valid") {
    'NOT READY: the redirect address is missing. In the registration open Authentication, add a redirect URI for the platform "Mobile and desktop applications" and enter http://localhost', 1
}
elseif ($personal.Text -match 'unable to complete your request') {
    ('NOT READY: ' + ($personal.Text -replace '.*unable to complete your request\s*', '').Trim()), 1
}
elseif ($personal.Status -eq 200 -and $personal.Host -eq 'login.live.com' -and $personal.Raw -match 'PPFT|loginfmt|sFTTag|urlPost') {
    'OK', 0
}
else {
    "UNKNOWN: Microsoft answered with something this check does not recognise (HTTP $($personal.Status) from $($personal.Host)). Try again later, or test with a real sign-in.", 2
}

# Work and school accounts: only a few problems show before sign-in, but those few are worth catching.
if ($code -eq 0) {
    $work = Get-SignInPage 'common'
    if ($work.Error) {
        $verdict = "OK for personal Microsoft accounts: the application ID is known and accepts http://localhost. " +
            "The extra look at work and school accounts could not be made ($($work.Error))."
    }
    elseif ($work.Raw -match 'AADSTS9002331') {
        $verdict, $code = "NOT READY: the registration is for personal Microsoft accounts only, which CloudLink's sign-in address does not accept. Supported account types must be $accountType.", 1
    }
    elseif ($work.Raw -match 'AADSTS(700016|50194|50011)') {
        $verdict, $code = "NOT READY: Microsoft reports $($Matches[0]) for work and school accounts. Check the supported account types ($accountType) and the redirect address.", 1
    }
    else {
        $verdict = 'OK: the application ID is known, allows personal Microsoft accounts, and accepts http://localhost. ' +
            'Make sure that redirect address is listed under "Mobile and desktop applications" (not Web or Single-page application). ' +
            'The first real sign-in is the final proof, in particular for work and school accounts.'
    }
}

[pscustomobject]@{ ClientId = $ClientId; Result = $verdict } | Format-List
exit $code
