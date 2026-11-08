# Runs the app's own checks (recorded game frames, no game or Npcap needed) in a built or published folder. A failed check ends this script with an error;
# each failed line becomes an error on the GitHub Actions run's page, so a red run says which check broke.
#
# -Runtime: the folder is the release's layout (Tools/release/publish.ps1); it is also checked that the app ran on the .NET in its "runtime" folder and
# not on one installed on the machine.
#
# Usage: pwsh Tools/release/selftest.ps1 -Dir out [-Exe AtreiaApp.exe] [-Runtime]
param(
    [Parameter(Mandatory = $true)][string]$Dir,
    [string]$Exe = 'AtreiaApp.exe',
    [switch]$Runtime
)

$dir = (Resolve-Path $Dir).Path
$trace = Join-Path $PWD 'host-trace.txt'
if ($Runtime) {
    # the .NET host writes how it found the runtime to this file
    $env:COREHOST_TRACE = '1'
    $env:COREHOST_TRACEFILE = $trace
    Remove-Item $trace -ErrorAction SilentlyContinue
}

$p = Start-Process -FilePath (Join-Path $dir $Exe) -ArgumentList @('selftest') -WorkingDirectory $dir -Wait -PassThru `
    -RedirectStandardOutput selftest.txt -RedirectStandardError selftest-errors.txt
Remove-Item Env:COREHOST_TRACE, Env:COREHOST_TRACEFILE -ErrorAction SilentlyContinue
Get-Content selftest.txt
$errors = Get-Content selftest-errors.txt -ErrorAction SilentlyContinue
if ($errors) { "--- errors ---"; $errors }
if ($p.ExitCode -ne 0 -or -not (Select-String -Path selftest.txt -SimpleMatch '[selftest] ALL CHECKS PASSED' -Quiet)) {
    $lines = Get-Content selftest.txt
    $shown = 0
    for ($i = 0; $i -lt $lines.Count -and $shown -lt 25; $i++) {
        if ($lines[$i] -match '\bFalse\b') {
            # the scenario's heading (the last "[selftest]" line above it) and the failed line
            $head = ''
            for ($j = $i; $j -ge 0; $j--) { if ($lines[$j] -like '[[]selftest]*') { $head = $lines[$j]; break } }
            "::error title=Self-check failed::$head $($lines[$i].Trim())"
            $shown++
        }
    }
    if ($errors) { "::error title=Self-check crashed::$(($errors | Select-Object -First 3) -join ' | ')" }
    "::error::The self-checks failed (exit $($p.ExitCode))."
    exit 1
}

if ($Runtime) {
    $text = "$(Get-Content $trace -Raw -ErrorAction SilentlyContinue)"
    $own = Join-Path $dir 'runtime\shared\Microsoft.NETCore.App'
    $global = (Get-Command dotnet -ErrorAction SilentlyContinue).Source | ForEach-Object { Split-Path -Parent $_ }
    $usedOwn = $text.IndexOf($own, [StringComparison]::OrdinalIgnoreCase) -ge 0
    $usedGlobal = $global -and ($text.IndexOf((Join-Path $global 'shared'), [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $text.IndexOf((Join-Path $global 'host\fxr'), [StringComparison]::OrdinalIgnoreCase) -ge 0)
    "--- how the host found .NET ---"
    ($text -split "`n") | Where-Object { $_ -match 'fxr|framework dir|Resolved|runtime\\' } | Select-Object -First 40
    if (-not $usedOwn -or $usedGlobal) {
        "::error title=Runtime folder::The app did not run only on the .NET in its runtime folder (own: $usedOwn, installed one at ${global}: $usedGlobal)."
        exit 1
    }
    "The app ran on the .NET in $dir\runtime."
}
