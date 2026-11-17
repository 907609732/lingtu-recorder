# Lingtu Unpack Recorder

[简体中文](README.md) | **English**

<p align="center">
  <img src="docs/assets/logo.png" width="180" alt="Lingtu Unpack Recorder logo">
</p>

Lingtu Unpack Recorder is a free, open-source Windows system for recording parcel unpacking and packing, working with an Android phone, and synchronizing data. It does not modify HIK SCAN or contain Hikvision source code, trademarks, or proprietary assets.

**Quick links:** [Download and install](#download-and-install) · [Screenshots](#screenshots) · [Feature overview](#feature-overview) · [Step-by-step guide (English)](docs/user-guide.en.md) · [Toolkit](#lingtu-e-commerce-toolkit) · [FAQ](docs/user-guide.en.md#20-faq) · [Privacy and data](#privacy-and-data) · [Data locations](#data-locations) · [Safe uninstall](#safe-uninstall) · [Build from source](#build-test-and-release) · [License](#license-and-independent-implementation)

[Website](https://record.lttlt.top) · [Brand upgrade and data retention](docs/operations/brand-upgrade.md) (Chinese) · [2.6.3 prerelease notes](docs/releases/2.6.3.md) (Chinese)

Version 2.6.3 retains the existing interface, layout, and workflows while updating the brand name and icons and adding help links. **The desktop interface is currently Chinese only**; the menu names below are given in Chinese with an English explanation. People installing the release package do not need the .NET SDK or any build tooling; building from source is for developers only.

## Screenshots

![Lingtu Unpack Recorder 2.6.3 main window (isolated render)](docs/assets/desktop-2.6.3.png)

Figure: the Lingtu Unpack Recorder 2.6.3 main window as an **isolated render**, produced by the project's isolated WPF rendering test. The test connected no camera and loaded no personal settings or business data, and the image contains no real tracking numbers or workbooks. It documents layout only; it is **not** evidence of actual recording quality, hardware compatibility, or production acceptance.

Layout, top to bottom: the "拆包录像 / 条码设计 / 系统设置" (recording / label designer / settings) page switch, video source, view layout (1/2/3/4/8/16) and preview controls; the live preview with snapshot, gallery, rotate, mirror and full-screen buttons; the "拆包 / 打包" (unpacking / packing) mode switch, label zoom, voice toggle, scan-to-start panel, and the "最近记录" (recent records) list.

## Download and install

- [Windows installer](https://github.com/907609732/lingtu-recorder/releases/download/v2.6.3/EcommerceUnpackRecorder-win-Setup.exe)
- [Android app](https://github.com/907609732/lingtu-recorder/releases/download/v2.6.3/EcommerceUnpackRecorder-Android.apk)
- [Releases and release notes](https://github.com/907609732/lingtu-recorder/releases)
- [Source repository](https://github.com/907609732/lingtu-recorder)

The Windows app uses a per-user Velopack installer. Updates download in the background and install after recording is idle and the user confirms. The installer does not yet have a trusted code-signing certificate and is distributed as a prerelease; Windows SmartScreen may show a warning. The release page provides SHA256 checksums; GitHub Actions builds also provide provenance where described by that release and its `build-info.json`.

Three steps for ordinary users:

1. Download `EcommerceUnpackRecorder-win-Setup.exe` from the release page and compare its SHA256 with the value published for that release.
2. Run the installer and complete the per-user installation; if SmartScreen appears, first confirm the download really came from this project's release page.
3. Launch the app from the Start menu and pick a recording folder in the first-run wizard. Excel can be skipped.

Full steps for download, verification, first-run setup, scanning, phone pairing, label design, compatibility sync, migration, and uninstall are in the **[user guide (English)](docs/user-guide.en.md)**.

## Lingtu E-commerce Toolkit

- **This project: Lingtu Unpack Recorder** — Windows unpacking/packing recording and record management.
- [E-commerce Purchase Order Export](https://github.com/907609732/lingtu-order-export/blob/master/docs/README.en.md) — buyer purchase order organization and Excel/CSV export.
- [Lingtu AI E-commerce Customer Service](https://github.com/907609732/LTTLT-AI-Customer-Service) — store knowledge management and AI-assisted customer reception (Chinese only).

These tools can be used as needed; see each project's documentation for features, versions, and licenses.

## Feature overview

- **Recording:** live preview, scan to start, scan the same number again to finish, scan a new number to switch parcels, manual stop, duplicate-number badges, timeout reminders, voice prompts, and optional snapshots on exceptions.
- **Data:** completed recordings are stored in local SQLite with their tracking number, start/end times, exception tags, and notes, then synchronized to Excel or WPS through a reliable retry queue. A locked workbook is retried, never forcibly modified.
- **Review:** search history by tracking number, note, date, and exception tag; play recordings, jump to exception timestamps, export videos and CSV, and inspect synchronization status.
- **Phone companion:** an Android phone works as a camera or a scanner, and the scanner screen embeds exception controls (tags, notes, snapshot, stop) for the active recording over the same private LAN.
- **Labels:** a built-in thermal label designer with text, barcodes, QR codes, images, shapes, data-field mapping, and batch printing.
- **Compatibility:** optionally watch HIK SCAN recording folders read-only and synchronize them without modifying HIK SCAN or the original MP4 files.

## Full feature and engineering details

### Recording, scanning, and on-site operation

- **Desktop recording:** live preview, scan to start or finish, automatically switch to a new parcel when its number is scanned, manual stop, duplicate detection, timeout reminders, voice prompts, and recent results.
- **Camera recording:** the compatibility path uses Windows Media Foundation and OpenCV and requests 3840×2160 at 15 fps by default. Eligible cameras are detected, time and tracking number watermarks are added, and a temporary recording is renamed only after the encoder closes successfully.
- **Video sources and controls:** automatic or selected local USB/UVC, iVCam and other Windows cameras; RTSP/HTTP IP cameras and Hikvision NVR/DVR channels. Network passwords are protected with Windows DPAPI. Controls include focus, image adjustments, snapshots, rotation, mirror, and full screen.
- **Scanning:** configurable tracking-number length, prefix, suffix, and scan interval; Windows Raw Input device isolation with an input-field fallback.

### Exceptions, notes, and history

- **Exceptions and notes:** scan command barcodes or use buttons for damage and substitution; multiple tags can be applied and undone. Notes autosave after 500 ms. Exceptions appear in watermarks, filenames, SQLite, history, and Excel without overwriting manually entered Excel notes.
- **History:** filter by tracking number, notes, dates, and exception tags; view thumbnails, timestamps, size, and synchronization status; edit notes, jump to exception times, open folders, re-sync, export video/CSV, and select or delete records in batches. The **重新同步** command in the history window re-queues **every Excel task that has not succeeded yet — pending, failed, and processing** — for the Excel connector, not just the selected rows; the dialog text mentions only failed or waiting tasks, while the actual predicate is `status <> 'Succeeded'`.

### Excel and data safety

- **HIK SCAN-compatible synchronization:** monitor unpacking and packing folders, wait for MP4 files to finish writing, extract tracking numbers and times, store records in SQLite, and synchronize to Excel with retries. The first run indexes existing videos without automatically backfilling Excel.
- **Excel:** `.xlsx` only; the worksheet must be named `退货扫码单号` and the first six headers must be `扫码日期`, `快递单号`, `关联结果`, `金额`, `备注`, `图片凭证` (extra columns are ignored). Append after the last nonempty cell in column B; a real date in A, tracking number as text in B, blanks in D and F, and the compatibility marker `【拆包智录】` plus exceptions and notes in E. **Column C receives the platform-association formula only when the workbook also contains the three legacy Pinduoduo, Taobao, and JD association sheets; otherwise C stays blank.** Workbooks are backed up and saved with temporary files and atomic replacement; missing date styles are repaired for Excel and WPS compatibility.

### Cameras, storage, and media runtime

- **Multiple views and storage:** layouts support 1, 2, 3, 4, 8, or 16 views. Each camera and optional composite has its own writer. Composite video is enabled by default for 1–4 cameras; above four, individual recordings take priority. A storage pool assigns each session to a healthy disk and checks capacity and reserved space; changing the pool does not move existing recordings.
- **Media checks:** the source checks for GStreamer 1.28.5, required plugins, and a usable H.264 encoder, and separately audits FFmpeg 8.1.2 against an LGPL distribution allowlist. These checks do not mean the runtime is installed or that 8/16-camera hardware acceptance is complete.

### Setup, recovery, and local interfaces

- **Local API and setup:** the compatibility API listens only on `127.0.0.1` and protects its key with DPAPI. A first-run wizard configures the recording folder and optionally an Excel workbook. When recoverable records are found, its step-4 merge option is **checked by default** and clicking 下一步 merges immediately; clear the checkbox first to skip. The `.unpackvision` portable index contains record metadata and relative paths for previewing and merging recovery without moving or overwriting recordings.
- **Offline processing:** document edge detection, perspective correction, rotation, image enhancement, and barcode/QR recognition. Cloud OCR is not called by default; without a local model, OCR reports that it is not configured.
- **Thermal barcode designer:** text, images, Code 128, QR codes, shapes, layers, alignment, rotation, undo/redo, millimeter dimensions, print offsets, Excel/CSV field mappings, batch quantities, versioned JSON templates, and Windows printer drivers. Recording can continue while editing.

### Phone companion

- `UnpackVision.StationHost` runs in the user session and receives scan commands. `IScanCommandRouter` stores idempotent receipts in SQLite. The Android scanner can optionally trigger recording or append to Excel.
- Android provides smart camera and phone scanner modes. The exception controls are embedded at the bottom of the phone scanner screen; they can view the active recording and add damage, substitution, missing-item, or purchase tags, capture a problem image, add notes, and stop recording, with Chinese voice and vibration feedback after desktop confirmation. **The phone UI currently has no visible undo button**: undo on the computer with 撤销标签 or by scanning the fixed `UV-UNDO-TAG` barcode. CameraX supplies frames to RootEncoder 2.8.0 and ZXing-C++ 3.1.0; a code must be seen twice inside a 300 ms stability window before it is sent, and the same code cannot trigger again until it has left the scan area for 1.2 seconds.
- In 2.6.3 the phone home page renders only two entry cards, 手机扫码器 and 智能摄像头. Turning off 更多设置 → 启动电脑录像 in the phone scanner switches it to collect-numbers-only and queue the scan for Excel; that branch and the exception controls are not separate home-page modes. See the [English user guide](docs/user-guide.en.md#13-phone-scanner-smart-camera-and-exception-controls).
- The desktop settings page issues a one-time pairing QR code valid for five minutes; inside the pairing dialog you can pick the phone's subnet under 局域网地址（手机和电脑须在同一网段） and click 刷新二维码 if needed. Each phone has its own key and Android Keystore-protected token; the computer stores token hashes. Devices can be inspected and revoked. MediaMTX 1.18.2 starts on demand with device-specific RTSPS paths, `camera:publish` and `video:read` authorization, and HTTPS/WHEP preview.
- Station APIs provide paginated records, events, thumbnails, and video with Range/206 and ETag without exposing local paths. LAN listeners bind loopback and available private IPv4 addresses. Android can recover connections using DNS-SD/mDNS, hotspot, tethering, or the USB debugging reverse channel; it separately checks station availability and desktop readiness.
- Mobile control uses HTTPS with a self-signed station certificate pinned by SHA256, and media publishing uses RTSPS. Legacy plaintext pairing credentials must be paired again after migration. The development ADB fallback permits plaintext only on `127.0.0.1`, never across the LAN. MediaMTX is downloaded with SHA256 verification instead of committing its binary; release packages include it and its license.

Station endpoints include:

```text
GET    /api/v1/records
GET    /api/v1/records/{id}
GET    /api/v1/records/{id}/thumbnail
GET    /api/v1/records/{id}/video
GET    /api/v1/records/{id}/events
GET    /api/v1/devices
POST   /api/v1/devices/{id}/revoke
DELETE /api/v1/devices/{id}
GET    /api/v1/stations/{id}/state
POST   /api/v1/stations/{id}/scans
GET    /api/v1/stations/{id}/live
```

## Camera capacity and prerelease limits

The 2.5.2 engineering foundation added 8/16-camera layouts, multiple recording disks, and IP camera discovery to the earlier LAN and four-camera features; 2.6.3 keeps them. It remains a prerelease:

- Up to 16 USB, virtual, phone, IP camera, or Hikvision recorder channels can be configured. Actual capacity depends on end-to-end performance testing on each computer.
- By default 1–4 cameras save individual streams and a 1080p composite; 5–16 cameras prioritize individual streams. The primary camera is required; writer failure or five seconds without a frame safely ends the session. Secondary cameras reconnect and gaps remain auditable.
- The 60-second test for 5–16 cameras is tied to the camera, composite, and enabled disk configuration; repeat it after changes. Each enabled disk is tested. History playback is currently limited to four synchronized views by default.
- Multi-disk removal, drive-letter changes, and capacity warnings still require physical disk acceptance. This repository does not record the two-hour 8/16-camera, 100-parcel, and disk-failover acceptance needed to claim stable production readiness. Existing `VideoPath`, `RecordingRoot`, Excel, folder, and API compatibility is preserved.

**Ordinary users should start with a single camera** (设置 → 相机信息 → 录像模式 → 单机位) and enable multiple cameras only after camera, scanning, recording, and Excel all work reliably.

## Privacy and data

On first launch, the terms and privacy policy are shown before camera use, scanning, or online updates. After the user ticks consent, the app may send one minimal anonymous daily activity event per Beijing calendar day to a fixed Cloudflare HTTPS endpoint; **that anonymous-usage checkbox is checked by default, so clear it first if you do not want it**. It does not collect hardware fingerprints, tracking numbers, recordings, Excel files, paths, or accounts. Disabling statistics stops requests and removes the local random statistics key.

Tracking numbers, recordings, notes, and Excel files stay on your computer by default. The Android first-launch consent is also pre-checked and can be turned off at any time. Phone control only connects to a station you paired by scanning its QR code; production stations expose no plaintext LAN HTTP switch. Do not map station ports to the public internet.

## Safe operation

- The compatibility service reads the recording folders and can run alongside HIK SCAN. The standalone recorder needs exclusive camera access; exit HIK SCAN before recording with it.
- For an initial trial, copy the workbook and select the copy in the desktop app under 系统设置 → 存储与数据. In normal production mode the compatibility service follows desktop settings, so the `Excel__WorkbookPath` environment variable does not override it. A workbook locked by Excel or Plastic SCM is retried rather than forcibly modified. Backups default to `%LOCALAPPDATA%\UnpackVision\Backups`.
- Do not publish screenshots of the pairing QR code. Remove phones you no longer use in 系统设置 → 手机协同 → 管理已配对设备.

## Quick start (ordinary users)

1. Accept the agreements and select a recording folder in the setup wizard.
2. Select an Excel workbook, create the standard six-column workbook, or skip it for now.
3. Review existing recordings and portable-index recovery: when recoverable records are found the merge option is checked by default, and clicking 下一步 merges immediately — clear it first to skip. Binding an existing workbook only checks exclusive read/write access, locks, and headers, and never writes a test row.
4. In 系统设置 → 相机信息, test the current camera, check orientation and resolution, then save.
5. Exit HIK SCAN and ensure the camera is free.
6. Scan a tracking number to start; scan it again to finish, or scan a new number to switch parcels.
7. If the scanner fails, use the 结束录像 (end recording) button to stop manually.

Click-by-click paths, phone pairing, label printing, and troubleshooting are in the [English user guide](docs/user-guide.en.md).

## Backup, recovery, and migration

- Back up the **entire recording folder** (including the hidden `.unpackvision`), the Excel workbook, and label templates. With only MP4 files, recordings that follow the naming rule can still be partially recovered from file names — tracking number, start/end times, and tag names embedded in the filename. The recovered exception timestamps are placeholders and cannot locate the real events; notes and sync state may also be lost.
- The record database is `%LOCALAPPDATA%\UnpackVision\unpackvision.db` and settings are in `settings.json` beside it; back both up before moving to a new computer.
- To migrate, preserve or copy the complete recording folder and workbook, reselect both locations on the new computer, and reconfigure cameras and phone pairing. Phone keys are not transferred with the portable index.
- Recovery preview appears in two places: step 4 of the first-run wizard (when recoverable records exist the merge option is checked by default, and clicking 下一步 merges), and — on an installed app — under 系统设置 → 存储与数据 → 数据检查与恢复, where you first click **检查旧数据** to see the classification counts and then **重建本机数据库** to confirm the merge (the current database is backed up first).
- Changing 主录像位置 to an old recording folder asks whether to **migrate**; it does not start recovery automatically. The app copies and verifies each file and only tries to remove source files that match its verification receipt (length and SHA-256). Power loss, hardware, or file-system faults can still occur, so keep a separate full backup before migrating.
- For older folders without a portable index, use **检查旧数据** to parse file names produced by this software and confirm the result with **查看恢复报告**; the report is for inspection only and never backfills Excel automatically.

## Compatibility service configuration

This section covers **HIK SCAN recording-file synchronization**: a read-only watcher over MP4 folders produced by HIK SCAN. It is separate from using **Hikvision NVR/DVR channels as a camera source** under 系统设置 → 相机信息, which is camera input and needs no compatibility service.

Defaults are in `src/UnpackVision.Service/appsettings.json`; double underscores represent nested keys. **In normal production mode the service loads the desktop `settings.json` and overwrites the recording folder, storage pool, and Excel path with it**, so the `Excel__WorkbookPath` environment variable normally has no effect — select the workbook in the desktop app under 系统设置 → 存储与数据 instead. Environment overrides for those paths apply only to an isolated test instance started with `UNPACKVISION_ALLOW_TEST_INSTANCE=1`, which must never point at real business data.

```powershell
$env:HikCompatibility__UnpackingDirectory = 'D:\Program Files\海康威视\HIK SCAN\storage\LogisticsUnpacking'
$env:HikCompatibility__PackingDirectory = 'D:\Program Files\海康威视\HIK SCAN\storage\LogisticsPaking'
powershell -ExecutionPolicy Bypass -File .\scripts\run-service.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\run-service.ps1 -ShowApiKey
```

The two `HikCompatibility__*` directories are ordinary service settings and are not overwritten by the desktop settings.

**How an installed release starts the compatibility service is not verified.** The release scripts only publish `UnpackVision.Service` into the `Service` subdirectory under the staging root `App`; where it ends up after installation and how it is started are not verified, and the repository contains no implementation record of the desktop app launching it or registering it as a startup task. Treat the behaviour on your own installation as unknown, do not assume opening the desktop app synchronizes HIK folders, and use `scripts\run-service.ps1` from source while watching the logs to confirm.

Except for the minimal `/api/v1/health` endpoint, requests require `X-UnpackVision-Key: <local API key>`. The service exposes record, tracking-number, tag, note, connector retry, health, image processing, barcode, and OCR health endpoints; see the [Chinese README](README.md#兼容同步服务配置) for the complete list. In 2.6.3 the compatibility service binds the loopback interface only and validates connection and Host headers.

## Data locations

- SQLite: `%LOCALAPPDATA%\UnpackVision\unpackvision.db`
- Settings: `%LOCALAPPDATA%\UnpackVision\settings.json`
- Thermal label templates: `%LOCALAPPDATA%\UnpackVision\Templates`
- Diagnostic logs: `%LOCALAPPDATA%\UnpackVision\Logs` (daily and 20 MB rotation, retained up to 14 days)
- Recordings: `%USERPROFILE%\Videos\UnpackVision\Unpacking` or `Packing`
- Portable index: `.unpackvision` inside the recording folder; back it up with the recordings.
- API key: `%LOCALAPPDATA%\UnpackVision\api-key.protected`

Settings include scan rules, exception barcodes, maximum recording duration, and working mode. Tracking numbers are stored as text so letters, hyphens, and leading zeroes are preserved.

## Safe uninstall

Use **设置 → 关于 → 卸载与数据保留 → 安全卸载灵兔拆包智录** with the app still open, after ending and saving any active recording. By default, only the application, shortcuts, and uninstall registration are removed; databases, settings, pairing data, recordings, and Excel are retained. Permanent cleanup requires two warnings and verifies `.unpackvision/workspace.json` against the current workspace ID; it is refused if verification fails. Cleanup is restricted to `Unpacking`, `Packing`, `Snapshots`, `.unpackvision`, and `%LOCALAPPDATA%\UnpackVision`; Excel and other files in the recording folder remain. Uninstalling through Windows Settings retains business data by default.

## Build, test, and release

This section is for developers. **Installed-release users do not need the .NET SDK, Android Studio, or any build script.** Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) are required only when building from source. Scripts prefer a root `.dotnet` installation if present, otherwise the system SDK.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\run-service.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\run-app.ps1
```

The service monitors HIK folders and synchronizes SQLite and Excel; the app starts the standalone recorder. `Start-Sync-Service.cmd` and `Start-UnpackVision.cmd` exist only in the source repository and hardcode `artifacts\publish\staging\2.3.0`, so they work solely for that staging output and are not universal launchers for every release; use `scripts\run-service.ps1`, `scripts\run-app.ps1`, or the installed shortcuts instead.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\build-android.ps1
```

The Android script checks the system Android Studio/JBR and SDK and handles Windows paths containing Chinese characters. The debug APK is written to `mobile\UnpackVision.Android\app\build\outputs\apk\debug\app-debug.apk`. For the development-only ADB fallback and pinned MediaMTX download:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\enable-adb-usb-fallback.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\fetch-mediamtx.ps1
```

Release output is stored in `artifacts\release-output`. The Windows installer updates the desktop app, StationHost, compatibility service, and licensed MediaMTX together. Android releases include an APK, update manifest, and SHA256. Example with the 2.5.2 version string (use the current version when packaging):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-signed-android.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 `
  -Version 2.5.2 `
  -AndroidApk .\mobile\UnpackVision.Android\app\build\outputs\apk\release\app-release.apk
```

The anonymous activity Worker is in `telemetry`; deployment requires copying `wrangler.example.toml`, creating a D1 database, and authorizing the developer's Cloudflare account. Clients send no statistics request without a configured production HTTPS endpoint.

## License and independent implementation

The project is licensed under the [MIT License](LICENSE). It is independently implemented; it contains no HIK SCAN or Hikvision source code, trademarks, interface assets, or other proprietary resources, and product names are used only to describe compatibility. See the [terms](docs/TERMS.md), [privacy policy](docs/PRIVACY.md), [security and vulnerability disclosure](SECURITY.md), [release notes](docs/releases/2.6.3.md), and [code-signing policy](docs/CODE_SIGNING.md). These linked documents are currently in Chinese unless otherwise indicated.

## Code signing policy

Stable Windows releases require a verifiable GitHub Actions build and a trusted Authenticode signature; unsigned builds remain prereleases. The project is applying to the SignPath Foundation for free open-source code signing. See the [code-signing policy](docs/CODE_SIGNING.md) for responsibilities, privacy boundaries, and release gates (in Chinese).

## Future work

`ISyncConnector` allows additional connectors beyond Excel. DingTalk AI tables and website integration should each implement a connector without changing scanning, recording, or database cores. Servers can be configured with signed webhooks using event IDs, timestamps, and HMAC-SHA256. PaddleOCR model packages, audio tracks, and virtual cameras remain acceptance work that needs available hardware.

---

[简体中文](README.md) | **English** · [User guide](docs/user-guide.en.md)
