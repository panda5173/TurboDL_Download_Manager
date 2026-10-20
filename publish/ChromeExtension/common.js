// Shared by background.js and popup.js
export const DEFAULTS = {
  enabled: true,          // capture browser downloads automatically
  showDialog: true,       // show TurboDL's "Add download" window (false = start immediately)
  port: 9614,
  fileTypes:
    "7z apk avi bin bz2 cab deb dmg exe flac gz img iso jar m4a m4v mkv mov mp3 mp4 mpeg mpg msi msix msu " +
    "ogg pdf rar rpm tar tgz wav webm wma wmv xz zip zst",
  excludedSites: "",      // one host per line, e.g. mail.google.com
  minSizeKB: 0            // skip files smaller than this (when size is known), 0 = off
};

export async function getSettings() {
  const s = await chrome.storage.sync.get(DEFAULTS);
  s.port = Number(s.port) || DEFAULTS.port;
  s.minSizeKB = Number(s.minSizeKB) || 0;
  return s;
}

export function baseUrl(port) {
  return `http://127.0.0.1:${port}`;
}

export async function ping(port) {
  try {
    const r = await fetch(`${baseUrl(port)}/ping`, { signal: AbortSignal.timeout(1500), cache: "no-store" });
    if (!r.ok) return null;
    return await r.json();
  } catch {
    return null;
  }
}
