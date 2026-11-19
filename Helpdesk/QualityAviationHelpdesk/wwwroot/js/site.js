// Behaviour shared by every page. Loaded in <head> so the chosen theme applies before the page paints.
// No library and no HTML parsing: elements are created with DOM methods, text goes in with textContent.
(function () {
    "use strict";

    var root = document.documentElement;
    var KEY = "qa-theme";

    function readTheme() {
        try { return localStorage.getItem(KEY) || ""; } catch (e) { return ""; }
    }

    function applyTheme(mode) {
        if (mode === "light" || mode === "dark") root.dataset.theme = mode;
        else delete root.dataset.theme;
    }

    applyTheme(readTheme());

    function themeLabel(mode) {
        return mode === "light" ? "☀ Light" : mode === "dark" ? "☾ Dark" : "◐ Auto";
    }

    function setup() {
        // 1. Theme: light → dark → follow the PC
        document.querySelectorAll("[data-theme-toggle]").forEach(function (button) {
            button.textContent = themeLabel(readTheme());
            button.addEventListener("click", function () {
                var current = readTheme();
                var next = current === "light" ? "dark" : current === "dark" ? "" : "light";
                try { localStorage.setItem(KEY, next); } catch (e) { /* private window: still applies to this page */ }
                applyTheme(next);
                button.textContent = themeLabel(next);
            });
        });

        // 2. Forms someone is typing in are not reloaded under them
        document.querySelectorAll("form.track-dirty").forEach(function (form) {
            form.addEventListener("input", function () { form.classList.add("dirty"); });
            form.addEventListener("submit", function () { form.classList.remove("dirty"); });
        });

        // 3. Live updates: the server says "changed", the page fetches itself again
        var live = document.body.dataset.live;
        if (live && window.EventSource) {
            var timer = null, toast = null;
            var stream = new EventSource(live);
            stream.addEventListener("changed", function () {
                if (document.querySelector("form.dirty")) {
                    if (toast) return;
                    toast = document.createElement("div");
                    toast.className = "toast";
                    toast.setAttribute("role", "status");
                    toast.appendChild(document.createTextNode("This page has new updates."));
                    var reload = document.createElement("button");
                    reload.type = "button";
                    reload.className = "btn btn-sm";
                    reload.textContent = "Reload";
                    reload.addEventListener("click", function () { location.reload(); });
                    toast.appendChild(reload);
                    document.body.appendChild(toast);
                    return;
                }
                clearTimeout(timer);
                timer = setTimeout(function () { location.reload(); }, 1500);
            });
        }

        // 4. Confirmations
        document.addEventListener("click", function (event) {
            var target = event.target.closest("[data-confirm]");
            if (target && !window.confirm(target.dataset.confirm)) event.preventDefault();
        });

        // 5. Attachments: list what was chosen and refuse too many or too large before uploading
        document.querySelectorAll("input[type=file][data-max-mb]").forEach(function (input) {
            input.addEventListener("change", function () {
                var maxBytes = parseFloat(input.dataset.maxMb) * 1024 * 1024;
                var list = input.nextElementSibling && input.nextElementSibling.classList.contains("files") ? input.nextElementSibling : null;
                if (!list) {
                    list = document.createElement("ul");
                    list.className = "files";
                    input.parentNode.insertBefore(list, input.nextSibling);
                }
                while (list.firstChild) list.removeChild(list.firstChild);

                var files = Array.prototype.slice.call(input.files || []);
                var tooBig = [];
                files.forEach(function (file) {
                    var li = document.createElement("li");
                    li.className = "file-chip";
                    var size = file.size >= 1048576 ? (file.size / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(file.size / 1024)) + " KB";
                    li.textContent = file.name + " (" + size + ")";
                    list.appendChild(li);
                    if (file.size > maxBytes) tooBig.push(file.name);
                });

                var problem = files.length > 5 ? "Choose at most 5 files at a time."
                    : tooBig.length ? tooBig.join(", ") + " is larger than " + input.dataset.maxMb + " MB."
                    : "";
                input.setCustomValidity(problem);
                if (problem) input.reportValidity();
            });
        });

        // 6. Fields that only apply to some ticket types follow the chosen type
        var typeSelect = document.querySelector("select[data-type-select]");
        if (typeSelect) {
            var follow = function () {
                var chosen = typeSelect.value;
                document.querySelectorAll("[data-types]").forEach(function (block) {
                    var types = (block.dataset.types || "").split("\n").map(function (t) { return t.trim(); }).filter(Boolean);
                    var visible = types.length === 0 || types.indexOf(chosen) >= 0;
                    block.hidden = !visible;
                    block.querySelectorAll("input, select, textarea").forEach(function (field) {
                        if (!visible) {
                            if (field.required) { field.dataset.wasRequired = "1"; field.required = false; }
                            field.disabled = true;
                        } else {
                            field.disabled = false;
                            if (field.dataset.wasRequired === "1") field.required = true;
                        }
                    });
                });
            };
            typeSelect.addEventListener("change", follow);
            follow();
        }

        // 7. One click, one submission
        document.querySelectorAll("form").forEach(function (form) {
            form.addEventListener("submit", function (event) {
                if (event.defaultPrevented) return;
                setTimeout(function () {
                    form.querySelectorAll("button[type=submit], input[type=submit]").forEach(function (button) {
                        button.disabled = true;
                        if (button.tagName === "BUTTON") button.textContent = "Working…";
                    });
                }, 0);
            });
        });
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", setup);
    else setup();
})();
