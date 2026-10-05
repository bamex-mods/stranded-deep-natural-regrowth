param(
    [string]$GameRoot = $env:STRANDED_DEEP_GAME_ROOT
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    throw "GameRoot was not supplied. Pass -GameRoot or set STRANDED_DEEP_GAME_ROOT."
}
$Managed = Join-Path $GameRoot "Stranded_Deep_Data\Managed"
$BepInExCore = Join-Path $GameRoot "BepInEx\core"
$Plugins = Join-Path $GameRoot "BepInEx\plugins"
$ConfigRoot = Join-Path $GameRoot "BepInEx\config\StrandedDeepNaturalRegrowth"
$ConfigFile = Join-Path $GameRoot "BepInEx\config\com.bamex.strandeddeep.naturalregrowth.cfg"

$Compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$Sources = @(
    (Join-Path $PSScriptRoot "NaturalRegrowthModel.cs"),
    (Join-Path $PSScriptRoot "NaturalRegrowthPersistence.cs"),
    (Join-Path $PSScriptRoot "NaturalRegrowthClock.cs"),
    (Join-Path $PSScriptRoot "SDK\ModSettingsClient.cs"),
    (Join-Path $PSScriptRoot "NaturalRegrowthPlugin.cs")
)

$PluginDir = Join-Path $Plugins "StrandedDeepNaturalRegrowth"
$Output = Join-Path $PluginDir "StrandedDeepNaturalRegrowth.dll"

if (-not (Test-Path $Compiler)) { throw "C# compiler not found: $Compiler" }
foreach ($Source in $Sources) {
    if (-not (Test-Path $Source)) { throw "Source file not found: $Source" }
}

New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null

$References = New-Object System.Collections.Generic.List[string]
$Required = @(
    (Join-Path $BepInExCore "BepInEx.dll"),
    (Join-Path $BepInExCore "0Harmony.dll"),
    (Join-Path $Managed "Assembly-CSharp.dll"),
    (Join-Path $Managed "bolt.dll"),
    (Join-Path $Managed "bolt.user.dll"),
    (Join-Path $Managed "Rewired_Core.dll")
)
foreach ($Dll in $Required) {
    if (-not (Test-Path $Dll)) { throw "Required assembly not found: $Dll" }
    $References.Add($Dll)
}
Get-ChildItem -Path $Managed -Filter "UnityEngine*.dll" | Sort-Object Name | ForEach-Object { $References.Add($_.FullName) }

$Args = New-Object System.Collections.Generic.List[string]
$Args.Add("/nologo")
$Args.Add("/target:library")
$Args.Add("/optimize+")
$Args.Add("/debug-")
$Args.Add("/langversion:5")
$Args.Add("/out:$Output")
foreach ($Reference in $References) { $Args.Add("/reference:$Reference") }
foreach ($Source in $Sources) { $Args.Add($Source) }

Write-Host "Game:       $GameRoot"
Write-Host "Compiler:   $Compiler"
Write-Host "Sources:"
foreach ($Source in $Sources) { Write-Host "  $Source" }
Write-Host "Output:     $Output"
Write-Host "References: $($References.Count)"
Write-Host ""

& $Compiler $Args.ToArray()
if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $Output)) { throw "Build reported success but DLL was not created: $Output" }

# One-time cleanup of obsolete diagnostic artifacts from older development builds.
$ObsoleteFiles = @(
    (Join-Path $ConfigRoot "status.txt"),
    (Join-Path $ConfigRoot "clock-probe.log"),
    (Join-Path $ConfigRoot "coconut-diagnostic.log"),
    (Join-Path $PluginDir "StrandedDeepNaturalRegrowth.pdb")
)
foreach ($Path in $ObsoleteFiles) {
    if (Test-Path $Path) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        Write-Host "Removed obsolete diagnostic artifact: $Path"
    }
}

$DiagnosticDll = Join-Path $Plugins "StrandedDeepPalmRegrowthDiagnostic\StrandedDeepPalmRegrowthDiagnostic.dll"
if (Test-Path $DiagnosticDll) {
    Remove-Item -LiteralPath $DiagnosticDll -Force -ErrorAction SilentlyContinue
    Write-Host "Removed obsolete diagnostic plugin: $DiagnosticDll"
}

$LegacyDll = Join-Path $Plugins "StrandedDeepNaturalRegrowth.dll"
if (Test-Path $LegacyDll) {
    Remove-Item -LiteralPath $LegacyDll -Force -ErrorAction SilentlyContinue
    Write-Host "Removed legacy root-level DLL: $LegacyDll"
}

# Remove the obsolete [Debug] section from the existing BepInEx config.
# If the exact fast-test coconut values are still present, normalize only those known test values back to production.
if (Test-Path $ConfigFile) {
    $ConfigText = Get-Content -LiteralPath $ConfigFile -Raw
    $ConfigText = [regex]::Replace($ConfigText, '(?ms)^\[Debug\]\s*\r?\n.*?(?=^\[[^\r\n]+\]|\z)', '')

    $HasFastTestMin = $ConfigText -match '(?m)^MinRegrowthDays\s*=\s*1\s*$'
    $HasFastTestMax = $ConfigText -match '(?m)^MaxRegrowthDays\s*=\s*1\s*$'
    $HasFastTestChance = $ConfigText -match '(?m)^SuccessChance\s*=\s*1(?:\.0+)?\s*$'
    if ($HasFastTestMin -and $HasFastTestMax -and $HasFastTestChance) {
        $ConfigText = [regex]::Replace($ConfigText, '(?m)^MinRegrowthDays\s*=.*$', 'MinRegrowthDays = 15')
        $ConfigText = [regex]::Replace($ConfigText, '(?m)^MaxRegrowthDays\s*=.*$', 'MaxRegrowthDays = 25')
        $ConfigText = [regex]::Replace($ConfigText, '(?m)^SuccessChance\s*=.*$', 'SuccessChance = 0.15')
        $ConfigText = [regex]::Replace($ConfigText, '(?m)^OneCoconutWeight\s*=.*$', 'OneCoconutWeight = 88')
        $ConfigText = [regex]::Replace($ConfigText, '(?m)^TwoCoconutWeight\s*=.*$', 'TwoCoconutWeight = 11')
        $ConfigText = [regex]::Replace($ConfigText, '(?m)^ThreeCoconutWeight\s*=.*$', 'ThreeCoconutWeight = 1')
        Write-Host "Normalized known fast-test coconut config back to production values."
    }

    Set-Content -LiteralPath $ConfigFile -Value $ConfigText -Encoding UTF8
}

Write-Host ""
Write-Host "Build OK - Natural Regrowth v0.2.2 bilingual Mod Settings release:"
Write-Host $Output
Write-Host "Diagnostics/hotkeys/probe files are not part of this build."
