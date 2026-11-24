<#
.SYNOPSIS
  Installs (or reinstalls) the WakaTime VSIX into SQL Server Management Studio 21/22.
.EXAMPLE
  .\Install-SSMS22.ps1 -Vsix .\WakaTime.vsix
.EXAMPLE
  .\Install-SSMS22.ps1 -Uninstall
.EXAMPLE
  .\Install-SSMS22.ps1 -Vsix .\WakaTime.vsix -Manual   # copy into Extensions + /updateconfiguration (run as admin)
#>
param(
    [string] $Vsix,
    [string] $SsmsIde = "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE",
    [switch] $Manual,
    [switch] $Uninstall
)
$ErrorActionPreference = 'Stop'
$ExtensionId = '52d9c3ff-c893-408e-95e4-d7484ec7fa47'

$ssmsExe = Join-Path $SsmsIde 'Ssms.exe'
$installer = Join-Path $SsmsIde 'VSIXInstaller.exe'
if (-not (Test-Path $ssmsExe)) { throw "Ssms.exe not found in '$SsmsIde'. Pass -SsmsIde <install>\Common7\IDE." }
if (Get-Process Ssms -ErrorAction SilentlyContinue) { throw 'Close SSMS first.' }
$version = (Get-Item $ssmsExe).VersionInfo.ProductVersion
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

function Remove-ManualCopies {
    # Earlier manual installs (any folder under Extensions holding our package id) block
    # VSIXInstaller with "already installed to all applicable products".
    $found = Get-ChildItem (Join-Path $SsmsIde 'Extensions') -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $m = Join-Path $_.FullName 'extension.vsixmanifest'
            (Test-Path $m) -and (Select-String -Path $m -Pattern $ExtensionId -Quiet)
        }
    foreach ($dir in $found) {
        if (-not $isAdmin) { throw "Old manual install found in '$($dir.FullName)'. Re-run this script as administrator to remove it." }
        Write-Host "Removing manual install $($dir.FullName)"
        Remove-Item $dir.FullName -Recurse -Force
    }
    return [bool]$found
}

function Uninstall-Vsix {
    Write-Host "Uninstalling any VSIX-installed WakaTime from SSMS $version..."
    $p = Start-Process $installer -ArgumentList @('/quiet', '/skuName:ssms', "/skuVersion:$version", "/uninstall:$ExtensionId") -Wait -PassThru
    # Non-zero simply means it was not installed that way; that's fine.
    Write-Host "  VSIXInstaller /uninstall exit code $($p.ExitCode)"
}

$removedManual = Remove-ManualCopies
Uninstall-Vsix
if ($Uninstall) {
    & $ssmsExe /updateconfiguration | Out-Null
    Write-Host 'WakaTime removed.'
    return
}

if (-not $Vsix) { throw 'Pass -Vsix <path to WakaTime.vsix>.' }
$Vsix = (Resolve-Path $Vsix).Path

if ($Manual) {
    if (-not $isAdmin) { throw '-Manual copies into Program Files; run as administrator.' }
    $target = Join-Path $SsmsIde 'Extensions\WakaTime'
    $zip = Join-Path $env:TEMP 'WakaTime.vsix.zip'
    Copy-Item $Vsix $zip -Force
    Expand-Archive $zip -DestinationPath $target -Force
    Remove-Item $zip
    Write-Host "Copied to $target"
} else {
    $p = Start-Process $installer -ArgumentList @('/skuName:ssms', "/skuVersion:$version", "`"$Vsix`"") -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "VSIXInstaller exited with $($p.ExitCode). Logs: $env:TEMP\dd_VSIXInstaller_*.log" }
    Write-Host "Installed into SSMS $version."
}

# Rebuild SSMS's package/menu cache so the (re)installed package is picked up.
& $ssmsExe /updateconfiguration | Out-Null
Write-Host 'Start SSMS; WakaTime is under Tools > WakaTime.'
