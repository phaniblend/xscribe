let boundTabId = null;
let offscreenReady = false;
let reconnectTimer = null;

function setState(connected, note) {
  chrome.storage.local.set({ connected, statusNote: note, statusAt: Date.now() });
}

function scheduleReconnect(ms = 10000) {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(() => hubConnect(), ms);
}

async function ensureOffscreen() {
  if (offscreenReady) return true;
  try {
    const existing = await chrome.runtime.getContexts?.({
      contextTypes: ["OFFSCREEN_DOCUMENT"],
    });
    if (existing?.length > 0) {
      offscreenReady = true;
      return true;
    }
  } catch { /* getContexts unavailable */ }
  await chrome.offscreen.createDocument({
    url: "offscreen.html",
    reasons: ["WORKERS"],
    justification: "Background connection for local tab synchronization",
  });
  offscreenReady = true;
  return true;
}

async function offscreenSend(cmd, extra = {}) {
  await ensureOffscreen();
  return chrome.runtime.sendMessage({ channel: "offscreen", cmd, ...extra });
}

async function isConnected() {
  try {
    const res = await offscreenSend("status");
    return !!res?.connected;
  } catch {
    return false;
  }
}

async function hubConnect() {
  try {
    await ensureOffscreen();
    const probe = await offscreenSend("probe");
    if (!probe?.up) {
      setState(false, "local hub unavailable");
      scheduleReconnect(15000);
      return false;
    }
    const started = await offscreenSend("connect");
    if (started?.error) {
      setState(false, started.error);
      scheduleReconnect(15000);
      return false;
    }
    for (let i = 0; i < 30; i++) {
      if (await isConnected()) {
        clearTimeout(reconnectTimer);
        setState(true, "linked");
        return true;
      }
      await new Promise((r) => setTimeout(r, 100));
    }
    setState(false, "link timeout");
    scheduleReconnect(15000);
    return false;
  } catch (e) {
    setState(false, String(e));
    scheduleReconnect(15000);
    return false;
  }
}

function hubSend(json) {
  offscreenSend("send", { data: JSON.stringify(json) }).catch(() => {});
}

async function handleCommand(msg) {
  if (!msg || typeof msg !== "object") return;
  try {
    let result;
    switch (msg.type) {
      case "listTabs":
        result = await listTabs();
        break;
      case "bindTab":
        result = await bindTab(msg.tabId);
        break;
      case "getPrFileList":
        result = await sendToBound({ type: "getPrFileList" });
        break;
      case "runStep":
        result = await sendToBound({ type: "runStep", payload: msg.payload, skipAnim: msg.skipAnim });
        break;
      case "abortStep":
        result = await sendToBound({ type: "abortStep" });
        break;
      case "cleanup":
        if (boundTabId) await cleanupTab(boundTabId);
        boundTabId = null;
        await chrome.storage.local.remove("boundTabId");
        result = { ok: true };
        break;
      case "ping":
        result = { ok: true, boundTabId };
        break;
      default:
        result = { ok: false, error: "unknown command" };
    }
    reply(msg.id, result);
  } catch (e) {
    reply(msg.id, { ok: false, error: String(e) });
  }
}

function reply(id, result) {
  if (id != null) hubSend({ type: "reply", id, result });
}

async function listTabs() {
  const tabs = await chrome.tabs.query({});
  return {
    ok: true,
    tabs: tabs.map((t) => ({
      id: t.id,
      title: t.title,
      url: t.url,
      windowId: t.windowId,
      active: t.active,
    })),
  };
}

async function bindTab(tabId) {
  const tab = await chrome.tabs.get(tabId);
  if (!tab.url?.includes("github.com") || !/\/pull\/\d+\/(files|changes)/.test(tab.url)) {
    return { ok: false, error: "Tab must be a GitHub PR files/changes page" };
  }
  if (boundTabId && boundTabId !== tabId) await cleanupTab(boundTabId);
  boundTabId = tabId;
  await chrome.storage.local.set({ boundTabId });
  await inject(tabId);
  const ping = await sendToTab(tabId, { type: "ping" });
  return { ok: true, tabId, url: ping?.url || tab.url };
}

async function restoreBoundTab() {
  try {
    const stored = await chrome.storage.local.get(["boundTabId"]);
    if (stored.boundTabId) boundTabId = stored.boundTabId;
  } catch { /* ignore */ }
}

async function sendToBound(msg) {
  if (!boundTabId) await restoreBoundTab();
  if (!boundTabId) return { ok: false, error: "no tab bound" };
  return sendToTab(boundTabId, msg);
}

async function inject(tabId) {
  await chrome.scripting.executeScript({
    target: { tabId },
    files: ["virtual-pointer.js", "github-pr.js", "content.js"],
  });
}

async function sendToTab(tabId, msg) {
  try {
    return await chrome.tabs.sendMessage(tabId, msg);
  } catch {
    await inject(tabId);
    return await chrome.tabs.sendMessage(tabId, msg);
  }
}

async function cleanupTab(tabId) {
  try {
    await chrome.tabs.sendMessage(tabId, { type: "cleanup" });
  } catch { /* tab closed */ }
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg.channel === "ws") {
    if (msg.event === "open") {
      clearTimeout(reconnectTimer);
      setState(true, "linked");
    } else if (msg.event === "close") {
      // Keep boundTabId — MV3 workers restart often; don't force re-bind
      setState(false, "hub closed");
      scheduleReconnect(5000);
    } else if (msg.event === "message") {
      try {
        handleCommand(JSON.parse(msg.data));
      } catch { /* ignore */ }
    }
    return;
  }

  if (msg.type === "connect") {
    hubConnect().then((connected) => sendResponse({ connected }));
    return true;
  }
  if (msg.type === "getStatus") {
    isConnected().then((connected) => sendResponse({ connected }));
    return true;
  }
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === "reconnect") isConnected().then((ok) => { if (!ok) hubConnect(); });
});

chrome.runtime.onStartup.addListener(() => {
  restoreBoundTab().then(() => scheduleReconnect(3000));
});
chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create("reconnect", { periodInMinutes: 1 });
  restoreBoundTab().then(() => scheduleReconnect(3000));
});

restoreBoundTab();

// No immediate connect — avoids ERR_CONNECTION_REFUSED when XScribe is not running yet.
