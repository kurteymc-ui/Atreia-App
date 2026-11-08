# Publishes the app in the layout it is installed in (.github/workflows/release.yml, and the "Release layout" check of ci.yml):
#
#   AtreiaApp.exe, AtreiaApp.dll and the app's own libraries
#   assets\     the game data, icons, maps and sounds
#   licenses\   the app's license, the notices of the parts from other projects, the fonts' license
#   runtime\    the .NET runtime it runs on (the exe looks for it only there: ReleaseLayout in Client/AionDPS.csproj)
#
# Usage: pwsh Tools/release/publish.ps1 -Version 1.21.0 [-Out publish]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Out = 'publish'
)

$ErrorActionPreference = 'Stop'

dotnet publish Client/AionDPS.csproj -c Release -r win-x64 --self-contained false -p:ReleaseLayout=true -p:PublishSingleFile=false `
    -p:Version=$Version -o $Out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The runtime the app was built against, taken from the .NET this build ran with: the version the app asks for (10.0 for net10.0), its newest patch that
# both parts (Microsoft.NETCore.App, and Microsoft.WindowsDesktop.App for WPF and the tray icon) have, and the host that starts them.
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$config = Get-Content (Join-Path $Out 'AtreiaApp.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @($config.runtimeOptions.frameworks) + @($config.runtimeOptions.framework) | Where-Object { $_ }
if (-not $frameworks) { throw 'AtreiaApp.runtimeconfig.json names no framework: was the app published self-contained?' }
$wanted = [version]($frameworks | Select-Object -First 1).version
$names = @('Microsoft.NETCore.App') + @($frameworks | ForEach-Object { $_.name }) | Select-Object -Unique

function Get-Versions([string]$dir) {
    if (-not (Test-Path $dir)) { return @() }
    Get-ChildItem $dir -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | ForEach-Object { [version]$_.Name } |
        Where-Object { $_.Major -eq $wanted.Major -and $_.Minor -eq $wanted.Minor -and $_ -ge $wanted }
}

$common = $null
foreach ($name in $names) {
    $have = @(Get-Versions (Join-Path $dotnetRoot "shared/$name"))
    $common = if ($null -eq $common) { $have } else { @($common | Where-Object { $have -contains $_ }) }
}
if (-not $common) { throw "No $($wanted.Major).$($wanted.Minor) runtime with all of $($names -join ', ') under $dotnetRoot" }
$runtimeVersion = ($common | Sort-Object -Descending | Select-Object -First 1).ToString()

$fxrVersions = @(Get-ChildItem (Join-Path $dotnetRoot 'host/fxr') -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | ForEach-Object { [version]$_.Name })
$fxr = if ($fxrVersions -contains [version]$runtimeVersion) { $runtimeVersion } else { ($fxrVersions | Sort-Object -Descending | Select-Object -First 1).ToString() }

$runtime = Join-Path $Out 'runtime'
New-Item -ItemType Directory -Force (Join-Path $runtime 'host/fxr') | Out-Null
Copy-Item (Join-Path $dotnetRoot "host/fxr/$fxr") (Join-Path $runtime "host/fxr/$fxr") -Recurse
foreach ($name in $names) {
    New-Item -ItemType Directory -Force (Join-Path $runtime "shared/$name") | Out-Null
    Copy-Item (Join-Path $dotnetRoot "shared/$name/$runtimeVersion") (Join-Path $runtime "shared/$name/$runtimeVersion") -Recurse
}
foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $file = Join-Path $dotnetRoot $notice
    if (Test-Path $file) { Copy-Item $file (Join-Path $runtime $notice) }
}

"Published $Version to $Out with .NET $runtimeVersion (host $fxr) in runtime\"
Get-ChildItem $Out | Sort-Object { -not $_.PSIsContainer }, Name | ForEach-Object { if ($_.PSIsContainer) { "  $($_.Name)\" } else { "  $($_.Name)" } }
