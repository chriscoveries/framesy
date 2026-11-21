#Requires -Version 5.1
<#
.SYNOPSIS
Builds the Windows x64 Framesy ZIP and optionally includes verified companion tools.
.EXAMPLE
.\Pack-Framesy.ps1 -IncludeCompanionTools
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory=(Join-Path $PSScriptRoot 'dist'),
    [switch]$IncludeCompanionTools
)
$ErrorActionPreference='Stop'
$output=(New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$stage=(New-Item -ItemType Directory -Path (Join-Path $output ('stage-'+[guid]::NewGuid()))).FullName
$notices=(New-Item -ItemType Directory -Path (Join-Path $stage 'Licenses')).FullName
$dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source

function Save-UpstreamFile([string]$Url,[string]$Destination,[string]$Sha256) {
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 75
    if ($Sha256) {
        $actual=(Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        if($actual -ne $Sha256) { throw "Checksum mismatch for $(Split-Path -Leaf $Destination)." }
    }
}

& $dotnet publish (Join-Path $PSScriptRoot 'Framesy.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Framesy publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'),(Join-Path $PSScriptRoot 'Manage-CpuBoost.ps1') -Destination $stage

$licenses=[ordered]@{
    'TraceEvent.txt'='https://raw.githubusercontent.com/microsoft/perfview/v3.2.8/LICENSE.TXT'
    'Vortice.Windows.txt'='https://raw.githubusercontent.com/amerkoleci/Vortice.Windows/cd916a03f206165bec67982ed501e88820d4182b/LICENSE'
    'Vortice.Mathematics.txt'='https://raw.githubusercontent.com/amerkoleci/Vortice.Mathematics/f712049b1c3cd1671240af6a8ae22a635c4c34dd/LICENSE'
    'SharpGenTools.txt'='https://raw.githubusercontent.com/SharpGenTools/SharpGenTools/a22348d2e1ff76dfbdc51d68800ed31e991d8b32/LICENSE.txt'
    'DotNet-Runtime.txt'='https://raw.githubusercontent.com/dotnet/runtime/aae90fa09086a9be09dac83fa66542232c7269d8/LICENSE.TXT'
    'DotNet-Diagnostics.txt'='https://raw.githubusercontent.com/dotnet/diagnostics/8c08c89a0643d31db91e119b1adb463be3e0ffe5/LICENSE.TXT'
}
$archiveName='Framesy-win-x64.zip'
if($IncludeCompanionTools) {
    $tools=(New-Item -ItemType Directory -Path (Join-Path $stage 'Tools')).FullName
    $green='greencurve-0.31.0-windows-x64-setup.exe'
    $pmon='PresentMon-2.6.0-x64.exe'
    Save-UpstreamFile "https://github.com/aufkrawall/green-curve/releases/download/0.31.0/$green" (Join-Path $tools $green) '2bd6b0f09eef04f533ce1a28559a782335d16ff507773fcf421b59bbf076961b'
    Save-UpstreamFile "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/$pmon" (Join-Path $tools $pmon) 'b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af'
    $licenses['GreenCurve.txt']='https://raw.githubusercontent.com/aufkrawall/green-curve/0.31.0/LICENSE'
    $licenses['PresentMon.txt']='https://raw.githubusercontent.com/GameTechDev/PresentMon/v2.6.0/LICENSE.txt'
    $archiveName='Framesy-toolkit-win-x64.zip'
}
foreach($entry in $licenses.GetEnumerator()) {
    Save-UpstreamFile $entry.Value (Join-Path $notices $entry.Key)
}
$zip=Join-Path $output $archiveName
$temporary=Join-Path $output ("build-{0}.zip" -f [guid]::NewGuid())
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $temporary
Move-Item -LiteralPath $temporary -Destination $zip -Force
Get-Item -LiteralPath $zip
