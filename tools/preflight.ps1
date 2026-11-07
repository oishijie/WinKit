<#
  WinKit local preflight gate.

  Purpose: make "code that does not compile" impossible to commit.
  This is a LOCAL, ON-DEMAND script only. It is not a workflow, not a CI job,
  and it is never triggered automatically by git or by any remote service.

  Usage (PowerShell, from anywhere):
      powershell -ExecutionPolicy Bypass -File tools\preflight.ps1
      powershell -ExecutionPolicy Bypass -File tools\preflight.ps1 -NoRestore

  Exit code 0 = all checks passed, 1 = at least one check failed.
#>
param(
    [switch]$NoRestore,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Continue"
# Pin CLI/MSBuild output to English so the "N Error(s) / N Warning(s)" summary
# can be parsed with a locale-independent regex. Process-scoped only.
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "WinKit.csproj"
$outDir = Join-Path $root "bin\$Configuration\net8.0-windows"
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail })
}

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "== $text" -ForegroundColor Cyan
}

if (-not (Test-Path $proj)) {
    Write-Host "FATAL: project file not found: $proj" -ForegroundColor Red
    exit 1
}

Write-Host "WinKit preflight | root: $root | config: $Configuration"

# --- 0. SDK ---------------------------------------------------------------
Write-Step "0/5 .NET SDK"
$sdkVersion = (& dotnet --version 2>&1 | Out-String).Trim()
$sdkOk = ($LASTEXITCODE -eq 0) -and ($sdkVersion -match '^\d+\.\d+\.\d+')
Add-Result "SDK available" $sdkOk "dotnet --version -> $sdkVersion"
if (-not $sdkOk) {
    Write-Host "No usable SDK. Install a .NET SDK (10.x works) and retry." -ForegroundColor Red
    Write-Host "NOTE: a runtime-only dotnet.exe on PATH produces this exact failure."
}

# --- 1. Restore -----------------------------------------------------------
Write-Step "1/5 dotnet restore"
if ($NoRestore) {
    Add-Result "restore" $true "skipped (-NoRestore)"
} elseif ($sdkOk) {
    $restoreOut = (& dotnet restore $proj --nologo -v minimal 2>&1 | Out-String)
    $restoreOk = ($LASTEXITCODE -eq 0) -and ($restoreOut -notmatch '(?im)^\s*error\b')
    $restoreDetail = if ($restoreOk) { "restore succeeded" } else { ($restoreOut -split "`n" | Where-Object { $_ -match 'error|NU1' } | Select-Object -First 4) -join ' | ' }
    Add-Result "restore" $restoreOk $restoreDetail
    if (-not $restoreOk) { Write-Host $restoreOut -ForegroundColor Red }
} else {
    Add-Result "restore" $false "skipped: no SDK"
}

# --- 2. Build -------------------------------------------------------------
Write-Step "2/5 dotnet build -c $Configuration"
$buildOk = $false
$errCount = -1
$warnCount = -1
if ($sdkOk) {
    $buildArgs = @("build", $proj, "-c", $Configuration, "--no-restore", "--nologo", "-v", "minimal")
    $buildOut = (& dotnet @buildArgs 2>&1 | Out-String)
    $buildExit = $LASTEXITCODE
    Write-Host $buildOut

    # Never put non-ASCII literals in this file: Windows PowerShell 5.1 reads
    # UTF-8 without BOM as ANSI and mangles them into invalid regex. The CLI
    # language is pinned to English above, so summary lines stay stable.
    $mErr = [regex]::Match($buildOut, '(?im)^\s*(\d+)\s+Error\(s\)')
    $mWarn = [regex]::Match($buildOut, '(?im)^\s*(\d+)\s+Warning\(s\)')
    $errCount = if ($mErr.Success) { [int]$mErr.Groups[1].Value } else { -1 }
    $warnCount = if ($mWarn.Success) { [int]$mWarn.Groups[1].Value } else { -1 }
    $hasErrorText = $buildOut -match '(?i)(error\s+(CS|MSB|NETSDK|NU|MC|XAML)\d+|: error )'
    $hasWarnText = $buildOut -match '(?i)(: warning |warning\s+(CS|MSB|NETSDK|NU|MC|XAML)\d+)'
    $buildOk = ($buildExit -eq 0) -and (-not $hasErrorText) -and ($errCount -le 0)
    # If the localized summary cannot be parsed, fall back to diagnostic text.
    # A previous version matched any "N word" line and mistook "1 error" for a
    # warning, producing a false FAIL on warning checks for failed builds.
    $warnOk = if ($mWarn.Success) { ($warnCount -eq 0) -and (-not $hasWarnText) } else { -not $hasWarnText }
    $warnDetail = if ($warnCount -lt 0) { "unknown (summary not parsed)" } else { "$warnCount" }
    Add-Result "build exit / errors" $buildOk "exit=$buildExit errors=$errCount"
    Add-Result "build warnings" $warnOk "warnings=$warnDetail"
} else {
    Add-Result "build exit / errors" $false "skipped: no SDK"
    Add-Result "build warnings" $false "skipped: no SDK"
}

