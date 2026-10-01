const files = [...document.querySelectorAll("main details")];
const links = new Map([...document.querySelectorAll(".toc li a")].map(a => [a.hash.slice(1), a]));
for (const b of document.querySelectorAll("button[data-open]")) {
  b.addEventListener("click", () => {
    for (const d of files) d.open = b.dataset.open === "1";
  });
}
const wrap = document.getElementById("wrap");
wrap.addEventListener("click", () => {
  const on = document.body.classList.toggle("wrap");
  wrap.setAttribute("aria-pressed", String(on));
});
const reveal = () => {
  const t = location.hash && document.getElementById(location.hash.slice(1));
  if (t && t.tagName === "DETAILS") t.open = true;
};
addEventListener("hashchange", reveal);
reveal();

// Filter by path: hides files, their contents entries, and any section left empty.
const filter = document.getElementById("filter");
filter.addEventListener("input", () => {
  const q = filter.value.trim().toLowerCase();
  for (const d of files) {
    const path = d.querySelector("summary code").textContent.toLowerCase();
    const hide = q !== "" && !path.includes(q);
    d.hidden = hide;
    const a = links.get(d.id);
    if (a) a.parentElement.hidden = hide;
  }
  for (const s of document.querySelectorAll(".sec, .toc")) {
    s.hidden = !s.querySelector("details:not([hidden]), li:not([hidden])");
  }
  document.getElementById("none").hidden = files.some(d => !d.hidden);
});

// The current file is the last one whose top has scrolled past the top of the viewport.
const current = () => {
  let at = -1;
  files.forEach((d, i) => { if (!d.hidden && d.getBoundingClientRect().top <= 40) at = i; });
  return at;
};
let marked = null;
const mark = () => {
  const d = files[current()];
  const a = d && links.get(d.id);
  if (a === marked) return;
  if (marked) marked.removeAttribute("aria-current");
  if (a) {
    a.setAttribute("aria-current", "true");
    const n = a.closest("nav"), r = a.getBoundingClientRect(), b = n.getBoundingClientRect();
    if (r.top < b.top || r.bottom > b.bottom) n.scrollTop += r.top - b.top - b.height / 2;
  }
  marked = a;
};
let queued = false;
addEventListener("scroll", () => {
  if (queued) return;
  queued = true;
  requestAnimationFrame(() => { queued = false; mark(); });
}, { passive: true });
mark();

// j / k: next and previous file; / focuses the filter. Enter on a file opens or closes it.
addEventListener("keydown", e => {
  if (e.metaKey || e.ctrlKey || e.altKey) return;
  if (e.target instanceof HTMLInputElement) {
    if (e.key === "Escape") e.target.blur();
    return;
  }
  if (e.key === "/") { e.preventDefault(); filter.focus(); return; }
  if (e.key !== "j" && e.key !== "k") return;
  const step = e.key === "j" ? 1 : -1;
  let i = current();
  if (step < 0 && i >= 0 && files[i].getBoundingClientRect().top < -1) i += 1;
  for (i += step; i >= 0 && i < files.length; i += step) {
    if (files[i].hidden) continue;
    const s = files[i].querySelector("summary");
    files[i].scrollIntoView({ block: "start", behavior: "instant" });
    s.focus({ preventScroll: true });
    break;
  }
});
