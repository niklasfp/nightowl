<#
.SYNOPSIS
  Manual release verification for NightOwl on Windows.

.DESCRIPTION
  Confirms the OS actually holds NightOwl's power request by inspecting
  `powercfg /requests` while the tool runs, and that it clears when it exits.
  Run from an ELEVATED terminal — powercfg hides process names otherwise.

.PARAMETER Bin
  Path to nightowl.exe (default: `nightowl` on PATH).

.EXAMPLE
  ./verify-windows.ps1 .\nightowl.exe
#>
param([string]$Bin = "nightowl")

$script:pass = 0
$script:fail = 0
function Ok($m) { Write-Host "  PASS $m" -ForegroundColor Green; $script:pass++ }
function No($m) { Write-Host "  FAIL $m" -ForegroundColor Red; $script:fail++ }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "Not elevated: 'powercfg /requests' may hide process names. Re-run as Administrator for reliable results."
}

$cmd = Get-Command $Bin -ErrorAction SilentlyContinue
if (-not $cmd) { Write-Error "nightowl not found: $Bin"; exit 2 }
$exe = $cmd.Source
Write-Host "Testing: $(& $exe --version)"

# Return the text block under a `powercfg /requests` category header (e.g. SYSTEM, DISPLAY).
function Get-RequestSection([string]$name) {
    $text = (powercfg /requests | Out-String)
    if ($text -match "(?ms)^${name}:\s*(.*?)(?=^\w+:|\z)") { return $Matches[1] }
    return ""
}

Write-Host "Pre-flight"
if ((& $exe --help) -match "USAGE") { Ok "--help shows usage" } else { No "--help shows usage" }
& $exe --frobnicate *> $null
if ($LASTEXITCODE -eq 2) { Ok "unknown option exits 2" } else { No "unknown option exits 2" }

Write-Host "System request + release on stop"
$p = Start-Process -FilePath $exe -ArgumentList "-v" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
if ((Get-RequestSection "SYSTEM") -match "nightowl") { Ok "SYSTEM request held by nightowl.exe" } else { No "SYSTEM request present while running" }
Stop-Process -Id $p.Id -Force
Start-Sleep -Seconds 1
if ((Get-RequestSection "SYSTEM") -match "nightowl") { No "request released after stop" } else { Ok "request released after stop" }

Write-Host "Display request"
$p = Start-Process -FilePath $exe -ArgumentList "--display", "-v" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
if ((Get-RequestSection "DISPLAY") -match "nightowl") { Ok "DISPLAY request held with --display" } else { No "DISPLAY request present with --display" }
Stop-Process -Id $p.Id -Force

Write-Host "Block sleep (-s/--block-sleep)"
$p = Start-Process -FilePath $exe -ArgumentList "--block-sleep", "-v" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
if ((Get-RequestSection "EXECUTION") -match "nightowl") { Ok "EXECUTION request held with --block-sleep" } else { No "EXECUTION request present with --block-sleep" }
Stop-Process -Id $p.Id -Force
Start-Sleep -Seconds 1
if ((Get-RequestSection "EXECUTION") -match "nightowl") { No "EXECUTION request released after stop" } else { Ok "EXECUTION request released after stop" }

Write-Host "Timed auto-release"
$sw = [Diagnostics.Stopwatch]::StartNew()
& $exe --for 3s *> $null
$rc = $LASTEXITCODE
$sw.Stop()
$sec = [int]$sw.Elapsed.TotalSeconds
if ($rc -eq 0 -and $sec -ge 2 -and $sec -le 8) { Ok "--for 3s exited 0 after ~3s ($sec s)" } else { No "--for timing/exit (rc=$rc, $sec s)" }

Write-Host "Wrap exit codes"
& $exe -- cmd /c "exit 0"
if ($LASTEXITCODE -eq 0) { Ok "wrap propagates 0" } else { No "wrap propagates 0" }
& $exe -- cmd /c "exit 5"
if ($LASTEXITCODE -eq 5) { Ok "wrap propagates 5" } else { No "wrap propagates 5" }

Write-Host ""
Write-Host "Passed: $($script:pass)  Failed: $($script:fail)"
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
