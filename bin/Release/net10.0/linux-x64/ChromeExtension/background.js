// TurboDL browser integration (Manifest V3 service worker)
import { DEFAULTS, getSettings, baseUrl } from "./common.js";

const URL_RE = /https?:\/\/[^\s"'<>]+/gi;

// ---------------------------------------------------------------- helpers

async function sendToTurboDL(payload, port) {
  try {
    const r = await fetch(`${baseUrl(port)}/add`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-TurboDL-Client": "chrome" },
      body: JSON.stringify(payload),
      signal: AbortSignal.timeout(3000)
    });
    const ok = r.ok;
    await setBadge(ok ? null : "!");
    return ok;
  } catch {
    await setBadge("!");
    return false;
  }
}

async function cookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map(c => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

function baseName(path) {
  if (!path) return "";
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] || "";
}

function extensionOf(name, url) {
  let n = name;
  if (!n) {
    try { n = decodeURIComponent(new URL(url).pathname.split("/").pop() || ""); } catch { n = ""; }
  }
  const i = n.lastIndexOf(".");
  return i > 0 ? n.slice(i + 1).toLowerCase() : "";
}

function matchesType(name, url, fileTypes) {
  const types = fileTypes.toLowerCase().split(/[\s,;]+/).filter(Boolean).map(t => t.replace(/^\./, ""));
  if (types.includes("*")) return true;
  const ext = extensionOf(name, url);
  return ext !== "" && types.includes(ext);
}

function hostOf(url) {
  try { return new URL(url).hostname.toLowerCase(); } catch { return ""; }
}

function isExcluded(urls, excludedSites) {
  const rules = excludedSites.toLowerCase().split(/[\s,;]+/).filter(Boolean);
  if (rules.length === 0) return false;
  return urls.some(u => {
    const h = hostOf(u);
    return h && rules.some(r => h === r || h.endsWith("." + r));
  });
}

async function setBadge(text) {
  const s = await getSettings();
  if (!s.enabled) {
    await chrome.action.setBadgeText({ text: "OFF" });
    await chrome.action.setBadgeBackgroundColor({ color: "#8C95AA" });
    await chrome.action.setTitle({ title: "TurboDL – capture is off" });
  } else if (text) {
    await chrome.action.setBadgeText({ text });
    await chrome.action.setBadgeBackgroundColor({ color: "#FF5D5D" });
    await chrome.action.setTitle({ title: "TurboDL is not running – downloads stay in the browser" });
  } else {
    await chrome.action.setBadgeText({ text: "" });
    await chrome.action.setTitle({ title: "TurboDL" });
  }
}

function notify(message) {
  chrome.notifications.create({
    type: "basic",
    iconUrl: "icons/icon128.png",
    title: "TurboDL",
    message
  });
}

// ---------------------------------------------------------------- automatic capture
// onDeterminingFilename runs before Chrome writes the file, and gives us the real file name
// (from Content-Disposition). If TurboDL accepts the link we cancel Chrome's copy; if TurboDL
// is not running, Chrome just continues normally - nothing is ever lost.

async function tryCapture(item) {
  const s = await getSettings();
  if (!s.enabled || item.incognito || item.byExtensionId) return false;

  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return false;                     // blob:, data:, file: stay in Chrome

  const name = baseName(item.filename);
  if (!matchesType(name, url, s.fileTypes)) return false;
  if (isExcluded([url, item.url, item.referrer || ""], s.excludedSites)) return false;

  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes;
  if (s.minSizeKB > 0 && size > 0 && size < s.minSizeKB * 1024) return false;

  const ok = await sendToTurboDL({
    url,
    fileName: name || null,
    referrer: item.referrer || "",
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
    silent: !s.showDialog
  }, s.port);
  if (!ok) return false;

  try { await chrome.downloads.cancel(item.id); } catch { }
  try { await chrome.downloads.erase({ id: item.id }); } catch { }
  return true;
}

chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  tryCapture(item)
    .then(captured => { if (!captured) suggest(); })
    .catch(() => suggest());
  return true; // we will call suggest() asynchronously
});

// ---------------------------------------------------------------- right-click menu

function createMenus(enabled) {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: "tdl-link", title: "Download with TurboDL", contexts: ["link"] });
    chrome.contextMenus.create({ id: "tdl-media", title: "Download media with TurboDL",
                                 contexts: ["image", "video", "audio"] });
    chrome.contextMenus.create({ id: "tdl-selection", title: "Download links in selection with TurboDL", contexts: ["selection"] });
    chrome.contextMenus.create({ id: "tdl-toggle", title: "Capture browser downloads", type: "checkbox",
                                 checked: enabled, contexts: ["action"] });
  });
}

chrome.runtime.onInstalled.addListener(async () => {
  const s = await getSettings();
  createMenus(s.enabled);
  await setBadge(null);
});

chrome.runtime.onStartup.addListener(async () => {
  const s = await getSettings();
  createMenus(s.enabled);
  await setBadge(null);
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const s = await getSettings();

  if (info.menuItemId === "tdl-toggle") {
    await chrome.storage.sync.set({ enabled: info.checked });
    return;
  }

  let urls = [];
  if (info.menuItemId === "tdl-link" && info.linkUrl) urls = [info.linkUrl];
  else if (info.menuItemId === "tdl-media" && info.srcUrl) urls = [info.srcUrl];
  else if (info.menuItemId === "tdl-selection" && info.selectionText) urls = info.selectionText.match(URL_RE) || [];

  urls = [...new Set(urls.filter(u => /^https?:/i.test(u)))];
  if (urls.length === 0) {
    notify(info.menuItemId === "tdl-media"
      ? "This media uses a stream/blob address that cannot be downloaded directly."
      : "No downloadable http/https link found.");
    return;
  }

  const ok = await sendToTurboDL({
    url: urls[0],
    urls,
    referrer: (tab && tab.url) || info.pageUrl || "",
    cookies: await cookieHeader(urls[0]),
    userAgent: navigator.userAgent,
    silent: false              // right-click always shows the Add window so you can choose name/folder
  }, s.port);

  if (!ok) notify("TurboDL is not running. Start TurboDL and try again.");
});

// ---------------------------------------------------------------- keep badge/menu in sync with settings

chrome.storage.onChanged.addListener(async (changes, area) => {
  if (area !== "sync") return;
  if (changes.enabled) {
    chrome.contextMenus.update("tdl-toggle", { checked: !!changes.enabled.newValue }, () => void chrome.runtime.lastError);
    await setBadge(null);
  }
});
