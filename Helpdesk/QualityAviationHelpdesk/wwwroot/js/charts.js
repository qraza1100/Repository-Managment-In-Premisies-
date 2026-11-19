// Small SVG charts for the dashboard: a line chart, horizontal bars and a donut. No library, nothing loaded from
// the internet, and no HTML parsing: every element is created with DOM methods and every text is textContent.
(function () {
    "use strict";

    var NS = "http://www.w3.org/2000/svg";
    var QAC = window.QAC = window.QAC || {};

    function node(name, attrs, text) {
        var el = document.createElementNS(NS, name);
        Object.keys(attrs || {}).forEach(function (key) { el.setAttribute(key, attrs[key]); });
        if (text !== undefined && text !== null) el.textContent = String(text);
        return el;
    }

    function clear(el) {
        while (el.firstChild) el.removeChild(el.firstChild);
    }

    function svg(width, height) {
        return node("svg", { viewBox: "0 0 " + width + " " + height, role: "img", preserveAspectRatio: "xMidYMid meet" });
    }

    // 1, 2 or 5 times a power of ten, at least the value
    function niceMax(value) {
        value = Math.max(4, value);
        var power = Math.pow(10, Math.floor(Math.log10(value)));
        var steps = [1, 2, 5, 10];
        for (var i = 0; i < steps.length; i++) if (steps[i] * power >= value) return steps[i] * power;
        return 10 * power;
    }

    function empty(el, height) {
        var box = svg(640, height || 60);
        box.appendChild(node("text", { x: 320, y: (height || 60) / 2, "text-anchor": "middle", "class": "chart-axis" }, "No data yet"));
        el.appendChild(box);
    }

    // data: { labels: [...], series: [{ name, values: [...], cls: "a" | "b" }] }
    QAC.line = function (el, data) {
        clear(el);
        var labels = data.labels || [];
        var series = (data.series || []).filter(function (s) { return s.values && s.values.length; });
        if (labels.length === 0 || series.length === 0) { empty(el, 120); return; }

        var W = 640, H = 240, left = 36, right = 12, top = 12, bottom = 28;
        var plotW = W - left - right, plotH = H - top - bottom;
        var largest = 0;
        series.forEach(function (s) { s.values.forEach(function (v) { largest = Math.max(largest, v); }); });
        var max = niceMax(largest);
        var n = labels.length;
        var x = function (i) { return left + (n === 1 ? plotW / 2 : i * plotW / (n - 1)); };
        var y = function (v) { return top + plotH * (1 - v / max); };

        var box = svg(W, H);
        for (var g = 0; g <= 4; g++) {
            var value = max * g / 4;
            box.appendChild(node("line", { x1: left, x2: W - right, y1: y(value), y2: y(value), "class": "chart-grid" }));
            box.appendChild(node("text", { x: left - 6, y: y(value) + 4, "text-anchor": "end", "class": "chart-axis" }, Math.round(value)));
        }

        // At most ~8 labels; the last one only when it does not crowd the one before it
        var every = Math.max(1, Math.ceil(n / 8));
        labels.forEach(function (label, i) {
            var regular = i % every === 0;
            var last = i === n - 1 && (n - 1) % every >= every / 2;
            if (!regular && !last) return;
            box.appendChild(node("text", { x: x(i), y: H - 6, "text-anchor": "middle", "class": "chart-axis" }, label));
        });

        series.forEach(function (s, index) {
            var points = s.values.map(function (v, i) { return x(i).toFixed(1) + "," + y(v).toFixed(1); });
            if (index === 0) {
                box.appendChild(node("path", {
                    d: "M" + x(0).toFixed(1) + "," + y(0) + " L" + points.join(" L") + " L" + x(s.values.length - 1).toFixed(1) + "," + y(0) + " Z",
                    "class": "chart-area-a"
                }));
            }
            box.appendChild(node("path", { d: "M" + points.join(" L"), "class": "chart-line-" + s.cls }));
        });

        // Dots last, so each sits on top of every line and its tooltip can be reached
        series.forEach(function (s) {
            s.values.forEach(function (v, i) {
                var dot = node("circle", { cx: x(i), cy: y(v), r: n > 40 ? 2 : 3.5, "class": "chart-dot chart-line-" + s.cls });
                dot.appendChild(node("title", null, s.name + " · " + labels[i] + ": " + v));
                box.appendChild(dot);
            });
        });

        el.appendChild(box);
    };

    // items: [{ label, value, cls }]
    QAC.hbar = function (el, items) {
        clear(el);
        items = items || [];
        if (items.length === 0) { empty(el, 40); return; }

        var max = Math.max.apply(null, items.map(function (i) { return i.value; }).concat([1]));
        var rowH = 30, height = items.length * rowH + 8;
        var box = svg(640, height);
        items.forEach(function (item, i) {
            var y0 = i * rowH + 4;
            var label = String(item.label || "—");
            if (label.length > 24) label = label.slice(0, 23) + "…";
            box.appendChild(node("text", { x: 0, y: y0 + 15, "dominant-baseline": "middle", "class": "chart-label" }, label));
            box.appendChild(node("rect", { x: 180, y: y0 + 10, width: 390, height: 10, rx: 5, "class": "chart-bar-track" }));
            var bar = node("rect", { x: 180, y: y0 + 10, width: item.value > 0 ? Math.max(3, 390 * item.value / max) : 0, height: 10, rx: 5, "class": "chart-bar " + (item.cls || "") });
            bar.appendChild(node("title", null, item.label + ": " + item.value));
            box.appendChild(bar);
            box.appendChild(node("text", { x: 582, y: y0 + 15, "dominant-baseline": "middle", "class": "chart-value" }, item.value));
        });
        el.appendChild(box);
    };

    // items: [{ label, value, cls }]; the total is written in the middle
    QAC.donut = function (el, items, centerLabel) {
        clear(el);
        items = items || [];
        var total = items.reduce(function (sum, i) { return sum + (i.value || 0); }, 0);
        var r = 80, C = 2 * Math.PI * r;

        var wrap = document.createElement("div");
        wrap.className = "chart-donut";
        var box = svg(220, 220);
        box.appendChild(node("circle", { cx: 110, cy: 110, r: r, fill: "none", "stroke-width": 26, "class": "chart-bar-track" }));

        var ring = node("g", { transform: "rotate(-90 110 110)" });
        var offset = 0;
        items.forEach(function (item) {
            if (!(item.value > 0)) return;
            var length = C * item.value / total;
            var arc = node("circle", {
                cx: 110, cy: 110, r: r, fill: "none", "stroke-width": 26, "class": item.cls || "c7",
                "stroke-dasharray": length.toFixed(2) + " " + (C - length).toFixed(2), "stroke-dashoffset": (-offset).toFixed(2)
            });
            arc.appendChild(node("title", null, item.label + ": " + item.value + " (" + Math.round(100 * item.value / total) + "%)"));
            ring.appendChild(arc);
            offset += length;
        });
        box.appendChild(ring);
        box.appendChild(node("text", { x: 110, y: 120, "text-anchor": "middle", "class": "chart-total" }, total));
        box.appendChild(node("text", { x: 110, y: 140, "text-anchor": "middle", "class": "chart-axis" }, centerLabel || ""));
        wrap.appendChild(box);

        var legend = document.createElement("div");
        legend.className = "legend";
        items.forEach(function (item) {
            var span = document.createElement("span");
            var swatch = document.createElement("i");
            swatch.className = "swatch " + (item.cls || "c7");
            span.appendChild(swatch);
            span.appendChild(document.createTextNode(item.label + " · " + item.value));
            legend.appendChild(span);
        });
        wrap.appendChild(legend);
        el.appendChild(wrap);
    };
})();
