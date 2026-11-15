// Hands downloads to Aria2NET through its native messaging host. If the app cannot take one, the browser carries on with it.

const HOST = "io.github.sect19n.aria2net";
const DEFAULTS = { enabled: true, minSizeMb: 0, skipHosts: "" };
const MENU_ID = "aria2net-download-link";

async function loadSettings() {
  return { ...DEFAULTS, ...(await chrome.storage.sync.get(DEFAULTS)) };
}

function hostOf(url) {
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return "";
  }
}

// "example.com" also skips "cdn.example.com".
function isSkipped(url, skipHosts) {
  const host = hostOf(url);
  return String(skipHosts)
    .split(/[\s,]+/)
    .map((entry) => entry.trim().toLowerCase())
    .filter(Boolean)
    .some((entry) => host === entry || host.endsWith("." + entry));
}

// Sites that need a login only serve the file with the cookies the browser would have sent.
async function cookieHeader(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; ");
  } catch {
    return "";
  }
}

function baseName(path) {
  return String(path || "").split(/[\\/]/).pop();
}

function send(message) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(HOST, message, (reply) => {
      if (chrome.runtime.lastError) {
        resolve({ ok: false, error: chrome.runtime.lastError.message });
      } else {
        resolve(reply || { ok: false, error: "Aria2NET did not answer." });
      }
    });
  });
}

async function handOver(url, referrer, filename) {
  return send({
    type: "download",
    url,
    referrer: referrer || "",
    filename: filename || "",
    cookies: await cookieHeader(url),
    userAgent: navigator.userAgent,
  });
}

function flash(text, color) {
  chrome.action.setBadgeBackgroundColor({ color });
  chrome.action.setBadgeText({ text });
  setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);
}

async function quietly(action) {
  try {
    await action();
  } catch {
    // The download finished or went away in the meantime.
  }
}

chrome.downloads.onCreated.addListener(async (item) => {
  const settings = await loadSettings();
  const url = item.finalUrl || item.url;
  if (!settings.enabled || item.state !== "in_progress" || !/^(https?|ftp):/i.test(url)) {
    return; // blob: and data: links only exist inside the browser
  }
  if (item.byExtensionId === chrome.runtime.id || isSkipped(url, settings.skipHosts)) {
    return;
  }
  // The size is often unknown this early; then the download is caught whatever the minimum says.
  const size = item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : 0;
  if (settings.minSizeMb > 0 && size > 0 && size < settings.minSizeMb * 1024 * 1024) {
    return;
  }

  // Held, not cancelled: if Aria2NET cannot take it, the browser picks it up again and nothing is lost.
  await quietly(() => chrome.downloads.pause(item.id));
  const reply = await handOver(url, item.referrer, baseName(item.filename));
  if (reply.ok) {
    await quietly(() => chrome.downloads.cancel(item.id));
    await quietly(() => chrome.downloads.erase({ id: item.id }));
    flash("✓", "#2e9e4f");
  } else {
    console.warn("Aria2NET did not take the download:", reply.error);
    await quietly(() => chrome.downloads.resume(item.id));
    flash("!", "#d13438");
  }
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: MENU_ID, title: "Download with Aria2NET", contexts: ["link"] });
  });
});

chrome.contextMenus.onClicked.addListener(async (info) => {
  if (info.menuItemId !== MENU_ID || !info.linkUrl) {
    return;
  }
  const reply = await handOver(info.linkUrl, info.pageUrl, "");
  if (reply.ok) {
    flash("✓", "#2e9e4f");
  } else {
    console.warn("Aria2NET did not take the link:", reply.error);
    flash("!", "#d13438");
  }
});

// The options page asks whether the app can be reached.
chrome.runtime.onMessage.addListener((message, _sender, respond) => {
  if (message && message.type === "ping") {
    send({ type: "ping" }).then(respond);
    return true; // answered later
  }
  return false;
});
