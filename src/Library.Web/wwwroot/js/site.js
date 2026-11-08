// Small progressive enhancements; every page works without this file.
(() => {
  // On narrow screens the folder tree starts collapsed so the models come first.
  const panel = document.querySelector(".tree-panel");
  if (panel && window.matchMedia("(max-width: 800px)").matches) {
    panel.removeAttribute("open");
  }

  // While the first scan runs, reload until there is something to show.
  const reload = document.querySelector("[data-reload]");
  if (reload) {
    const seconds = Number(reload.getAttribute("data-reload")) || 3;
    setTimeout(() => window.location.reload(), seconds * 1000);
  }
})();
