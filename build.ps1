# Bangun ulang ResourceMonitoring.exe (butuh .NET Framework bawaan Windows, tanpa SDK)
# app.manifest WAJIB di-embed (kompatibilitas Windows 8+ untuk widget taskbar + DPI-aware).
# GPS cuaca (Windows Location/WinRT) butuh metadata Windows.winmd dari Windows SDK 10 — bila
# tidak ada, kompilasi tetap jalan TANPA fitur GPS (fallback: location.txt -> IP -> Sukajadi).
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

$winmd = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.*\Windows.winmd" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$winrt = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll"
$facade = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll"

$a = New-Object System.Collections.ArrayList
[void]$a.AddRange(@("/nologo", "/codepage:65001", "/target:winexe", "/win32manifest:$dir\app.manifest",
    "/out:$dir\ResourceMonitoring.exe",
    "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
    "/r:System.Management.dll", "/r:System.Web.Extensions.dll", "/r:System.Net.Http.dll",
    "/r:$dir\LibreHardwareMonitorLib.dll"))
if ($winmd -and (Test-Path $winrt)) {
    [void]$a.Add("/r:$winmd"); [void]$a.Add("/r:$winrt"); [void]$a.Add("/r:$facade")
    Write-Host "GPS: WinRT diaktifkan ($winmd)"
} else {
    [void]$a.Add("/define:NO_WINRT")
    Write-Host "GPS: WinRT tidak tersedia -> build tanpa GPS (fallback IP)"
}
[void]$a.Add("$dir\Dashboard.cs")

& $csc $a.ToArray()
if ($LASTEXITCODE -eq 0) { Write-Host "OK: $dir\ResourceMonitoring.exe" } else { Write-Host "GAGAL (kode $LASTEXITCODE)" }
