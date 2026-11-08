// GitHub PR Files-changed DOM helpers — GITHUB_DOM_VERSION: 2026-08
(function () {
  const NS = window.__xscribePointer?.NS || "xscribe-pr-walker";

  function normPath(p) {
    return (p || "").replace(/\\/g, "/").replace(/^\/+/, "").toLowerCase();
  }

  function pathEndsWith(full, suffix) {
    const a = normPath(full);
    const b = normPath(suffix);
    return a === b || a.endsWith("/" + b) || a.endsWith(b);
  }

  function basename(p) {
    const n = normPath(p);
    const i = n.lastIndexOf("/");
    return i >= 0 ? n.slice(i + 1) : n;
  }

  function looksLikeRepoPath(p) {
    if (!p || typeof p !== "string") return false;
    const s = p.trim().replace(/\\/g, "/");
    if (s.length < 3 || s.length > 400) return false;
    if (!s.includes("/")) return false;
    if (/\s{2,}/.test(s)) return false;
    if (/^(https?:|mailto:|javascript:)/i.test(s)) return false;
    // must look like a source file path
    return /\.[a-z0-9]{1,8}$/i.test(s.split("/").pop() || "");
  }

  function isPrPage() {
    return /\/pull\/\d+\/(files|changes)/.test(location.pathname);
  }

  function fileRootFrom(node) {
    return (
      node.closest('[id^="diff-"]') ||
      node.closest("[data-file-type]") ||
      node.closest("[data-tagsearch-path]") ||
      node.closest(".file") ||
      node.closest("copilot-diff-entry") ||
      node.closest("[data-testid='file-diff-entry']") ||
      node.closest("article") ||
      node.closest("[class*='Diff']") ||
      node.closest("[class*='diff']") ||
      node
    );
  }

  function collectPaths() {
    const entries = [];
    const add = (path, node) => {
      const cleaned = (path || "").trim().replace(/\\/g, "/");
      if (!looksLikeRepoPath(cleaned)) return;
      const root = fileRootFrom(node);
      if (!root) return;
      entries.push({ path: cleaned, root, toggle: findToggle(root) });
    };

    // Classic + current GitHub attributes
    const attrSelectors = [
      "[data-tagsearch-path]",
      "[data-path]",
      "[data-file-path]",
      "[data-hydro-view*='file']",
    ];
    for (const sel of attrSelectors) {
      document.querySelectorAll(sel).forEach((node) => {
        add(
          node.getAttribute("data-tagsearch-path") ||
            node.getAttribute("data-path") ||
            node.getAttribute("data-file-path") ||
            node.getAttribute("title"),
          node
        );
      });
    }

    // Diff containers — pull path from header text / links inside
    document.querySelectorAll('[id^="diff-"], .file, copilot-diff-entry, [data-file-type]').forEach((root) => {
      const fromAttr =
        root.getAttribute("data-tagsearch-path") ||
        root.getAttribute("data-path") ||
        root.getAttribute("data-file-path");
      if (fromAttr) add(fromAttr, root);

      const link = root.querySelector(
        "a[title*='/'], a[href*='/blob/'], a[href*='/pull/'][href*='/files'], .file-info a, h3 a, h2 a"
      );
      if (link) {
        add(link.getAttribute("title") || link.textContent, link);
      }

      // Visible header text like "apps/foo/bar.ts"
      const header = root.querySelector(
        ".file-header, [class*='file-header'], [class*='FileHeader'], h3, h2"
      );
      if (header) {
        const text = (header.textContent || "").replace(/\s+/g, " ").trim();
        const m = text.match(/([\w./\-]+\/[\w./\-]+\.[a-z0-9]+)/i);
        if (m) add(m[1], root);
      }
    });

    // File tree / filter list on the side
    document.querySelectorAll(
      '[data-testid="file-tree-item"], [role="treeitem"], .js-diff-progressive-loader a, a[href*="#diff-"]'
    ).forEach((a) => {
      add(a.getAttribute("title") || a.textContent, a);
    });

    // Last resort: any link whose text looks like a repo path
    document.querySelectorAll("a").forEach((a) => {
      const t = (a.getAttribute("title") || a.textContent || "").trim();
      if (looksLikeRepoPath(t)) add(t, a);
    });

    const seen = new Set();
    return entries.filter((e) => {
      const k = normPath(e.path);
      if (seen.has(k)) return false;
      seen.add(k);
      return true;
    });
  }

  function getFileEntries() {
    return collectPaths();
  }

  function isDiffRoot(el) {
    if (!el) return false;
    return !!(
      el.id?.startsWith?.("diff-") ||
      el.classList?.contains?.("file") ||
      el.matches?.("[data-file-type], copilot-diff-entry, [data-testid='file-diff-entry']")
    );
  }

  function resolveDiffRoot(entry) {
    if (!entry?.root) return null;
    if (isDiffRoot(entry.root)) return entry.root;

    // Tree / link hit — jump to matching diff on the page
    const want = normPath(entry.path);
    const diffs = document.querySelectorAll(
      '[id^="diff-"], .file, [data-file-type], copilot-diff-entry'
    );
    for (const d of diffs) {
      const attrs = [
        d.getAttribute("data-tagsearch-path"),
        d.getAttribute("data-path"),
        d.getAttribute("data-file-path"),
        d.querySelector("a[title]")?.getAttribute("title"),
        d.querySelector(".file-info a, h3 a, a")?.textContent,
      ];
      for (const a of attrs) {
        if (a && (normPath(a) === want || pathEndsWith(a, entry.path) || pathEndsWith(entry.path, a)))
          return d;
      }
      const text = (d.textContent || "").replace(/\s+/g, " ");
      if (text.toLowerCase().includes(basename(entry.path))) {
        // weak match — only if basename is unique-ish
        const m = text.match(/([\w./\-]+\/[\w./\-]+\.[a-z0-9]+)/i);
        if (m && scoreMatch(m[1], entry.path) >= 80) return d;
      }
    }
    return entry.root;
  }

  function findToggle(root) {
    if (!root) return null;
    const header =
      root.querySelector(".file-header, [class*='FileHeader'], [class*='file-header']") || root;

    const candidates = [
      ...header.querySelectorAll("button"),
      ...root.querySelectorAll("button"),
    ];

    const byLabel = candidates.find((b) => {
      const label = `${b.getAttribute("aria-label") || ""} ${b.getAttribute("title") || ""}`.toLowerCase();
      return /expand|collapse|show|hide|fold|chevron|caret|toggle|details/.test(label);
    });
    if (byLabel) return byLabel;

    return (
      root.querySelector('[data-testid="expand-button"]') ||
      root.querySelector(".js-details-target") ||
      root.querySelector("button[aria-expanded]") ||
      // GitHub often uses a button that only contains an octicon triangle
      header.querySelector("button:has(svg)") ||
      header.querySelector("[role='button']")
    );
  }

  function hasDiffBody(root) {
    return !!root.querySelector(
      "table.diff-table, .js-file-content, .data, .blob-code, [class*='Diff-module'], [class*='diff-table'], [data-code-marker]"
    );
  }

  function isExpanded(root) {
    if (!root) return false;
    const t = findToggle(root);
    if (t?.getAttribute("aria-expanded") === "true") return true;
    if (t?.getAttribute("aria-expanded") === "false") return false;
    if (root.classList?.contains("open") || root.classList?.contains("Details--on")) return true;
    // If we can see code lines, treat as expanded
    if (hasDiffBody(root)) {
      const body = root.querySelector(
        "table.diff-table, .js-file-content, .data, [class*='diff-table']"
      );
      if (body && body.getClientRects().length > 0) return true;
    }
    return false;
  }

  async function clickEl(el) {
    if (!el) return;
    const ptr = window.__xscribePointer;
    if (ptr) {
      const { x, y } = await ptr.moveToEl(el, 280);
      ptr.ripple(x, y);
    }
    el.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true, cancelable: true, view: window }));
    el.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, cancelable: true, view: window }));
    el.click();
    el.dispatchEvent(new MouseEvent("mouseup", { bubbles: true, cancelable: true, view: window }));
    el.dispatchEvent(new MouseEvent("pointerup", { bubbles: true, cancelable: true, view: window }));
  }

  async function setExpanded(root, open) {
    if (!root) return;
    for (let attempt = 0; attempt < 3; attempt++) {
      if (isExpanded(root) === open) return;

      const t = findToggle(root);
      if (t) {
        await clickEl(t);
      } else {
        // Fallback: click the file header row
        const header =
          root.querySelector(".file-header, [class*='FileHeader'], summary, h3") || root;
        await clickEl(header);
      }
      await sleep(450 + attempt * 200);
    }
  }

  async function ensureFilesLoaded() {
    for (let i = 0; i < 6; i++) {
      if (getFileEntries().length >= 1) return;
      window.scrollBy(0, Math.max(600, window.innerHeight * 0.8));
      await sleep(200);
    }
    window.scrollTo({ top: 0, behavior: "auto" });
    await sleep(150);
  }

  function scoreMatch(entryPath, wanted) {
    const a = normPath(entryPath);
    const b = normPath(wanted);
    if (a === b) return 100;
    if (pathEndsWith(a, b) || pathEndsWith(b, a)) return 90;
    const seg = b.split("/").filter(Boolean);
    if (seg.length >= 2 && a.endsWith(seg.slice(-2).join("/"))) return 80;
    if (basename(a) === basename(b) && basename(b).length > 4) return 50;
    return 0;
  }

  function findFile(path, entries) {
    const list = entries || getFileEntries();
    let best = null;
    let bestScore = 0;
    for (const e of list) {
      let s = scoreMatch(e.path, path);
      // Prefer real diff containers over sidebar tree hits
      if (s >= 50 && isDiffRoot(e.root)) s += 15;
      if (s > bestScore) {
        bestScore = s;
        best = e;
      }
    }
    return bestScore >= 50 ? best : null;
  }

  function getPrFileList() {
    return getFileEntries().map((e) => e.path);
  }

  async function scrollToEl(el) {
    el.scrollIntoView({ behavior: "smooth", block: "center", inline: "nearest" });
    await sleep(400);
  }

  function lineEls(root, lineNum) {
    const sel = [
      `[data-line-number="${lineNum}"]`,
      `#LC${lineNum}`,
      `#R${lineNum}`,
      `td[data-line-number="${lineNum}"]`,
      `[data-line-number='${lineNum}']`,
    ];
    for (const s of sel) {
      const found = root.querySelectorAll(s);
      if (found.length) return [...found];
    }
    // Fallback: row numbers in left gutter
    for (const el of root.querySelectorAll("td, span, div")) {
      if ((el.textContent || "").trim() === String(lineNum) && el.closest("tr, [class*='line']"))
        return [el];
    }
    return [];
  }

  function highlightRange(root, ranges) {
    clearHighlights();
    for (const r of ranges) {
      for (let ln = r.start; ln <= r.end; ln++) {
        for (const el of lineEls(root, ln)) {
          const row = el.closest("tr") || el;
          row.classList.add(`${NS}-highlight`);
        }
      }
    }
  }

  function clearHighlights() {
    document.querySelectorAll(`.${NS}-highlight`).forEach((n) =>
      n.classList.remove(`${NS}-highlight`)
    );
  }

  function sleep(ms) {
    return new Promise((r) => setTimeout(r, ms));
  }

  let lastPath = null;

  async function runStep(payload, skipAnim) {
    const { path, lineRanges } = payload;
    if (!path) return { ok: false, error: "no path" };
    if (!isPrPage()) {
      return { ok: false, error: `not a PR files/changes page: ${location.pathname}` };
    }

    await ensureFilesLoaded();
    let entries = getFileEntries();
    let entry = findFile(path, entries);

    if (!entry) {
      await ensureFilesLoaded();
      entries = getFileEntries();
      entry = findFile(path, entries);
    }

    if (!entry) {
      const n = entries.length;
      const sample = entries.slice(0, 5).map((e) => e.path).join(" | ");
      const hint = n === 0
        ? "No files detected in DOM — hard-refresh the PR Files tab, re-bind, retry."
        : `Saw ${n} files. Sample: ${sample}`;
      return { ok: false, error: `file not found: ${path} (${hint})` };
    }

    const root = resolveDiffRoot(entry);
    entry = { ...entry, root };

    if (lastPath && lastPath !== entry.path) {
      const prev = findFile(lastPath, entries);
      if (prev) {
        const prevRoot = resolveDiffRoot(prev);
        if (prevRoot && isExpanded(prevRoot)) await setExpanded(prevRoot, false);
      }
    }

    await scrollToEl(root);
    const ptr = window.__xscribePointer;
    if (ptr && !skipAnim) await ptr.moveToEl(root, 350);

    // Always try expand — don't trust isExpanded alone
    await setExpanded(root, true);
    await sleep(300);

    if (!isExpanded(root)) {
      // One more hard attempt: click first clickable in header
      const header = root.querySelector(".file-header, summary, [class*='FileHeader']") || root;
      await clickEl(header);
      await sleep(500);
    }

    if (lineRanges?.length) {
      const first = lineRanges[0].start;
      let targets = lineEls(root, first);
      if (!targets[0]) {
        await sleep(400);
        targets = lineEls(root, first);
      }
      if (targets[0]) {
        await scrollToEl(targets[0]);
        if (ptr && !skipAnim) await ptr.moveToEl(targets[0], 300);
      }
      highlightRange(root, lineRanges);
    }

    lastPath = entry.path;
    return {
      ok: true,
      path: entry.path,
      expanded: isExpanded(root),
      matched: entries.length,
    };
  }

  function cleanup() {
    lastPath = null;
    clearHighlights();
    window.__xscribePointer?.cleanup();
  }

  window.__xscribeGithubPr = {
    isPrPage,
    getPrFileList,
    findFile,
    runStep,
    cleanup,
  };
})();
