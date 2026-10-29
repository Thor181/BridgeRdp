# Регистрирует DVC-плагин для mstsc текущего пользователя (HKCU, права администратора не нужны).
# Использование: .\scripts\register-client.ps1 [-Configuration Release] [-DllPath C:\path\BridgeRdp.Client.comhost.dll]
param(
    [string]$Configuration = "Debug",
    [string]$DllPath
)

$ErrorActionPreference = "Stop"

$clsid = "{81E782FA-3E1F-4574-9FB2-B689AFCA5BB1}"
$addinName = "BridgeRdp"

if (-not $DllPath) {
    $packagedDll = Join-Path $PSScriptRoot "BridgeRdp.Client.comhost.dll"
    if (Test-Path -LiteralPath $packagedDll) {
        $DllPath = $packagedDll
    } else {
        $DllPath = Join-Path $PSScriptRoot "..\src\BridgeRdp.Client\bin\$Configuration\net10.0-windows\BridgeRdp.Client.comhost.dll"
    }
}
$DllPath = (Resolve-Path $DllPath).Path

$clsidKey = "HKCU:\Software\Classes\CLSID\$clsid"
New-Item -Path $clsidKey -Value "BridgeRdp DVC Plugin" -Force | Out-Null
New-Item -Path "$clsidKey\InprocServer32" -Value $DllPath -Force | Out-Null
New-ItemProperty -Path "$clsidKey\InprocServer32" -Name "ThreadingModel" -Value "Both" -Force | Out-Null

$addinKey = "HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\$addinName"
New-Item -Path $addinKey -Force | Out-Null
New-ItemProperty -Path $addinKey -Name "Name" -Value $clsid -Force | Out-Null

Write-Host "Registered $clsid -> $DllPath"
Write-Host "Restart mstsc.exe to load the plugin."
