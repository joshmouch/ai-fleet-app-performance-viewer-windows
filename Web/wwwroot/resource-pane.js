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
