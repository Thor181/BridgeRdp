# Удаляет регистрацию DVC-плагина, сделанную register-client.ps1.
$ErrorActionPreference = "Stop"

$clsid = "{81E782FA-3E1F-4574-9FB2-B689AFCA5BB1}"
$addinName = "BridgeRdp"

Remove-Item -Path "HKCU:\Software\Classes\CLSID\$clsid" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\$addinName" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Unregistered $clsid"