# --- 3. Output artifacts --------------------------------------------------
Write-Step "3/5 release artifacts"
if (Test-Path $outDir) {
    $required = @(
        "WinKit.exe", "WinKit.dll", "WinKit.deps.json", "WinKit.runtimeconfig.json",
        "Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll", "RapidOcrNet.dll"
    )
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $outDir $_)) })
    Add-Result "core binaries" ($missing.Count -eq 0) $(if ($missing.Count) { "missing: $($missing -join ', ')" } else { "$($required.Count) files present" })

    $modelsDir = Join-Path $outDir "models"
    $models = @(
        "PP-OCRv6_det_small.onnx", "PP-OCRv6_rec_small.onnx", "PP-OCRv6_small_dict.txt",
        "PP-OCRv6_det_tiny.onnx", "PP-OCRv6_rec_tiny.onnx", "PP-OCRv6_tiny_dict.txt",
        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"
    )
    $missingModels = @($models | Where-Object { -not (Test-Path (Join-Path $modelsDir $_)) })
    Add-Result "OCR models" ($missingModels.Count -eq 0) $(if ($missingModels.Count) { "missing: $($missingModels -join ', ')" } else { "$($models.Count) files present" })

    $hasRuntimes = Test-Path (Join-Path $outDir "runtimes")
    Add-Result "RID locked (no runtimes/)" (-not $hasRuntimes) $(if ($hasRuntimes) { "runtimes/ exists: RID was lost, output will bloat to ~378MB" } else { "win-x64 native assets flattened" })

    $sizeMB = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Add-Result "output size 40-120MB" (($sizeMB -ge 40) -and ($sizeMB -le 120)) "$sizeMB MB"
} else {
    Add-Result "core binaries" $false "output dir not found: $outDir"
    Add-Result "OCR models" $false "output dir not found"
    Add-Result "RID locked (no runtimes/)" $false "output dir not found"
    Add-Result "output size 40-120MB" $false "output dir not found"
}

# --- 4. Version consistency ----------------------------------------------
Write-Step "4/5 version consistency"
$csprojRaw = Get-Content $proj -Raw -Encoding UTF8
$mVer = [regex]::Match($csprojRaw, '<Version>([^<]+)</Version>')
$csprojVersion = if ($mVer.Success) { $mVer.Groups[1].Value.Trim() } else { "" }
$latestPath = Join-Path $root "latest.json"
$latestVersion = ""
if (Test-Path $latestPath) {
    try { $latestVersion = (Get-Content $latestPath -Raw -Encoding UTF8 | ConvertFrom-Json).version } catch { }
}
$versionOk = ($csprojVersion -ne "") -and ($csprojVersion -eq $latestVersion)
Add-Result "csproj == latest.json" $versionOk "csproj=$csprojVersion latest.json=$latestVersion"

$changelogPath = Join-Path $root "CHANGELOG.md"
$changelogOk = $false
$changelogDetail = "not found"
if (Test-Path $changelogPath) {
    $cl = Get-Content $changelogPath -Raw -Encoding UTF8
    $changelogOk = $cl -match "(?m)^## \[$([regex]::Escape($csprojVersion))\]"
    $changelogDetail = "expecting heading [$csprojVersion], found=$changelogOk"
}
Add-Result "CHANGELOG release heading" $changelogOk $changelogDetail

# --- Summary --------------------------------------------------------------
Write-Host ""
Write-Host "================ PREFLIGHT SUMMARY ================" -ForegroundColor Cyan
$results | ForEach-Object {
    $mark = if ($_.Pass) { "PASS" } else { "FAIL" }
    $color = if ($_.Pass) { "Green" } else { "Red" }
    Write-Host ("{0,-4} {1,-30} {2}" -f $mark, $_.Check, $_.Detail) -ForegroundColor $color
}
$failed = @($results | Where-Object { -not $_.Pass })
Write-Host "===================================================" -ForegroundColor Cyan
if ($failed.Count -gt 0) {
    Write-Host "PREFLIGHT FAILED: $($failed.Count) check(s) failed. Fix before committing." -ForegroundColor Red
    exit 1
}
Write-Host "PREFLIGHT PASSED: all $($results.Count) checks green." -ForegroundColor Green
Write-Host "Next: run the GUI smoke checklist in docs\local-preflight-checklist.md" -ForegroundColor Yellow
exit 0
