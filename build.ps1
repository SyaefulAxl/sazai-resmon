# Bangun ulang ResourceMonitoring.exe (butuh .NET Framework bawaan Windows, tanpa SDK)
# app.manifest WAJIB di-embed (kompatibilitas Windows 8+ untuk widget taskbar + DPI-aware).
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
& $csc /nologo /codepage:65001 /target:winexe /win32manifest:"$dir\app.manifest" /out:"$dir\ResourceMonitoring.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Management.dll /r:System.Web.Extensions.dll /r:System.Net.Http.dll `
    /r:"$dir\LibreHardwareMonitorLib.dll" `
    "$dir\Dashboard.cs"
if ($LASTEXITCODE -eq 0) { Write-Host "OK: $dir\ResourceMonitoring.exe" } else { Write-Host "GAGAL (kode $LASTEXITCODE)" }
