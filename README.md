# TurboDL for Ubuntu 26.04 LTS (Linux x64)

Port of TurboDL 1.4 (Windows, WPF) to Linux. C# / .NET 10 / **Avalonia UI** (cross-platform successor to WPF).
The download engine, queue, resume logic (including the v1.1.1 pause/resume fix), rate limiter and browser bridge.

## Build and install

```bash
cd TurboDL.Linux
# 1. copy the ChromeExtension folder from the Windows project into ./ChromeExtension
chmod +x *.sh
./build-linux.sh      # installs the .NET 10 SDK on first run if needed, then -> publish/TurboDL
./install.sh          # per user, no sudo: app grid entry, icon, 'turbodl' command
```

Run: open **TurboDL** from the app grid, or `turbodl`, or `turbodl https://example.com/file.iso`.
Data, settings and log: `~/.local/share/TurboDL/` (downloads.json, settings.json, log.txt).
Uninstall: `./uninstall.sh` (keeps settings) or `./uninstall.sh --purge`.

> You can also build on the Windows PC and copy `publish/` to Ubuntu:
> `dotnet publish TurboDL.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`

## Feature map (Windows → Linux)

| Feature | Windows 1.4 | Ubuntu port |
|---|---|---|
| Multi-connection engine (up to 32), dynamic segmentation, connection reuse | ✔ | ✔ same code |
| Single pre-allocated `.tdl` file, no merge step | Sparse file (NTFS) | `fallocate` – space reserved instantly on ext4/btrfs/xfs; sparse fallback elsewhere |
| Safe resume after power cut (flush data before saving positions) | FlushFileBuffers | `fsync` |
| Pause/resume fix + self-healing passes (v1.1.1) | ✔ | ✔ same code |
| Retry, timeouts, expired-link / changed-file detection, disk-space check | ✔ | ✔ (disk check uses the real mount point, e.g. USB drives) |
| Queue, Start all / Stop all, Add paused, speed limiter | ✔ | ✔ |
| Category sub-folders, sidebar filters, search | ✔ | ✔ (+ .AppImage, .flatpak, .snap, .run, .sh, .img) |
| Segment progress bar, speed/ETA, total speed | ✔ | ✔ |
| Dark / Light / Same as system theme (live preview) | ✔ | ✔ follows GNOME Settings → Appearance live; dark title bar via GNOME |
| Tray icon + menu (Open, Add, Resume/Pause all, Start at login, Exit) | Notification area | Ubuntu top bar (AppIndicator) |
| Notifications, click to show file | Balloon | GNOME notification, click → file selected in Files |
| Start with Windows (minimised to tray) | HKCU\Run | `~/.config/autostart/turbodl.desktop` |
| Single instance, `TurboDL <url>`, `--tray` | Mutex | file lock (released automatically if TurboDL crashes) |
| Drag-and-drop links, Ctrl+N / Ctrl+V / Space / Del / Enter | ✔ | ✔ |
| Browser bridge 127.0.0.1:9614 + Chrome/Edge extension | ✔ | ✔ same extension, unchanged; Chrome, Chromium (snap) and Edge |
| System proxy | Windows proxy | `http(s)_proxy` variables or GNOME manual proxy |
| Clean stop on logout/shutdown | WM_ENDSESSION | SIGTERM/SIGHUP handled → progress saved |
| File names | Windows rules | Windows-safe *and* Linux 255-byte limit (safe for USB/NTFS copies) |

## Browser extension
TurboDL → Settings → **Set up Chrome** (or Chromium / Edge). The folder path is copied to the clipboard;
in the extensions page turn on Developer mode → Load unpacked → **Ctrl+L**, paste, Select.
For Ubuntu's snap Chromium the extension is placed in `~/snap/chromium/common/TurboDL-Extension` (snaps can't read hidden folders).

## Troubleshooting
- **No icon in the top bar** – the *Ubuntu AppIndicators* extension must be on (default on Ubuntu; check the Extensions app).
  Without it TurboDL minimises instead of hiding, so the window is never lost.
- **Bridge test** – `./test-bridge.sh` (TurboDL must be running).
- **Log** – `~/.local/share/TurboDL/log.txt`.

## Bridge API (unchanged)
GET /ping · POST /add (header X-TurboDL-Client) `{"url","urls","fileName","referrer","cookies","userAgent","silent"}` · POST /show.
Requests from web pages (http/https Origin) are rejected.

<img width="1107" height="721" alt="image" src="https://github.com/user-attachments/assets/3a935899-0fca-4276-99b7-5d9955d13dcd" />

<img width="621" height="735" alt="image" src="https://github.com/user-attachments/assets/4429cdc3-890d-42fd-9cf4-23b6bc0d4dc1" />


