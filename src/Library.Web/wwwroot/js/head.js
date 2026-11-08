// Loaded in <head> without defer: what has to be in place before the page is first drawn.
// 1. Light or dark: follows the system unless this browser chose one (localStorage "library.theme" = light | dark);
//    site.js adds the buttons.
// 2. Page transitions (site.css @view-transition): the preview you click grows into the file page, and back.
(() => {
  const key = "library.theme";
  const root = document.documentElement;
  const systemDark = window.matchMedia("(prefers-color-scheme: dark)");
  const barColours = { light: "#f5f4f1", dark: "#131416" };

  const choice = () => {
    try {
      const value = localStorage.getItem(key);
      return value === "light" || value === "dark" ? value : "system";
    } catch {
      return "system";
    }
  };
  const effective = () => {
    const chosen = choice();
    return chosen === "system" ? (systemDark.matches ? "dark" : "light") : chosen;
  };

  const apply = (chosen) => {
    if (chosen === "system") delete root.dataset.theme;
    else root.dataset.theme = chosen;
    // The browser bar colour (phones, installed app) follows too.
    for (const meta of document.querySelectorAll('meta[name="theme-color"]')) {
      meta.dataset.system ??= meta.content;
      meta.content = chosen === "system" ? meta.dataset.system : barColours[chosen];
    }
  };

  window.libraryTheme = {
    choice,
    effective,
    set(chosen) {
      try {
        if (chosen === "system") localStorage.removeItem(key);
        else localStorage.setItem(key, chosen);
      } catch {
        // Storage blocked: the choice still applies to this page.
      }
      apply(chosen);
      document.dispatchEvent(new CustomEvent("library:theme"));
    },
  };

  systemDark.addEventListener("change", () => document.dispatchEvent(new CustomEvent("library:theme")));
  apply(choice());

  // --- Page transitions --------------------------------------------------------------------------------------
  // Only one element per page may carry the "model-preview" name (site.css .vt-preview); the file page's own
  // preview always has it. Browsers without these events simply cross-fade or switch pages as usual.
  const samePath = (link, url) => {
    try {
      return decodeURIComponent(new URL(link, location.href).pathname) === decodeURIComponent(new URL(url).pathname);
    } catch {
      return false;
    }
  };
  // A transition can be skipped (window resized, page hidden): that is fine, so its promises must not throw.
  const quiet = (transition) => {
    transition?.ready.catch(() => {});
    transition?.finished.catch(() => {});
  };
  const clearAfter = (transition, image) => {
    const clear = () => image.classList.remove("vt-preview");
    transition.finished.then(clear, clear);
  };
  const cardImage = (url) =>
    [...document.querySelectorAll(".card-thumb[href]")].find((a) => samePath(a.getAttribute("href"), url))?.querySelector("img");

  // Leaving a list for a file page: name the preview that was clicked.
  window.addEventListener("pageswap", (event) => {
    quiet(event.viewTransition);
    const to = event.activation?.entry?.url;
    if (!event.viewTransition || !to || document.querySelector(".file-page")) return;
    const image = cardImage(to);
    if (!image) return;
    image.classList.add("vt-preview");
    clearAfter(event.viewTransition, image);
  });

  // Coming back from a file page: the large preview shrinks into its card, when that card is on screen.
  window.addEventListener("pagereveal", (event) => {
    quiet(event.viewTransition);
    const from = window.navigation?.activation?.from?.url;
    if (!event.viewTransition || !from || !new URL(from).pathname.startsWith("/file/")) return;
    const image = cardImage(from);
    const box = image?.getBoundingClientRect();
    if (!image || !box || box.bottom < 0 || box.top > innerHeight) return;
    image.classList.add("vt-preview");
    clearAfter(event.viewTransition, image);
  });
})();
