// Ghost presenter pointer — classic OS arrow; never moves the real cursor.
(function () {
  const NS = "xscribe-pr-walker";
  const STYLE_ID = `${NS}-style`;

  // Upgrade in place after extension reload / re-inject
  try { window.__xscribePointer?.cleanup?.(); } catch { /* ignore */ }

  function ensureStyle() {
    const old = document.getElementById(STYLE_ID);
    if (old) old.remove();
    const s = document.createElement("style");
    s.id = STYLE_ID;
    s.textContent = `
      .${NS}-pointer {
        position: fixed; z-index: 2147483647; pointer-events: none;
        width: 20px; height: 20px;
        filter: drop-shadow(1px 1px 1px rgba(0,0,0,.35));
      }
      .${NS}-ripple {
        position: fixed; z-index: 2147483646; pointer-events: none;
        width: 20px; height: 20px; border-radius: 50%;
        border: 1.5px solid rgba(0,0,0,.45); background: rgba(0,0,0,.08);
        transform: translate(-50%, -50%) scale(0.4);
        animation: ${NS}-ripple .4s ease-out forwards;
      }
      @keyframes ${NS}-ripple {
        to { transform: translate(-50%, -50%) scale(2); opacity: 0; }
      }
      .${NS}-highlight td, .${NS}-highlight {
        background: rgba(255, 213, 0, 0.28) !important;
        outline: 1px solid rgba(200, 160, 0, 0.55);
      }
    `;
    document.documentElement.appendChild(s);
  }

  // Classic black mouse arrow
  const svg = `<svg class="${NS}-pointer" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
    <path fill="#000" stroke="#fff" stroke-width="1.25" stroke-linejoin="round"
      d="M5.5 3.5 L5.5 18.5 L9.2 14.9 L12.2 21.2 L14.4 20.2 L11.3 13.7 L16.5 13.7 Z"/>
  </svg>`;

  let el = null;
  let abort = false;

  function getEl() {
    ensureStyle();
    document.querySelectorAll(`.${NS}-pointer`).forEach((n) => {
      if (n !== el) n.remove();
    });
    if (!el || !el.isConnected) {
      const wrap = document.createElement("div");
      wrap.innerHTML = svg;
      el = wrap.firstElementChild;
      document.documentElement.appendChild(el);
      el.style.left = "0px";
      el.style.top = "0px";
      el.style.display = "none";
    }
    return el;
  }

  function centerOf(target) {
    const r = target.getBoundingClientRect();
    return { x: r.left + Math.min(24, r.width / 2), y: r.top + Math.min(16, r.height / 2) };
  }

  function ease(t) {
    return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
  }

  async function moveTo(x, y, ms = 500) {
    const p = getEl();
    p.style.display = "block";
    const startX = parseFloat(p.style.left) || x;
    const startY = parseFloat(p.style.top) || y;
    const t0 = performance.now();
    return new Promise((resolve) => {
      function frame(now) {
        if (abort) return resolve();
        const t = Math.min(1, (now - t0) / ms);
        const e = ease(t);
        p.style.left = `${startX + (x - startX) * e}px`;
        p.style.top = `${startY + (y - startY) * e}px`;
        if (t < 1) requestAnimationFrame(frame);
        else resolve();
      }
      requestAnimationFrame(frame);
    });
  }

  function ripple(x, y) {
    ensureStyle();
    const r = document.createElement("div");
    r.className = `${NS}-ripple`;
    r.style.left = `${x}px`;
    r.style.top = `${y}px`;
    document.documentElement.appendChild(r);
    setTimeout(() => r.remove(), 450);
  }

  async function moveToEl(target, ms) {
    const { x, y } = centerOf(target);
    await moveTo(x, y, ms);
    return { x, y };
  }

  function hide() {
    if (el) el.style.display = "none";
  }

  function cleanup() {
    abort = true;
    hide();
    document.querySelectorAll(`.${NS}-highlight`).forEach((n) =>
      n.classList.remove(`${NS}-highlight`)
    );
    document.querySelectorAll(`.${NS}-ripple`).forEach((n) => n.remove());
    document.querySelectorAll(`.${NS}-pointer`).forEach((n) => n.remove());
    el = null;
    abort = false;
  }

  window.__xscribePointer = { moveTo, moveToEl, ripple, hide, cleanup, NS };
})();
