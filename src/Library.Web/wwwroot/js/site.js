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

  // Forms and links that do something irreversible (or large) ask first.
  document.addEventListener("submit", (event) => {
    const message = event.target instanceof HTMLFormElement ? event.target.dataset.confirm : null;
    if (message && !window.confirm(message)) event.preventDefault();
  });
  document.addEventListener("click", (event) => {
    const link = event.target instanceof Element ? event.target.closest("a[data-confirm]") : null;
    if (link && !window.confirm(link.dataset.confirm)) event.preventDefault();
  });

  // "/" jumps to the search box.
  const search = document.querySelector(".topbar-search input");
  document.addEventListener("keydown", (event) => {
    if (event.key === "/" && search && !isTyping(event)) {
      event.preventDefault();
      search.focus();
      search.select();
    }
  });

  // File page: the 3D viewer, loaded only when asked for.
  const figure = document.querySelector(".stage[data-mesh]");
  const stage = document.getElementById("stage-image");
  const viewer3d = (() => {
    const toggle = document.getElementById("toggle-3d");
    const container = document.getElementById("stage-3d");
    const status = document.getElementById("stage-status");
    const details = document.getElementById("stage-details");
    if (!figure || !toggle || !container) return null;

    let viewer = null;
    let loading = null;
    let active = false;
    let detailsText = details?.textContent ?? "";

    const setStatus = (text) => {
      status.textContent = text;
      status.hidden = !text;
    };

    const load = async () => {
      loading?.abort();
      const controller = new AbortController();
      loading = controller;
      setStatus("Loading 3D model… large models can take a few seconds the first time.");
      try {
        if (!viewer) {
          const { Viewer } = await import("/js/viewer.js");
          viewer = new Viewer(container);
        }
        const result = await viewer.load(figure.dataset.mesh, controller.signal);
        if (controller.signal.aborted) return;
        if (result.triangles === 0) {
          setStatus("Nothing on this plate.");
        } else {
          setStatus("");
          const [x, y, z] = result.size.map((v) => (v < 10 ? v.toFixed(1) : Math.round(v)));
          // STLs already show their size; plates only learn it from the mesh.
          if (details && !detailsText.endsWith(" mm")) {
            details.textContent = [detailsText, `${x} × ${y} × ${z} mm`].filter(Boolean).join(" · ");
          }
        }
      } catch (error) {
        if (error.name !== "AbortError") setStatus(error.message || "Could not load the 3D model.");
      }
    };

    const show = (on) => {
      active = on;
      toggle.setAttribute("aria-pressed", String(on));
      toggle.querySelector("span").textContent = on ? "Image" : "3D";
      container.hidden = !on;
      if (stage) stage.hidden = on;
      if (on) {
        load();
      } else if (details) {
        details.textContent = detailsText;
      }
    };

    toggle.addEventListener("click", () => show(!active));
    return {
      // Called when another plate is picked; keeps the 3D view on the new plate.
      setMesh(url, text) {
        figure.dataset.mesh = url;
        detailsText = text;
        if (active) load();
        else if (details) details.textContent = text;
      },
    };
  })();

  // File page: switch plates in place instead of reloading.
  const thumbs = document.querySelectorAll(".plate-thumb");
  if (stage && thumbs.length) {
    const label = document.getElementById("stage-label");
    const details = document.getElementById("stage-details");
    const objects = document.getElementById("stage-objects");
    const select = (thumb) => {
      stage.src = thumb.dataset.src;
      stage.alt = thumb.dataset.label;
      if (label) label.textContent = thumb.dataset.label;
      if (viewer3d && thumb.dataset.mesh) viewer3d.setMesh(thumb.dataset.mesh, thumb.dataset.details);
      else if (details) details.textContent = thumb.dataset.details;
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

  // Star buttons: toggle a favourite without leaving the page.
  const token = () => document.querySelector('#request-token input[name="__RequestVerificationToken"]')?.value ?? "";
  document.addEventListener("click", async (event) => {
    const button = event.target instanceof Element ? event.target.closest("button[data-fav-path]") : null;
    if (!button) return;
    event.preventDefault();
    const body = new URLSearchParams({ path: button.dataset.favPath, __RequestVerificationToken: token() });
    try {
      const response = await fetch("/favourites?handler=Toggle", { method: "POST", body, headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(String(response.status));
      const { favourite } = await response.json();
      document.querySelectorAll("button[data-fav-path]").forEach((b) => {
        if (b.dataset.favPath !== button.dataset.favPath) return;
        b.setAttribute("aria-pressed", String(favourite));
        b.title = favourite ? "Remove from favourites" : "Add to favourites";
        const label = b.querySelector(".fav-label");
        if (label) label.textContent = favourite ? "Favourite" : "Add to favourites";
      });
    } catch {
      window.alert("Could not update your favourites. Reload the page and try again.");
    }
  });

  // "Open in <slicer>". Two routes:
  // - download link: fetch a fresh short-lived /dl link and hand it to the slicer's own URL scheme (formats checked
  //   against each slicer's source, and Manyfold's). Bambu Studio only opens 3MF this way; PrusaSlicer only downloads
  //   from Printables/Thingiverse, so it has no download route at all.
  // - local copy (Account → This computer): threemflib://open?path=<library path>&slicer=<id>, handled by the helper
  //   in tools/mac-opener or tools/windows-opener, which opens the synced file itself (slicers with helper: true).
  // Both choices are per browser (localStorage), not per account.
  const isComputer = window.matchMedia("(hover: hover) and (pointer: fine)").matches;
  const isMac = /Macintosh|Mac OS X/.test(navigator.userAgent);
  const openFile = (scheme) => (url) => `${scheme}://open?file=${encodeURIComponent(url)}`;
  const slicers = {
    bambu: {
      name: "Bambu Studio",
      link: (url) => (isMac ? `bambustudioopen://${encodeURIComponent(url)}` : openFile("bambustudio")(url)),
      linkKinds: ["3mf"],
      helper: true,
    },
    orca: { name: "OrcaSlicer", link: openFile("orcaslicer"), helper: true },
    prusa: { name: "PrusaSlicer", link: null, helper: true },
    cura: { name: "UltiMaker Cura", link: openFile("cura"), helper: true },
    creality: { name: "Creality Print", link: openFile("crealityprintlink"), helper: false },
    elegoo: { name: "ElegooSlicer", link: openFile("elegooslicer"), helper: false },
  };
  const canLink = (slicer, kind) => Boolean(slicer.link) && (!slicer.linkKinds || slicer.linkKinds.includes(kind));
  const localKey = "library.openLocalCopy";
  const slicerKey = "library.slicer";
  const stored = (key) => {
    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  };
  const store = (key, value) => {
    try {
      if (value === null) window.localStorage.removeItem(key);
      else window.localStorage.setItem(key, value);
    } catch {
      // Storage blocked (private mode): the defaults apply.
    }
  };
  const openLocal = () => isComputer && stored(localKey) === "1";
  const slicerId = () => (isComputer && slicers[stored(slicerKey)] ? stored(slicerKey) : "bambu");

  const deviceSettings = document.getElementById("this-computer");
  const localToggle = document.getElementById("open-local");
  const slicerSelect = document.getElementById("slicer-choice");
  if (deviceSettings && localToggle && slicerSelect && isComputer) {
    deviceSettings.hidden = false;
    localToggle.checked = openLocal();
    slicerSelect.value = slicerId();
    const explain = () => {
      const note = document.getElementById("slicer-note");
      if (!note) return;
      const chosen = slicers[slicerSelect.value];
      const text = !chosen.link && !localToggle.checked
        ? `${chosen.name} can't download from this site (it only accepts Printables and Thingiverse), so the button only appears with the local-copy option below.`
        : !chosen.helper && localToggle.checked
          ? `The helper can't start ${chosen.name} yet, so the button downloads a copy into ${chosen.name} instead of opening your synced file.`
          : chosen.linkKinds && !localToggle.checked
            ? `${chosen.name} only opens 3MF files from a link; for STLs use Download, or the local-copy option below.`
            : "";
      note.textContent = text;
      note.hidden = !text;
    };
    localToggle.addEventListener("change", () => {
      store(localKey, localToggle.checked ? "1" : null);
      explain();
    });
    slicerSelect.addEventListener("change", () => {
      store(slicerKey, slicerSelect.value === "bambu" ? null : slicerSelect.value);
      explain();
    });
    explain();
  }

  document.querySelectorAll("[data-slicer-link]").forEach((button) => {
    const slicer = slicers[slicerId()];
    const local = openLocal() && slicer.helper && Boolean(button.dataset.localPath);
    const label = button.querySelector(".slicer-label");
    if (!local && !canLink(slicer, button.dataset.kind)) {
      // This slicer can't take this file by link, and the local copy isn't available: Download is the way.
      button.hidden = true;
      return;
    }

    if (label) label.textContent = local ? `Open local copy in ${slicer.name}` : `Open in ${slicer.name}`;
    button.title = local
      ? `Opens your synced copy of this file in ${slicer.name} (via the 3MF Library Opener helper).`
      : slicerId() === "bambu"
        ? "Opens the file in Bambu Studio. The first time it asks whether to trust this site: answer Yes."
        : `Opens the file in ${slicer.name}, which downloads it first.`;

    button.addEventListener("click", async () => {
      if (local) {
        window.location.href = `threemflib://open?path=${encodeURIComponent(button.dataset.localPath)}&slicer=${slicerId()}`;
        return;
      }

      button.disabled = true;
      try {
        const response = await fetch(button.dataset.slicerLink, { headers: { Accept: "application/json" } });
        if (!response.ok) throw new Error(String(response.status));
        const { url } = await response.json();
        window.location.href = slicer.link(url);
      } catch {
        window.alert(`Could not prepare the file for ${slicer.name}. Try downloading it instead.`);
      } finally {
        setTimeout(() => (button.disabled = false), 1500);
      }
    });
  });

  // File page: ← / → go to the previous / next model in the folder.
  document.addEventListener("keydown", (event) => {
    if (isTyping(event) || dialog?.open || event.altKey || event.ctrlKey || event.metaKey) return;
    const link = document.querySelector(`a[data-key="${event.key}"]`);
    if (link) window.location.href = link.href;
  });
})();
