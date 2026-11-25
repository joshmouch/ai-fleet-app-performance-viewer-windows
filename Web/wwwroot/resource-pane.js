let paneDrag;

function resizeResourcePane(dock, height) {
    const cards = dock.previousElementSibling;
    const available = dock.offsetHeight + (cards?.classList.contains("resource-children") ? cards.offsetHeight : 0);
    const minimum = Math.min(115, available);
    const maximum = Math.max(minimum, available - 45);
    dock.style.setProperty("--resource-pane-height", `${Math.min(maximum, Math.max(minimum, height))}px`);
    dock.dataset.resized = "true";
}

function resourceDock(target) {
    return target.closest("[data-resource-splitter]")?.closest(".resource-dock");
}

document.addEventListener("pointerdown", event => {
    const dock = resourceDock(event.target);
    if (!dock || event.button !== 0) return;
    paneDrag = { dock, y: event.clientY, height: dock.offsetHeight };
    event.target.setPointerCapture(event.pointerId);
});

document.addEventListener("pointermove", event => {
    if (paneDrag) resizeResourcePane(paneDrag.dock, paneDrag.height + paneDrag.y - event.clientY);
});

document.addEventListener("lostpointercapture", () => { paneDrag = undefined; });

document.addEventListener("keydown", event => {
    const dock = resourceDock(event.target);
    if (!dock || (event.key !== "ArrowUp" && event.key !== "ArrowDown")) return;
    event.preventDefault();
    resizeResourcePane(dock, dock.offsetHeight + (event.key === "ArrowUp" ? 12 : -12));
});

window.focusResourceScope = (section, cardId) => {
    const card = cardId ? section.querySelector(`[data-resource-node="${CSS.escape(cardId)}"]:not(.resource-overview)`) : null;
    (card?.querySelector(".resource-name") ?? section.querySelector(".resource-overview h2.resource-name"))?.focus();
};

const pathDialog = document.createElement("dialog");
pathDialog.className = "resource-path-dialog";
pathDialog.setAttribute("aria-labelledby", "resource-path-title");
pathDialog.innerHTML = `
    <h2 id="resource-path-title">Full file path</h2>
    <code></code>
    <form method="dialog"><button autofocus>Close</button></form>`;
document.body.append(pathDialog);

document.addEventListener("click", event => {
    const file = event.target.closest("[data-full-path]");
    if (!file) return;
    pathDialog.querySelector("code").textContent = file.dataset.fullPath;
    pathDialog.showModal();
});

const rowMenu = document.createElement("div");
rowMenu.className = "resource-row-menu";
rowMenu.hidden = true;
rowMenu.innerHTML = '<button type="button">Copy row</button><span role="status"></span>';
document.body.append(rowMenu);
const copyRowButton = rowMenu.querySelector("button");
const copyStatus = rowMenu.querySelector("[role=status]");
let copyText, copyReturnFocus;

function closeRowMenu(restoreFocus) {
    if (rowMenu.hidden) return;
    rowMenu.hidden = true;
    if (restoreFocus) copyReturnFocus?.focus();
}

function openRowMenu(row, x, y, trigger) {
    const headers = Array.from(row.closest("table").querySelectorAll("thead th"), cell => cell.dataset.copyLabel);
    copyText = Array.from(row.cells, (cell, i) => `${headers[i]}: ${cell.title || cell.textContent.trim() || "—"}`).join("\n");
    copyReturnFocus = trigger;
    copyStatus.textContent = "";
    rowMenu.hidden = false;
    rowMenu.style.left = `${Math.max(0, Math.min(x, innerWidth - rowMenu.offsetWidth))}px`;
    rowMenu.style.top = `${Math.max(0, Math.min(y, innerHeight - rowMenu.offsetHeight))}px`;
    copyRowButton.focus();
}

document.addEventListener("contextmenu", event => {
    const row = event.target.closest("[data-copy-row]");
    if (!row) return;
    event.preventDefault();
    openRowMenu(row, event.clientX, event.clientY, row.querySelector("[data-full-path]") ?? row);
});

document.addEventListener("keydown", event => {
    if (!rowMenu.hidden && event.key === "Escape") {
        event.preventDefault();
        closeRowMenu(true);
        return;
    }
    if (event.key !== "ContextMenu" && !(event.shiftKey && event.key === "F10")) return;
    const row = event.target.closest("[data-copy-row]");
    if (!row) return;
    event.preventDefault();
    const anchor = event.target.getBoundingClientRect();
    openRowMenu(row, anchor.left, anchor.bottom, event.target);
});

document.addEventListener("pointerdown", event => {
    if (!rowMenu.contains(event.target)) closeRowMenu(false);
});
rowMenu.addEventListener("focusout", event => {
    if (!rowMenu.contains(event.relatedTarget)) closeRowMenu(false);
});
copyRowButton.addEventListener("click", async () => {
    try {
        await navigator.clipboard.writeText(copyText);
        closeRowMenu(true);
    } catch {
        copyStatus.textContent = "Clipboard access is unavailable.";
    }
});
