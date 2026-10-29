<#
.SYNOPSIS
    Runs the camera audit as a technician's report: device IPs, usernames and
    passwords, snapshots, HTML + PDF + CSV.

.DESCRIPTION
    Runs XProtectCameraAudit.exe with --passwords and --pdf. The report is the
    technician's copy (its file name ends in -technician); it holds device
    passwords in cleartext, so do not hand it to the customer.

    Every recording server is reported unless -Recorder names some.

    Put this script in the folder with XProtectCameraAudit.exe and its DLLs, or
    point -ExePath at the exe.

.EXAMPLE
    .\Run-TechnicianReport.ps1 -Customer "Acme"
    All recording servers on this machine's Management Server, logged in as you.

.EXAMPLE
    .\Run-TechnicianReport.ps1 -Customer "Acme" -Recorder RS-North
    Only the recording server RS-North (by name or host name).

.EXAMPLE
    .\Run-TechnicianReport.ps1 -Server https://mgmt.acme.local -Customer "Acme" -Recorder RS-North,RS-South -Auth basic -User tech
    Two recording servers on another Management Server, as a Basic user (the
    password is asked for).
#>
param(
    # Management Server address.
    [string]$Server = "http://localhost",

    # Recording servers to report on, by name or host name. Leave out for all.
    [string[]]$Recorder = @(),

    # Customer name; shown in the report and used in the file name.
    [string]$Customer = "",

    # Where the report goes.
    [string]$Output = (Join-Path $PSScriptRoot "reports"),

    # windows-current = the logged-in Windows user; windows = another Windows
    # account; basic = an XProtect Basic user.
    [ValidateSet("windows-current", "windows", "basic")]
    [string]$Auth = "windows-current",

    # User for -Auth windows (DOMAIN\user) or basic. The password is asked for.
    [string]$User = "",

    # No PDF, only HTML and CSV.
    [switch]$NoPdf,

    # Inventory only, no snapshots (much faster).
    [switch]$SkipSnapshots,

    # For a self-signed Management Server certificate: trust exactly this one.
    [string]$TrustCertThumbprint = "",

    # XProtectCameraAudit.exe, if it is not beside this script.
    [string]$ExePath = (Join-Path $PSScriptRoot "XProtectCameraAudit.exe"),

    # Open the report when done.
    [switch]$Open
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    throw "XProtectCameraAudit.exe was not found at '$ExePath'. Put this script in its folder, or pass -ExePath."
}

$arguments = @("--server", $Server, "--auth", $Auth, "--passwords", "--output", $Output)
if ($Customer) { $arguments += @("--customer", $Customer) }
foreach ($r in $Recorder) {
    if ($r.Trim()) { $arguments += @("--recorder", $r.Trim()) }
}
if (-not $NoPdf) { $arguments += "--pdf" }
if ($SkipSnapshots) { $arguments += "--skip-snapshots" }
if ($TrustCertThumbprint) { $arguments += @("--trust-cert-thumbprint", $TrustCertThumbprint) }

# The password goes through an environment variable of this process only, so
# it is never on a command line where other processes can read it.
$passwordVar = "CAMERA_AUDIT_PW"
if ($Auth -ne "windows-current") {
    if (-not $User) { throw "-Auth $Auth needs -User." }
    $secure = Read-Host "Password for $User" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Environment]::SetEnvironmentVariable($passwordVar, [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr), "Process") }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $arguments += @("--user", $User, "--password-env", $passwordVar)
}

if ($Recorder.Count -gt 0) {
    Write-Host ("Recording servers: " + ($Recorder -join ", ")) -ForegroundColor Cyan
} else {
    Write-Host "Recording servers: all" -ForegroundColor Cyan
}

try {
    & $ExePath @arguments
    $code = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable($passwordVar, $null, "Process")
}

Write-Host ""
switch ($code) {
    0 { Write-Host "Done: every camera gave a picture." -ForegroundColor Green }
    2 { Write-Host "Done, with cameras that gave no picture - see the report." -ForegroundColor Yellow }
    default { Write-Host "The report failed (exit code $code) - see the messages above." -ForegroundColor Red }
}
if ($code -eq 0 -or $code -eq 2) {
    Write-Host "Technician's copy, with device passwords - do not hand it to the customer." -ForegroundColor Yellow
    if ($Open) {
        $latest = Get-ChildItem $Output -Filter "*-technician*.html" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latest) { Invoke-Item $latest.FullName }
    }
}
exit $code
