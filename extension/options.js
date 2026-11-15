const DEFAULTS = { enabled: true, minSizeMb: 0, skipHosts: "" };
const enabled = document.getElementById("enabled");
const minSizeMb = document.getElementById("minSizeMb");
const skipHosts = document.getElementById("skipHosts");
const status = document.getElementById("status");

async function load() {
  const settings = { ...DEFAULTS, ...(await chrome.storage.sync.get(DEFAULTS)) };
  enabled.checked = settings.enabled;
  minSizeMb.value = settings.minSizeMb;
  skipHosts.value = settings.skipHosts;
}

function save() {
  const size = Math.max(0, Math.min(100000, Math.floor(Number(minSizeMb.value) || 0)));
  chrome.storage.sync.set({ enabled: enabled.checked, minSizeMb: size, skipHosts: skipHosts.value });
}

async function check() {
  const reply = await chrome.runtime.sendMessage({ type: "ping" });
  status.textContent = reply && reply.ok
    ? "Aria2NET is installed and can be reached."
    : "Aria2NET cannot be reached. Install it, then switch on \"Catch downloads from Chrome and Edge\" in its Settings, under Downloads.";
}

enabled.addEventListener("change", save);
minSizeMb.addEventListener("change", save);
skipHosts.addEventListener("change", save);
load();
check();
