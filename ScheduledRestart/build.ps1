# Builds ScheduledRestart.dll with Roslyn csc + the game/RocketMod/MySQL/Harmony DLLs directly.
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Build on your Windows dev machine (where Unturned + RocketMod are installed), then upload
# bin\ScheduledRestart.dll to the server's Rocket\Plugins\ folder (RestoreMonarchy panel etc.).
$ErrorActionPreference = "Stop"
$proj = $PSScriptRoot
$csc = $null
foreach ($root in @(
    "C:\Program Files\Microsoft Visual Studio\2022\Preview",
    "C:\Program Files\Microsoft Visual Studio\2022\Community",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise")) {
    $hit = Join-Path $root "MSBuild\Current\Bin\Roslyn\csc.exe"
    if (Test-Path $hit) { $csc = $hit; break }
}
if (-not $csc) { throw "Roslyn csc.exe not found." }

# Adjust these if your Unturned install lives elsewhere.
$managed = "D:\SteamLibrary\steamapps\common\Unturned\Unturned_Data\Managed"
$rocket  = "D:\SteamLibrary\steamapps\common\Unturned\Extras\Rocket.Unturned"
$fx      = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

$refs = @(
    "$managed\Assembly-CSharp.dll", "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll",
    "$managed\com.rlabrecque.steamworks.net.dll", "$managed\netstandard.dll",
    "$rocket\Rocket.API.dll", "$rocket\Rocket.Core.dll", "$rocket\Rocket.Unturned.dll",
    "$proj\lib\0Harmony.dll",
    "$proj\lib\MySql.Data.dll",
    "$fx\System.dll", "$fx\System.Core.dll", "$fx\System.Xml.dll", "$fx\System.Data.dll"
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { Write-Warning "missing reference: $r" } }

New-Item -ItemType Directory -Force "$proj\bin" | Out-Null
$srcs = Get-ChildItem $proj -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName
$cscArgs = @("/nologo", "/target:library", "/langversion:latest", "/optimize+",
    "/out:$proj\bin\ScheduledRestart.dll") + ($refs | ForEach-Object { "/reference:$_" }) + $srcs
& $csc $cscArgs
if ($LASTEXITCODE -eq 0) { Write-Host "OK -> $proj\bin\ScheduledRestart.dll" -ForegroundColor Green }
else { throw "Build failed ($LASTEXITCODE)" }
