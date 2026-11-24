<#
.SYNOPSIS
  Installs the WakaTime VSIX into SQL Server Management Studio 21/22.
.EXAMPLE
  .\Install-SSMS22.ps1 -Vsix .\WakaTime.vsix
.EXAMPLE
  .\Install-SSMS22.ps1 -Vsix .\WakaTime.vsix -Manual   # copy into Extensions + /updateconfiguration (needs admin)
#>
param(
    [Parameter(Mandatory = $true)] [string] $Vsix,
    [string] $SsmsIde = "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE",
    [switch] $Manual
)
$ErrorActionPreference = 'Stop'
$Vsix = (Resolve-Path $Vsix).Path
$ssmsExe = Join-Path $SsmsIde 'Ssms.exe'
if (-not (Test-Path $ssmsExe)) { throw "Ssms.exe not found in '$SsmsIde'. Pass -SsmsIde <install>\Common7\IDE." }
if (Get-Process Ssms -ErrorAction SilentlyContinue) { throw 'Close SSMS first.' }

if ($Manual) {
    $target = Join-Path $SsmsIde 'Extensions\WakaTime'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    $zip = Join-Path $env:TEMP 'WakaTime.vsix.zip'
    Copy-Item $Vsix $zip -Force
    Expand-Archive $zip -DestinationPath $target -Force
    Remove-Item $zip
    & $ssmsExe /updateconfiguration | Out-Null
    Write-Host "Copied to $target and refreshed SSMS configuration."
} else {
    $version = (Get-Item $ssmsExe).VersionInfo.ProductVersion
    $installer = Join-Path $SsmsIde 'VSIXInstaller.exe'
    $p = Start-Process $installer -ArgumentList @('/skuName:ssms', "/skuVersion:$version", "`"$Vsix`"") -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "VSIXInstaller exited with $($p.ExitCode). Logs: $env:TEMP\dd_VSIXInstaller_*.log" }
    Write-Host "Installed into SSMS $version."
}
Write-Host 'Start SSMS; WakaTime is under Tools > WakaTime.'
