# Resource Monitoring — Brand Identity Assets (`assets/`)

## Bahasa Indonesia

### Daftar Berkas
- `rm-icon.svg` — Ikon vektor utama (512×512, sudut membulat ~22%).
- `rm-icon-16.png` s/d `rm-icon-512.png` — Ekspor PNG raster (`16`, `32`, `48`, `64`, `128`, `256`, `512` px).
- `rm-icon.ico` — Ikon Windows multi-ukuran (`16`, `32`, `48`, `64`, `128`, `256` px, format Vista+ PNG).
- `rm-logo.svg` — Logo horizontal (*icon mark* + tipografi `RESOURCE MONITORING` + subjudul fitur).
- `rm-avatar.png` — Avatar repositori GitHub (512×512 persegi penuh).
- `preview.png` — Lembar tinjauan skala ikon pada permukaan gelap/terang beserta *lockup* logo.
- `make-icons.ps1` & `make-ico.py` — Skrip generator deterministik (GDI+ & Python stdlib).

### Cara Meregenerasi Aset
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\assets\make-icons.ps1
python .\assets\make-ico.py
```

### Rasional Desain
- **Palet Otentik dari `Dashboard.cs`**: Menggunakan warna asli UI aplikasi—latar utama `#181822` (`24,24,34`), permukaan dalam `#202230` (`32,34,48`), kartu `#2C2F3E` (`44,47,62`), bingkai `#484E64` (`72,78,100`), serta empat aksen indikator: `#6EBEFF` (*CAccent*), `#73DEA0` (*CGood*), `#FFB05C` (*CWarn*), dan `#FF7676` (*CBad*).
- **Motif Termometer + Busur Gauge**: Menggabungkan busur pengukur 3-zona (hijau, biru, oranye) dengan jarum penunjuk biru serta termometer vertikal putih-merah di tengah untuk melambangkan pemantauan suhu perangkat keras, daya baterai (watt), jaringan, dan beban CPU/GPU/NPU.
- **Penyederhanaan Geometris di Ukuran Kecil**: Pada ukuran 16px dan 32px, garis derajat kecil dan celah tipis dihilangkan serta ketebalan busur dipertebal agar siluet tetap tajam di *system tray* dan *taskbar* Windows 11.
- **Keluarga Visual SAZAI**: Proporsi *squircle* (radius ~22%), ketebalan garis, dan fondasi *dark navy* diselaraskan langsung dengan aplikasi pendamping **SAZAI Remote**.

---

## English

### File List
- `rm-icon.svg` — Master vector app icon (512×512, ~22% rounded square).
- `rm-icon-16.png` .. `rm-icon-512.png` — Raster PNG exports (`16`, `32`, `48`, `64`, `128`, `256`, `512` px).
- `rm-icon.ico` — Multi-resolution Windows icon (`16`, `32`, `48`, `64`, `128`, `256` px, Vista+ PNG-compressed).
- `rm-logo.svg` — Horizontal brand lockup (icon mark + `RESOURCE MONITORING` wordmark + subtitle).
- `rm-avatar.png` — 512×512 full-bleed square GitHub repository avatar.
- `preview.png` — Multi-scale visual review sheet across dark/light surfaces plus horizontal lockup.
- `make-icons.ps1` & `make-ico.py` — Deterministic asset generation scripts.

### How to Regenerate
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\assets\make-icons.ps1
python .\assets\make-ico.py
```

### Design Rationale
- **Exact Source Palette (`Dashboard.cs`)**: Built strictly from the app's native WinForms constants—dark navy base `#181822`, panel surface `#202230`, card fill `#2C2F3E`, border `#484E64`, and status accents `#6EBEFF` (*CAccent*), `#73DEA0` (*CGood*), `#FFB05C` (*CWarn*), and `#FF7676` (*CBad*).
- **Thermometer + Circular Gauge Fusion**: Pairs a three-zone resource gauge arc and active pointer with a bold central thermometer, directly expressing laptop thermal sensors, battery wattage, and real-time system telemetry.
- **Optical Scaling for 16px/32px**: Fine degree ticks and recessed inner channels are progressively simplified at small sizes while stroke weights are boosted for crisp legibility in the Windows 11 system tray and taskbar.
- **Unified SAZAI Suite Identity**: Shares the exact squircle geometry, dark navy surface hierarchy, and typographic lockup system with **SAZAI Remote**.
