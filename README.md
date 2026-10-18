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

## Pembaruan 2026-10-18 (v1.2.0 — Proses Teratas, peringatan, siklus baterai, CPU di widget)

- **PROSES TERATAS** di flyout tray (dan proses #1 CPU di tooltip tray): sampling delta CPU per nama proses + agregat RAM tiap ~3 detik — langsung menjawab "apa yang membuat laptop panas/berat". (Verifikasi log: `OneDrive 22.6% · Code 8.6% · node 5.5% | chrome 5.0 GB · Code 4.1 GB · node 1.9 GB`.)
- **Peringatan balloon tray**: suhu rata-rata ≥85°C (jeda 10 mnt), RAM ≥90% (jeda 10 mnt), baterai ≤15% saat tidak mengisi (jeda 30 mnt) — teks dua bahasa.
- **CPU di widget taskbar**: kolom suhu kini dua baris — suhu (baris 1) + `CPU xx%` (baris 2, warna berubah ≥50% oranye, ≥80% merah).
- **Siklus baterai** dari `powercfg /batteryreport /XML` (CycleCount) digabung ke baris Kesehatan: "Kesehatan: 97.3% (desain 84,000 mWh) · 213 siklus".
- **Flyout diperbesar** (560 px) dan ditata ulang — seksi SUHU + PROSES TERATAS di atas tombol (sebelumnya ada tumpang-tindih LAd/tombol dari seksi lama).
- Versi: exe/installer/manifest → 1.2.0.

## Pembaruan 2026-10-18 (v1.3.0 — Riwayat: grafik suhu 60 menit, CSV per bulan, log alert)

- **Grafik suhu 60 menit terakhir** di flyout (sparkline; sampel tiap 30 detik, buffer 120 titik) — melihat tren panas sekilas.
- **Perekaman CSV otomatis**: `history\history-YYYY-MM.csv` (satu baris per menit): `waktu;temp_avg;temp_min;temp_max;cpu_pct;ram_pct;batt_pct;batt_w;top_cpu` — bisa dibuka kapan saja di Excel/Notepad (pemisah `;`, locale Indonesia).
- **Log peringatan**: `history\alerts.log` mencatat setiap balloon (suhu/RAM/baterai) dengan stempel waktu.
- **Menu tray "Buka riwayat (CSV)"** → membuka folder `history` langsung dari ikon tray.
- Versi 1.3.0 (exe/installer/manifest).

## Pembaruan 2026-10-18 (v1.3.1 — Penjaga zona waktu otomatis di kolektor)

- **Kolektor kini juga penjaga zona waktu**: tiap ~2 menit membaca zona dari registry (fresh — `TimeZoneInfo.Local` di-cache per-proses, pernah bikin tes gagal) dan menjalankan `tzutil /s` bila menyimpang dari target. Target default **SE Asia Standard Time (WIB)**; bisa diubah lewat `tz.txt` (satu baris berisi TimeZoneInfo ID). Kolektor sudah berjalan sebagai Administrator, jadi tidak perlu task/UAC tambahan.
- Terverifikasi dua arah di mesin ini: `TEMP-tz aktif, zona=Bangladesh...` → `TEMP-tz koreksi Bangladesh -> SE Asia` dalam 2 menit, dan sebaliknya saat target diubah.
- Konteks laptop ini: zona pernah ditarik ke Dhaka berulang oleh auto-timezone/lokasi (sisa cache VPN OpenVPN) — `tzautoupdate` + `lfsvc` sudah dimatikan; penjaga ini lapisan terakhir agar kasus serupa self-heal.
- Versi 1.3.1 (exe/installer/manifest).

## File

- `Dashboard.cs` — seluruh sumber (±2950 baris, termasuk class Lang)



- `app.manifest` — supportedOS + dpiAware (WAJIB)
- `build.ps1`, `screenshot.ps1`, `ResourceMonitoring.exe` (build terakhir)
- `LibreHardwareMonitorLib.dll` + dependensi (`DiskInfoToolkit.dll`, `RAMSPDToolkit-NDD.dll`, `HidSharp.dll`, `BlackSharp.Core.dll`, keluarga `System.*`)
- Runtime (dibuat otomatis, tidak di-commit): `mode.txt`, `lang.txt`, `wx_cache.json`, `dbg.log`, `temps.json`, `temps.stop`
