(function () {
  if (window.__xscribeContentReady) return;
  window.__xscribeContentReady = true;

  let aborted = false;

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    (async () => {
      try {
        if (msg.type === "runStep") {
          aborted = false;
          const gh = window.__xscribeGithubPr;
          if (!gh?.isPrPage()) {
            sendResponse({ ok: false, error: "not a PR files/changes page" });
            return;
          }
          const result = await gh.runStep(msg.payload, msg.skipAnim || aborted);
          sendResponse(result);
        } else if (msg.type === "getPrFileList") {
          sendResponse({ ok: true, files: window.__xscribeGithubPr?.getPrFileList() || [] });
        } else if (msg.type === "abortStep") {
          aborted = true;
          sendResponse({ ok: true });
        } else if (msg.type === "cleanup") {
          window.__xscribeGithubPr?.cleanup();
          sendResponse({ ok: true });
        } else if (msg.type === "ping") {
          sendResponse({ ok: true, url: location.href });
        } else {
          sendResponse({ ok: false, error: "unknown" });
        }
      } catch (e) {
        sendResponse({ ok: false, error: String(e) });
      }
    })();
    return true;
  });
})();
