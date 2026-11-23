const stepButtons = [...document.querySelectorAll("[data-step]")];
const previews = stepButtons.map((button) => document.getElementById(button.getAttribute("aria-controls")));

function showStep(selectedIndex, animate = false) {
  stepButtons.forEach((button, index) => {
    const active = index === selectedIndex;
    button.setAttribute("aria-pressed", String(active));
    previews[index].hidden = !active;
    previews[index].classList.toggle("is-entering", active && animate);
  });
}

showStep(0);
document.querySelector(".workflow-controls").hidden = false;
stepButtons.forEach((button, index) => {
  button.addEventListener("click", () => showStep(index, true));
});

const hero = document.querySelector(".hero-figure");
const frame = hero.querySelector(".preview-frame");
const allowTilt = matchMedia("(hover: hover) and (pointer: fine) and (prefers-reduced-motion: no-preference)");

hero.addEventListener("pointermove", (event) => {
  if (!allowTilt.matches || event.pointerType !== "mouse") return;
  const bounds = hero.getBoundingClientRect();
  frame.style.setProperty("--pitch", `${(0.5 - (event.clientY - bounds.top) / bounds.height) * 4}deg`);
  frame.style.setProperty("--yaw", `${((event.clientX - bounds.left) / bounds.width - 0.5) * 5}deg`);
});

hero.addEventListener("pointerleave", () => {
  frame.style.removeProperty("--pitch");
  frame.style.removeProperty("--yaw");
});

async function showVisitCount() {
  const count = document.getElementById("visit-count");
  const site = window.apexCodeAnalytics;
  const homepage = document.querySelector('link[rel="canonical"]').href;
  const currentPage = new URL(location.href);
  const pageUrl = new URL("./", currentPage).href;
  if (pageUrl !== homepage || !/^[a-z0-9][a-z0-9-]*$/.test(site ?? "")) return;

  const origin = `https://${site}.goatcounter.com`;
  const beacon = document.createElement("script");
  beacon.src = "https://gc.zgo.at/count.js";
  beacon.async = true;
  beacon.dataset.goatcounter = `${origin}/count`;
  document.head.append(beacon);

  try {
    const response = await fetch(`${origin}/counter/TOTAL.json`, {
      credentials: "omit",
      signal: AbortSignal.timeout(5000),
    });
    if (!response.ok) return;
    const total = await response.json();
    if (typeof total.count === "string" && /^[\d,.\s]+$/.test(total.count) && /\d/.test(total.count)) {
      count.textContent = total.count;
    }
  } catch {
    // Keep the initial unavailable label if the provider cannot be reached.
  }
}

showVisitCount();
