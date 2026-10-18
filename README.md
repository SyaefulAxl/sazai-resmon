# Resource Monitoring (dulu: sazai-speedmonitoring)

Dashboard pemantau laptop **native Windows 11** (C# WinForms, .NET Framework bawaan Windows — tanpa SDK/instalasi apa pun). Dua bahasa: **Bahasa Indonesia** (default) dan **English** (klik kanan ikon tray → Switch to English). Identitas lama `LaptopDashboard`/`sazai-speedmonitoring` diganti menjadi `ResourceMonitoring`/`resourcemonitoring` (repo baru, riwayat fresh).

## Fitur

| Kartu | Isi |
|---|---|
| ⚡ Baterai | %, watt real-time (mW dari WMI `BatteryStatus`), laju, tegangan, energi mWh, kesehatan (design capacity via `powercfg /batteryreport`), estimasi penuh/menyala |
| 🌐 Internet | Pemakaian real-time ↓/↑ (WMI `MSFT_NetAdapterStatisticsSettingData`, 64-bit, hanya adapter fisik), total sesi & sejak nyala, puncak, grafik mini 3 menit, IP publik + ISP |
| 🌧 Cuaca | Open-Meteo + **lokasi berprioritas**: `location.txt` (manual) → **GPS/Windows Location perangkat** (dipakai bila wajar, <300 km dari IP) → **geolokasi IP** → default Sukajadi. GPS Windows bisa salah saat VPN aktif (kasus nyata: melapor Dhaka padahal IP Bekasi) — uji plausibilitas Haversine otomatis menolaknya dan memakai IP. Reverse-geocode nama kota via BigDataCloud (id/en). Cache `wx_cache.json`, refresh 5 mnt |
| 💻 Sistem | CPU (GetSystemTimes), RAM, uptime, disk C:, OS (registry build) |
| 🌡 SUHU | Suhu SEMUA sensor yang bisa dibaca: CPU (Package + per-core), Intel GPU (IGCL bila tersedia), SSD NVMe (komposit), papan/baterai bila tersedia + **rata-rata keseluruhan** + baris "Kipas: x RPM · Daya CPU: x W" |
| 💾 Penyimpanan | Semua drive + bar pemakaian berwarna, aktivitas disk real-time (perf counter) |
| 🎮 GPU·NPU·CPU | Win32_VideoController + VRAM registry, NPU via `PNPClass='ComputeAccelerator'`, CPU cores/threads |

## 4 mode tampilan (mode.txt)

- **taskbar** — widget DI DALAM taskbar bawaan (teknik TrafficMonitor: `WS_CHILD`+`SetParent` ke `Shell_TrayWnd`, render `UpdateLayeredWindowIndirect` alpha per-piksel) — menampilkan ↓/↑ internet, % baterai + watt, dan suhu rata-rata
- **tray** — ikon area notifikasi (flyout ringkasan)
- **bar** — bar ramping di atas taskbar
- **full** — dashboard jendela (auto-fit)

## Bahasa

- Disimpan di `lang.txt` (`id`/`en`); default `id`.
- Ganti: klik kanan **ikon tray** → "Switch to English" / "Ganti ke Bahasa Indonesia" — UI dibangun ulang seketika.

## Build

```powershell
.\build.ps1   # csc .NET Framework bawaan + embed app.manifest -> ResourceMonitoring.exe
```

**`app.manifest` WAJIB** — tanpa deklarasi `supportedOS` Win8+, Windows menolak layered child window (err 87) → widget taskbar tidak pernah tampil.

## Install

- **EXE installer** (`ResourceMonitoring-Setup.exe`, Inno Setup, per-user tanpa admin, pilihan "Jalankan saat Windows nyala") — lihat GitHub Releases.
- **ZIP portable** — ekstrak, jalankan `ResourceMonitoring.exe`.
- (Tidak ada MSI: pembuat MSI butuh WiX/SDK; installer EXE Inno sudah setara untuk pengguna umum.)

## Kartu SUHU & kolektor

- WMI standar di kebanyakan laptop modern tidak mengekspos suhu: `MSAcpi_ThermalZoneTemperature` di-tolak (Access denied) untuk proses biasa dan bernilai 0 walau admin; `MSFT_StorageReliabilityCounter` ditolak; `BatteryStatus.Temperature` sering kosong. Solusi: **kolektor Administrator** (exe yang sama, argumen `--tempsvc`) membaca semua sensor via **LibreHardwareMonitorLib** (ring-0, + **PawnIO**; IGCL untuk Intel GPU) lalu menulis `temps.json` tiap 5 detik; dashboard (proses biasa) hanya membaca file itu.
- Task terjadwal **ResourceMonitoring-TempSvc** (saat logon, hak tertinggi, tanpa batas eksekusi, boleh baterai) + mutex satu-instans; berhenti sendiri bila dashboard tidak ada 90 detik atau ada file `temps.stop`.
- Dashboard otomatis menyalakan kolektor via task bila `temps.json` usang (tanpa UAC tambahan).
- **PawnIO** (https://github.com/namazso/PawnIO.Setup) harus terpasang sekali — tanpa itu suhu CPU/GPU tidak muncul.
- Laptop yang diuji: Lenovo BEE (Core Ultra 9 285H, 16C/16T, Intel Arc 140T 16GB, WD SN7100S NVMe). RPM kipas tidak terekspos (fan via EC) — baris kipas tetap disiapkan untuk mesin lain.

## Pelajaran penting (riwayat bug, tetap berlaku)

1. **Baterai 0 W**: driver laptop melapor properti legacy `ChargeRate` (mW) — bukan `ChargeRateInMilliwatts`; `BatteryStaticData` gagal → design capacity via powercfg XML.
2. **Bocor handle**: semua query WMI lewat `Util.WmiRows()` (disposal penuh); scope dipakai ulang, disk lewat `PerformanceCounter`.
3. **Err 87 layered**: app.manifest (supportedOS) WAJIB.
4. **Widget "loncat"**: lebar kolom SELALU dari templat tetap; tepi kanan di-cache 10 dtk.
5. **DPI 200%**: font piksel `Util.S(pt)*4/3`; `Form.Scale()` dilarang.
6. **Compiler csc v4 = C# 5**: dilarang `out var` inline, `is X t`, `?.`, string interpolation, tuple.
7. **i18n**: teks hanya lewat `Lang.T("kunci")` — jangan hardcode string UI baru.

## File

- `Dashboard.cs` — seluruh sumber (±2600 baris, termasuk class Lang)
- `app.manifest` — supportedOS + dpiAware (WAJIB)
- `build.ps1`, `screenshot.ps1`, `ResourceMonitoring.exe` (build terakhir)
- `LibreHardwareMonitorLib.dll` + dependensi (`DiskInfoToolkit.dll`, `RAMSPDToolkit-NDD.dll`, `HidSharp.dll`, `BlackSharp.Core.dll`, keluarga `System.*`)
- Runtime (dibuat otomatis, tidak di-commit): `mode.txt`, `lang.txt`, `wx_cache.json`, `dbg.log`, `temps.json`, `temps.stop`
