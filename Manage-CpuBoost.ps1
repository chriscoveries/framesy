#Requires -Version 5.1
<#
.SYNOPSIS
Reads or adjusts Windows CPU boost policy and the AC frequency cap.
.EXAMPLE
.\Manage-CpuBoost.ps1
.EXAMPLE
.\Manage-CpuBoost.ps1 -Boost Enabled -MaxMHz 4800
.EXAMPLE
.\Manage-CpuBoost.ps1 -RestoreFrom C:\path\to\backup.json
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Disabled','Enabled','Aggressive')][string]$Boost,
    [ValidateRange(0,64000)][int]$MaxMHz,
    [string]$RestoreFrom
)
$ErrorActionPreference='Stop'
$powercfg=Join-Path $env:SystemRoot 'System32/powercfg.exe'
if (-not ('Framesy.PowerPolicy' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Framesy {
    public static class PowerPolicy {
        [DllImport("powrprof.dll")]
        public static extern uint PowerReadACValueIndex(IntPtr key, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    }
}
"@
}
$processor=[guid]'54533251-82be-4824-96c1-47b60b740d00'
$settingIds=[ordered]@{
    PERFBOOSTMODE=[guid]'be337238-0d82-4146-a960-4f3749d470c7'
    PROCFREQMAX=[guid]'75b0ae3f-bce0-45a7-8c89-c9611c25e100'
    PROCFREQMAX1=[guid]'75b0ae3f-bce0-45a7-8c89-c9611c25e101'
}
$modes=[ordered]@{Disabled=0; Enabled=1; Aggressive=2}
function Read-ActivePlan {
    $text=& $powercfg /getactivescheme
    if($LASTEXITCODE -ne 0 -or "$text" -notmatch '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}') {
        throw 'Windows did not report an active power plan.'
    }
    return [guid]$Matches[0]
}
function Read-Settings([guid]$Plan) {
    $values=[ordered]@{}
    foreach($name in $settingIds.Keys) {
        $setting=$settingIds[$name]
        [uint32]$value=0
        $code=[Framesy.PowerPolicy]::PowerReadACValueIndex([IntPtr]::Zero,[ref]$Plan,[ref]$processor,[ref]$setting,[ref]$value)
        if($code -eq 0) { $values[$name]=$value }
        elseif($name -ne 'PROCFREQMAX1' -or $code -notin 2,1168) {
            throw "Reading $name failed with Windows error $code."
        }
    }
    return $values
}
function Set-Index([guid]$Plan,[string]$Name,[uint32]$Value) {
    & $powercfg /setacvalueindex $Plan $processor $settingIds[$Name] $Value
    if($LASTEXITCODE -ne 0) { throw "Writing $Name failed. Try an administrator PowerShell." }
}
function Refresh-Plan([guid]$Plan) {
    if((Read-ActivePlan) -ne $Plan) { throw 'The active plan changed during the operation.' }
    & $powercfg /setactive $Plan
    if($LASTEXITCODE -ne 0) { throw 'Windows could not reapply the power plan.' }
}
$plan=Read-ActivePlan
$current=Read-Settings $plan
$desired=[ordered]@{}
if($RestoreFrom) {
    if($Boost -or $PSBoundParameters.ContainsKey('MaxMHz')) { throw 'Restore cannot be combined with setting values.' }
    $saved=Get-Content -LiteralPath $RestoreFrom -Raw | ConvertFrom-Json
    if($saved.Schema -ne 1 -or [guid]$saved.Plan -ne $plan) {
        throw 'The snapshot must be for the currently active plan.'
    }
    foreach($item in $saved.Settings.PSObject.Properties) {
        if(!$current.Contains($item.Name)) { throw "Snapshot setting $($item.Name) is unavailable." }
        $desired[$item.Name]=[uint32]$item.Value
    }
} else {
    if($Boost) { $desired['PERFBOOSTMODE']=$modes[$Boost] }
    if($PSBoundParameters.ContainsKey('MaxMHz')) {
        $desired['PROCFREQMAX']=$MaxMHz
        if($current.Contains('PROCFREQMAX1')) { $desired['PROCFREQMAX1']=$MaxMHz }
    }
}
$changes=@($desired.Keys | Where-Object {$desired[$_] -ne $current[$_]})
$backup=$null
if($changes.Count -gt 0 -and $PSCmdlet.ShouldProcess($plan,'Update CPU boost policy on AC power')) {
    $folder=New-Item -ItemType Directory -Path (Join-Path $env:LOCALAPPDATA 'Framesy/Power') -Force
    $backup=Join-Path $folder.FullName ('cpu-boost-{0}.json' -f [guid]::NewGuid())
    [ordered]@{Schema=1;Plan="$plan";Settings=$current} | ConvertTo-Json | Set-Content -LiteralPath $backup -Encoding utf8
    $written=[System.Collections.Generic.List[string]]::new()
    try {
        foreach($name in $changes) {
            Set-Index $plan $name $desired[$name]
            $written.Add($name)
        }
        Refresh-Plan $plan
        $result=Read-Settings $plan
        foreach($name in $changes) {
            if($result[$name] -ne $desired[$name]) { throw "Read-back of $name differs from the requested value." }
        }
        $current=$result
    } catch {
        $cause=$_
        try {
            foreach($name in $written) { Set-Index $plan $name $current[$name] }
            if($written.Count -gt 0 -and (Read-ActivePlan) -eq $plan) { Refresh-Plan $plan }
        } catch {
            Write-Warning "Automatic rollback was incomplete. Restore using $backup."
        }
        throw $cause
    }
}
$modeName=$modes.Keys | Where-Object {$modes[$_] -eq $current.PERFBOOSTMODE}
[pscustomobject]@{
    Plan=$plan
    BoostMode=if($modeName){$modeName}else{$current.PERFBOOSTMODE}
    MaxMHz=$current.PROCFREQMAX
    MaxMHzClass1=$current.PROCFREQMAX1
    Backup=$backup
}
