// Light or dark: follows the system unless this browser chose one (localStorage "library.theme" = light | dark).
// Loaded in <head> without defer, so the chosen theme is in place before anything is drawn; site.js adds the buttons.
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
})();
