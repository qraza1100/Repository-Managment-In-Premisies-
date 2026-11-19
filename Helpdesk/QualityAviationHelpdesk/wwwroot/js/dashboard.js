// The management dashboard: fetches the figures (read-only GET) and draws them; redraws when the helpdesk changes.
// Everything is built with DOM methods and textContent: no data is ever parsed as HTML.
(function () {
    "use strict";

    var root = document.getElementById("dashboard");
    if (!root) return;

    var source = root.dataset.source;
    var loading = false;
    var again = false;

    function el(tag, cls, text) {
        var node = document.createElement(tag);
        if (cls) node.className = cls;
        if (text !== undefined && text !== null) node.textContent = String(text);
        return node;
    }

    function clear(node) {
        while (node.firstChild) node.removeChild(node.firstChild);
    }

    function hours(value) {
        if (value === null || value === undefined) return "—";
        if (value < 1) return Math.max(1, Math.round(value * 60)) + " min";
        if (value < 48) return value.toFixed(1).replace(/\.0$/, "") + " h";
        return (value / 24).toFixed(1).replace(/\.0$/, "") + " days";
    }

    function percent(value) {
        return value === null || value === undefined ? "—" : Math.round(value) + "%";
    }

    function ago(iso) {
        var minutes = Math.max(0, (Date.now() - new Date(iso).getTime()) / 60000);
        if (minutes < 1) return "just now";
        if (minutes < 60) return Math.floor(minutes) + " min ago";
        if (minutes < 1440) return Math.floor(minutes / 60) + " h ago";
        return Math.floor(minutes / 1440) + " d ago";
    }

    function initials(name) {
        var parts = String(name || "?").split(/[\s._-]+/).filter(Boolean);
        if (parts.length === 0) return "?";
        if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
        return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
    }

    var statusLabels = { "new": "New", "accepted": "Accepted", "in-progress": "In progress", "on-hold": "On hold", "resolved": "Resolved", "closed": "Closed", "rejected": "Rejected", "cancelled": "Cancelled" };
    var priorityLabels = { 1: "P1 Critical", 2: "P2 High", 3: "P3 Medium", 4: "P4 Low" };

    function kpis(d) {
        var box = document.getElementById("kpis");
        clear(box);
        var tiles = [
            ["Tickets raised", d.raised, "in the last " + rangeName(d.days), ""],
            ["Resolved", d.resolved, "completed by IT", "good"],
            ["Open now", d.open, d.overdue + " past their target", d.overdue > 0 ? "warn" : ""],
            ["Overdue", d.overdue, "open and past target", d.overdue > 0 ? "bad" : "good"],
            ["Avg time to resolve", hours(d.avgResolveHours), "from raised to resolved", ""],
            ["Resolved on target", percent(d.slaMetPercent), "within the priority's target", d.slaMetPercent !== null && d.slaMetPercent < 80 ? "warn" : "good"],
            ["First response", hours(d.avgFirstResponseHours), "raised to accepted, average", ""],
            ["Hours logged", d.hoursLogged, d.todosDone + " team to-dos done", ""]
        ];
        tiles.forEach(function (t) {
            var tile = el("div", "kpi" + (t[3] ? " " + t[3] : ""));
            tile.appendChild(el("div", "kpi-label", t[0]));
            tile.appendChild(el("div", "kpi-value", t[1]));
            tile.appendChild(el("div", "kpi-sub", t[2]));
            box.appendChild(tile);
        });
    }

    function rangeName(days) {
        return days === 365 ? "12 months" : days + " days";
    }

    function trend(d) {
        var legend = document.getElementById("trend-legend");
        clear(legend);
        [["Raised", "c7"], ["Resolved", "c5"]].forEach(function (item) {
            var span = el("span");
            span.appendChild(el("i", "swatch " + item[1]));
            span.appendChild(document.createTextNode(item[0]));
            legend.appendChild(span);
        });
        QAC.line(document.getElementById("trend"), {
            labels: d.labels,
            series: [{ name: "Raised", values: d.raisedSeries, cls: "a" }, { name: "Resolved", values: d.resolvedSeries, cls: "b" }]
        });
    }

    function team(d) {
        var body = document.getElementById("team");
        clear(body);
        var total = d.team.reduce(function (sum, row) { return sum + row.resolved; }, 0);
        if (d.team.length === 0) {
            var empty = el("tr");
            var cell = el("td", "muted", "No IT activity yet. Team members appear once they use the Helpdesk tab of the Git client.");
            cell.colSpan = 8;
            empty.appendChild(cell);
            body.appendChild(empty);
            return;
        }
        d.team.forEach(function (row) {
            var tr = el("tr");
            var who = el("td");
            who.appendChild(el("span", "avatar", initials(row.name)));
            who.appendChild(el("strong", null, row.name));
            tr.appendChild(who);
            tr.appendChild(el("td", "num", row.resolved));
            tr.appendChild(el("td", "num", hours(row.avgResolveHours)));
            tr.appendChild(el("td", "num", percent(row.slaMetPercent)));
            tr.appendChild(el("td", "num", row.openAssigned));
            tr.appendChild(el("td", "num", row.todosDone));
            tr.appendChild(el("td", "num", row.hoursLogged));
            var share = el("td");
            var bar = el("div", "bar");
            var fill = el("i");
            fill.style.setProperty("--w", (total > 0 ? Math.round(100 * row.resolved / total) : 0) + "%");
            bar.appendChild(fill);
            share.appendChild(bar);
            tr.appendChild(share);
            body.appendChild(tr);
        });
    }

    function recent(d) {
        var list = document.getElementById("recent");
        clear(list);
        if (d.recent.length === 0) {
            list.appendChild(el("li", "muted", "Nothing resolved in this period yet."));
            return;
        }
        d.recent.forEach(function (c) {
            var li = el("li");
            li.appendChild(el("span", "avatar", initials(c.resolvedBy)));
            var text = el("div");
            var title = el("div");
            title.appendChild(el("strong", null, c.number + " "));
            title.appendChild(document.createTextNode(c.summary));
            text.appendChild(title);
            var meta = el("div", "muted small", c.resolvedBy + " · " + (c.department || "General") + " · " + hours(c.hours) + " · " + ago(c.resolvedAt));
            text.appendChild(meta);
            li.appendChild(text);
            li.appendChild(el("span", "pill " + (c.slaMet ? "pill-resolved" : "pill-on-hold"), c.slaMet ? "On target" : "Late"));
            list.appendChild(li);
        });
    }

    function latest(d) {
        var body = document.getElementById("latest");
        clear(body);
        if (d.latest.length === 0) {
            var empty = el("tr");
            var cell = el("td", "muted", "No tickets yet.");
            cell.colSpan = 7;
            empty.appendChild(cell);
            body.appendChild(empty);
            return;
        }
        d.latest.forEach(function (t) {
            var tr = el("tr");
            tr.appendChild(el("td", null, t.number));
            tr.appendChild(el("td", null, t.summary));
            tr.appendChild(el("td", "muted", t.department || "General"));
            var prio = el("td");
            prio.appendChild(el("span", "prio prio-" + t.priority, priorityLabels[t.priority] || ("P" + t.priority)));
            tr.appendChild(prio);
            var status = el("td");
            status.appendChild(el("span", "pill pill-" + t.status, statusLabels[t.status] || t.status));
            if (t.overdue) status.appendChild(el("span", "overdue", " · late"));
            tr.appendChild(status);
            tr.appendChild(el("td", "muted", t.assignee || "—"));
            tr.appendChild(el("td", "muted", ago(t.createdAt)));
            body.appendChild(tr);
        });
    }

    function integrity(d) {
        var badge = document.getElementById("integrity");
        var audit = d.audit;
        if (!audit) {
            badge.className = "integrity";
            badge.textContent = "Audit trail check pending";
            return;
        }
        badge.className = "integrity " + (audit.intact ? "ok" : "broken");
        badge.textContent = (audit.intact ? "✓ " : "⚠ ") + audit.message;
        badge.title = "Checked " + ago(audit.checkedAt) + ". Every change is chained by SHA-256; an edited or deleted record breaks the chain.";
    }

    function render(d) {
        kpis(d);
        trend(d);
        QAC.donut(document.getElementById("priority"), d.openByPriority.map(function (s) { return { label: s.label, value: s.value, cls: s.cls }; }), "open");
        QAC.hbar(document.getElementById("departments"), d.byDepartment.map(function (s) { return { label: s.label, value: s.value, cls: s.cls }; }));
        QAC.hbar(document.getElementById("types"), d.byType.map(function (s) { return { label: s.label, value: s.value, cls: s.cls }; }));
        team(d);
        recent(d);
        latest(d);
        integrity(d);
        document.getElementById("generated").textContent = "updated " + new Date(d.generatedAt).toLocaleTimeString();
    }

    function load() {
        if (loading) { again = true; return; }
        loading = true;
        fetch(source, { credentials: "same-origin", headers: { "Accept": "application/json" } })
            .then(function (response) {
                if (response.status === 401) { location.href = "/login?returnUrl=" + encodeURIComponent(location.pathname + location.search); return null; }
                if (!response.ok) throw new Error("The dashboard could not be loaded (" + response.status + ").");
                return response.json();
            })
            .then(function (data) {
                if (!data) return;
                document.getElementById("dash-error").hidden = true;
                render(data);
            })
            .catch(function (error) {
                var box = document.getElementById("dash-error");
                box.textContent = error.message + " It will try again by itself.";
                box.hidden = false;
            })
            .finally(function () {
                loading = false;
                if (again) { again = false; setTimeout(load, 500); }
            });
    }

    document.addEventListener("DOMContentLoaded", function () {
        load();
        // Something changed in the helpdesk: fetch the figures again (a burst of changes is drawn once)
        var timer = null;
        if (window.EventSource && root.dataset.live) {
            var stream = new EventSource(root.dataset.live);
            stream.addEventListener("changed", function () {
                clearTimeout(timer);
                timer = setTimeout(load, 1200);
            });
        }
        // Times like "5 min ago" stay true, and the audit badge picks up the periodic check
        setInterval(load, 120000);
    });
})();
