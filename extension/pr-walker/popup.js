async function refresh() {
  const el = document.getElementById("s");
  try {
    const res = await chrome.runtime.sendMessage({ type: "getStatus" });
    if (res?.connected) {
      el.textContent = "Linked to local app.";
      el.className = "ok";
    } else {
      el.textContent = "Waiting for local app. Open XScribe Desktop first.";
      el.className = "wait";
    }
  } catch {
    el.textContent = "Background idle. Click Refresh link.";
    el.className = "wait";
  }
}

document.getElementById("go").addEventListener("click", async () => {
  const el = document.getElementById("s");
  el.textContent = "Linking…";
  el.className = "wait";
  try {
    const res = await chrome.runtime.sendMessage({ type: "connect" });
    if (res?.connected) {
      el.textContent = "Linked to local app.";
      el.className = "ok";
    } else {
      el.textContent = "Local app not running. Start XScribe Desktop.";
      el.className = "wait";
    }
  } catch {
    el.textContent = "Could not reach background.";
    el.className = "wait";
  }
});

refresh();
