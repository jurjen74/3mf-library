// Progressive enhancements; every page works without this file.
(() => {
  const isTyping = (event) =>
    event.target instanceof HTMLElement &&
    (event.target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(event.target.tagName));

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

  // "/" jumps to the search box.
  const search = document.querySelector(".topbar-search input");
  document.addEventListener("keydown", (event) => {
    if (event.key === "/" && search && !isTyping(event)) {
      event.preventDefault();
      search.focus();
      search.select();
    }
  });

  // File page: switch plates in place instead of reloading.
  const stage = document.getElementById("stage-image");
  const thumbs = document.querySelectorAll(".plate-thumb");
  if (stage && thumbs.length) {
    const label = document.getElementById("stage-label");
    const details = document.getElementById("stage-details");
    const objects = document.getElementById("stage-objects");
    const select = (thumb) => {
      stage.src = thumb.dataset.src;
      stage.alt = thumb.dataset.label;
      if (label) label.textContent = thumb.dataset.label;
      if (details) details.textContent = thumb.dataset.details;
      if (objects) {
        objects.textContent = thumb.dataset.objects;
        objects.parentElement.hidden = !thumb.dataset.objects;
      }
      thumbs.forEach((t) => t.removeAttribute("aria-current"));
      thumb.setAttribute("aria-current", "true");
      history.replaceState(null, "", thumb.getAttribute("href"));
    };
    thumbs.forEach((thumb) =>
      thumb.addEventListener("click", (event) => {
        event.preventDefault();
        select(thumb);
      }),
    );
  }

  // Photo lightbox with previous/next.
  const photos = [...document.querySelectorAll("a[data-lightbox]")];
  let dialog = null;
  if (photos.length) {
    dialog = document.createElement("dialog");
    dialog.className = "lightbox";
    dialog.innerHTML =
      '<img alt="">' +
      '<button type="button" class="lightbox-close" aria-label="Close">×</button>' +
      '<button type="button" class="lightbox-prev" aria-label="Previous photo">‹</button>' +
      '<button type="button" class="lightbox-next" aria-label="Next photo">›</button>' +
      '<span class="lightbox-count"></span>';
    document.body.append(dialog);
    const image = dialog.querySelector("img");
    const count = dialog.querySelector(".lightbox-count");
    let current = 0;
    const show = (index) => {
      current = (index + photos.length) % photos.length;
      image.src = photos[current].href;
      image.alt = photos[current].querySelector("img")?.alt ?? "";
      count.textContent = `${current + 1} / ${photos.length}`;
    };
    photos.forEach((link, index) =>
      link.addEventListener("click", (event) => {
        event.preventDefault();
        show(index);
        dialog.showModal();
      }),
    );
    dialog.querySelector(".lightbox-close").addEventListener("click", () => dialog.close());
    dialog.querySelector(".lightbox-prev").addEventListener("click", () => show(current - 1));
    dialog.querySelector(".lightbox-next").addEventListener("click", () => show(current + 1));
    dialog.addEventListener("click", (event) => {
      if (event.target === dialog) dialog.close();
    });
    dialog.addEventListener("keydown", (event) => {
      if (event.key === "ArrowLeft") show(current - 1);
      if (event.key === "ArrowRight") show(current + 1);
    });
  }

  // File page: ← / → go to the previous / next model in the folder.
  document.addEventListener("keydown", (event) => {
    if (isTyping(event) || dialog?.open || event.altKey || event.ctrlKey || event.metaKey) return;
    const link = document.querySelector(`a[data-key="${event.key}"]`);
    if (link) window.location.href = link.href;
  });
})();
