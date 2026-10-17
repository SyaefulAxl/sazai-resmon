# AGENTS.md — resourcemonitoring

## Izin git (standing approval)

Pemilik repo (SyaefulAxl) memberi izin tetap: **agent boleh `git add` + `commit` + `push` langsung ke repo ini tanpa meminta konfirmasi per perubahan.**

Aturan main:
- Pesan commit deskriptif (apa & kenapa), satu commit per perubahan logis
- Jangan force-push ke `master`; jangan rewrite history yang sudah dipush
- File runtime tetap di-ignore (`mode.txt`, `lang.txt`, `wx_cache.json`, `dbg.log`, `temps.json`, `temps.stop`) — jangan commit
- Repo **private** — jangan pernah memasukkan secret/API key; redaksi sebelum commit
- `ResourceMonitoring.exe` ikut di-commit (biner build terakhir, tanpa SDK di mesin lain)
- Release GitHub = EXE installer (Inno Setup) + ZIP portable

## Konteks singkat

Dashboard laptop Windows 11 native (C# WinForms, .NET Framework bawaan, csc C#5).
Detail fitur, cara build, riwayat bug, dan aturan i18n ada di `README.md`.
Rebranding dari `sazai-speedmonitoring` (LaptopDashboard) → `resourcemonitoring` (ResourceMonitoring), 2026-10-18.
Salinan berjalan (runtime): `C:\Users\syaef\Tools\ResourceMonitoring` — repo ini sumber resminya.
