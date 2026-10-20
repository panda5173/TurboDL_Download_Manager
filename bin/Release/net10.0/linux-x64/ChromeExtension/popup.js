import { DEFAULTS, getSettings, ping } from "./common.js";

const $ = id => document.getElementById(id);

async function refreshStatus(port) {
  const el = $("status");
  el.className = "status checking";
  $("statusText").textContent = "Checking…";
  const info = await ping(port);
  if (info) {
    el.className = "status ok";
    $("statusText").textContent = `Connected · TurboDL ${info.version}`;
    chrome.action.setBadgeText({ text: (await getSettings()).enabled ? "" : "OFF" });
  } else {
    el.className = "status bad";
    $("statusText").textContent = "TurboDL is not running";
  }
}

function fill(s) {
  $("enabled").checked = s.enabled;
  $("showDialog").checked = s.showDialog;
  $("fileTypes").value = s.fileTypes;
  $("excludedSites").value = s.excludedSites;
  $("minSizeKB").value = s.minSizeKB;
  $("port").value = s.port;
}

async function init() {
  const s = await getSettings();
  fill(s);
  refreshStatus(s.port);

  $("enabled").addEventListener("change", e => chrome.storage.sync.set({ enabled: e.target.checked }));
  $("showDialog").addEventListener("change", e => chrome.storage.sync.set({ showDialog: e.target.checked }));

  $("save").addEventListener("click", async () => {
    const port = Number($("port").value);
    if (!(port >= 1024 && port <= 65535)) { $("saved").textContent = "Port must be 1024–65535"; return; }
    await chrome.storage.sync.set({
      fileTypes: $("fileTypes").value.trim(),
      excludedSites: $("excludedSites").value.trim(),
      minSizeKB: Math.max(0, Number($("minSizeKB").value) || 0),
      port
    });
    $("saved").textContent = "Saved ✓";
    setTimeout(() => ($("saved").textContent = ""), 1500);
    refreshStatus(port);
  });

  $("reset").addEventListener("click", async () => {
    await chrome.storage.sync.set(DEFAULTS);
    fill(DEFAULTS);
    $("saved").textContent = "Defaults restored";
    refreshStatus(DEFAULTS.port);
  });
}

init();
