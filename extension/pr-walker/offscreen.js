const HUB_WS = "ws://127.0.0.1:17341/walker/";
const HUB_HEALTH = "http://127.0.0.1:17341/walker/health";
let ws = null;

function isOpen() {
  return ws?.readyState === WebSocket.OPEN;
}

async function hubUp() {
  try {
    const r = await fetch(HUB_HEALTH, { cache: "no-store" });
    if (r.ok) return true;
  } catch { /* fall through */ }
  // Older XScribe builds have no /health — any HTTP response means the port is open.
  try {
    const r = await fetch("http://127.0.0.1:17341/walker/", { cache: "no-store" });
    return r.status > 0;
  } catch {
    return false;
  }
}

async function connect() {
  if (isOpen()) return { ok: true, connected: true };
  if (ws?.readyState === WebSocket.CONNECTING) return { ok: true, connected: false };

  if (!(await hubUp())) {
    return { ok: false, error: "local hub not available" };
  }

  try {
    ws = new WebSocket(HUB_WS);
    ws.onopen = () => {
      ws.send(JSON.stringify({ type: "hello", from: "extension" }));
      chrome.runtime.sendMessage({ channel: "ws", event: "open" }).catch(() => {});
    };
    ws.onmessage = (ev) => {
      chrome.runtime.sendMessage({ channel: "ws", event: "message", data: ev.data }).catch(() => {});
    };
    ws.onclose = () => {
      ws = null;
      chrome.runtime.sendMessage({ channel: "ws", event: "close" }).catch(() => {});
    };
    ws.onerror = () => {
      try { ws?.close(); } catch { /* ignore */ }
    };
    return { ok: true, connected: false };
  } catch (e) {
    ws = null;
    return { ok: false, error: String(e) };
  }
}

function send(data) {
  if (!isOpen()) return { ok: false, error: "not connected" };
  ws.send(data);
  return { ok: true };
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg.channel !== "offscreen") return;
  if (msg.cmd === "connect") {
    connect().then(sendResponse);
    return true;
  }
  if (msg.cmd === "send") {
    sendResponse(send(msg.data));
    return true;
  }
  if (msg.cmd === "status") {
    sendResponse({ connected: isOpen() });
    return true;
  }
  if (msg.cmd === "probe") {
    hubUp().then((up) => sendResponse({ up }));
    return true;
  }
});
