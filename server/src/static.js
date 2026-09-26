// The owner's page and the site's styles, as strings the Worker serves. The page's script is a separate file
// (/admin.js) so the CSP can refuse every inline script; everything it shows from the database goes in with
// textContent, never as HTML. (No backticks or template literals inside these strings.)

export const SITE_CSS = String.raw`
:root { color-scheme: light dark; --bg: #f6f5f2; --fg: #1d1b20; --muted: #5d5a63; --line: #d8d4dc; --accent: #6b3fa0; }
@media (prefers-color-scheme: dark) { :root { --bg: #16141a; --fg: #ece8f1; --muted: #a9a3b3; --line: #34303b; --accent: #b995ec; } }
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif; }
main { max-width: 760px; margin: 0 auto; padding: 32px 16px 64px; }
h1 { font-size: 1.6rem; line-height: 1.25; margin: 0 0 1rem; }
h2 { font-size: 1.15rem; margin: 2rem 0 .5rem; }
a { color: var(--accent); }
li { margin: .2rem 0; }
`;

export const ADMIN_CSS = String.raw`
:root { color-scheme: light dark; --bg: #f6f5f2; --panel: #ffffff; --fg: #1d1b20; --muted: #5d5a63; --line: #d8d4dc; --accent: #6b3fa0; --bad: #b3261e; --ok: #1f7a3a; }
@media (prefers-color-scheme: dark) { :root { --bg: #16141a; --panel: #1f1c24; --fg: #ece8f1; --muted: #a9a3b3; --line: #34303b; --accent: #b995ec; --bad: #ff8a80; --ok: #7bd88f; } }
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--fg); font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
header { display: flex; gap: 12px; align-items: center; padding: 12px 16px; border-bottom: 1px solid var(--line); }
header h1 { font-size: 1.1rem; margin: 0; flex: 1; }
section, #app { padding: 12px 16px; }
nav { display: flex; flex-wrap: wrap; gap: 6px; padding: 12px 16px 0; }
button { font: inherit; padding: 5px 10px; border: 1px solid var(--line); border-radius: 6px; background: var(--panel); color: var(--fg); cursor: pointer; }
button:hover { border-color: var(--accent); }
button.on { background: var(--accent); color: var(--bg); border-color: var(--accent); }
button.danger { color: var(--bad); }
input, select, textarea { font: inherit; padding: 5px 8px; border: 1px solid var(--line); border-radius: 6px; background: var(--panel); color: var(--fg); max-width: 100%; }
textarea { width: 100%; min-height: 60px; }
table { border-collapse: collapse; width: 100%; background: var(--panel); }
th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; overflow-wrap: anywhere; }
th { color: var(--muted); font-weight: 600; }
.msg { min-height: 1.4em; color: var(--muted); }
.bad { color: var(--bad); }
.good { color: var(--ok); }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: 12px; }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 8px; }
.card img, .thumb { width: 128px; height: 128px; object-fit: cover; border-radius: 4px; display: block; background: var(--line); }
.row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin: 6px 0; }
.kv { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; }
.kv dt { color: var(--muted); }
.kv dd { margin: 0; overflow-wrap: anywhere; }
.panel { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 12px; margin: 12px 0; }
.pre { white-space: pre-wrap; }
`;

export const ADMIN_HTML = String.raw`<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex"><title>hub owner page</title><link rel="stylesheet" href="/admin.css"></head>
<body>
<header><h1>hub owner page</h1><button id="signout" hidden>close</button></header>
<section id="signin">
  <p>paste your ADMIN_KEY. it stays in this browser tab only and is gone when you close the tab. NEVER paste it anywhere else.</p>
  <form id="signin-form" class="row"><input id="key" type="password" autocomplete="off" spellcheck="false" placeholder="admin key" size="50"><button type="submit">open</button></form>
  <p id="signin-msg" class="msg"></p>
</section>
<div id="app" hidden>
  <nav id="tabs">
    <button data-tab="overview">overview</button><button data-tab="reports">reports</button><button data-tab="pictures">pictures</button>
    <button data-tab="packages">entries</button><button data-tab="floods">spam waves</button><button data-tab="settings">settings</button>
    <button data-tab="purges">purges</button><button data-tab="backup">backup</button>
  </nav>
  <p id="status" class="msg"></p>
  <div id="view"></div>
  <div id="detail"></div>
</div>
<script src="/admin.js"></script>
</body></html>
`;

export const ADMIN_JS = String.raw`(function () {
  "use strict";
  var STORE = "nbb-admin-key";
  var key = "";
  try { key = sessionStorage.getItem(STORE) || ""; } catch (e) { key = ""; }
  var REMOVE_REASONS = ["copyright", "offensive", "malicious", "spam", "rules", "other"];

  function $(id) { return document.getElementById(id); }
  function add(parent, child) {
    if (child === null || child === undefined || child === false) return;
    if (Array.isArray(child)) { child.forEach(function (c) { add(parent, c); }); return; }
    parent.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  function el(tag, attrs) {
    var e = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) {
      var v = attrs[k];
      if (v === null || v === undefined || v === false) return;
      if (k === "text") e.textContent = String(v);
      else if (k === "class") e.className = v;
      else if (k.slice(0, 2) === "on") e.addEventListener(k.slice(2), v);
      else e.setAttribute(k, v === true ? "" : String(v));
    });
    for (var i = 2; i < arguments.length; i++) add(e, arguments[i]);
    return e;
  }
  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); return node; }
  function when(t) { return t ? new Date(t * 1000).toISOString().replace("T", " ").slice(0, 16) + " utc" : "-"; }
  function mb(n) { return (Number(n || 0) / 1048576).toFixed(1) + " MB"; }
  function say(text, bad) { var s = $("status"); s.textContent = text || ""; s.className = bad ? "msg bad" : "msg"; }
  function kv(pairs) {
    var dl = el("dl", { "class": "kv" });
    pairs.forEach(function (p) { dl.appendChild(el("dt", { text: p[0] })); dl.appendChild(el("dd", null, p[1] === null || p[1] === undefined ? "-" : p[1])); });
    return dl;
  }
  function table(head, rows) {
    var t = el("table");
    t.appendChild(el("tr", null, head.map(function (h) { return el("th", { text: h }); })));
    rows.forEach(function (r) { t.appendChild(el("tr", null, r.map(function (c) { return el("td", null, c); }))); });
    return t;
  }

  function api(method, path, body) {
    var opts = { method: method, headers: { "Authorization": "Bearer " + key, "X-NBB-Client": "1" }, cache: "no-store" };
    if (method !== "GET") { opts.headers["Content-Type"] = "application/json"; opts.body = JSON.stringify(body || {}); }
    return fetch("/v1/admin/" + path, opts).then(function (res) {
      if (res.status === 401) { signOut("wrong key"); throw new Error("wrong key"); }
      var type = res.headers.get("Content-Type") || "";
      if (type.indexOf("application/json") < 0) { if (!res.ok) throw new Error("error " + res.status); return res; }
      return res.json().then(function (data) {
        if (!res.ok) throw new Error((data && data.message) || ("error " + res.status));
        return data;
      });
    });
  }
  function act(label, promise, after) {
    say(label + "...");
    return promise.then(function (r) {
      var extra = r && r.purge === "pending" ? " (the cache purge is still pending: see purges)" : "";
      say(label + ": done" + extra);
      if (after) after(r);
      return r;
    }, function (e) { say(label + ": " + e.message, true); });
  }

  // ---- signing in ----------------------------------------------------------------------------------
  function signOut(message) {
    key = "";
    try { sessionStorage.removeItem(STORE); } catch (e) { /* nothing */ }
    $("app").hidden = true; $("signin").hidden = false; $("signout").hidden = true;
    $("signin-msg").textContent = message || "";
  }
  function signIn() {
    return api("GET", "overview").then(function () {
      $("app").hidden = false; $("signin").hidden = true; $("signout").hidden = false; $("key").value = "";
      show("overview");
    }, function (e) { if (key) signOut(e.message); });
  }
  // Over plain http the key would cross the network readable: refuse to send it (loopback is the local stand-in).
  var plainHttp = location.protocol === "http:" && !/^(127\.0\.0\.1|localhost|\[::1\])$/.test(location.hostname);
  var HTTP_WARNING = "this page was opened over plain http, so the key would travel unprotected. open it with https:// instead (and turn on always use https for the domain).";
  if (plainHttp) key = "";
  $("signin-form").addEventListener("submit", function (ev) {
    ev.preventDefault();
    if (plainHttp) return;
    key = $("key").value.trim();
    try { sessionStorage.setItem(STORE, key); } catch (e) { /* this tab only */ }
    signIn();
  });
  $("signout").addEventListener("click", function () { signOut(""); });

  // ---- tabs -----------------------------------------------------------------------------------------
  var views = {};
  function show(name) {
    Array.prototype.forEach.call(document.querySelectorAll("#tabs button"), function (b) { b.className = b.getAttribute("data-tab") === name ? "on" : ""; });
    clear($("detail"));
    var view = clear($("view"));
    views[name](view);
  }
  Array.prototype.forEach.call(document.querySelectorAll("#tabs button"), function (b) {
    b.addEventListener("click", function () { show(b.getAttribute("data-tab")); });
  });

  views.overview = function (view) {
    api("GET", "overview").then(function (o) {
      var s = o.switches;
      var warn = [];
      if (o.purgesFailing) warn.push(el("p", { "class": "bad", text: o.purgesFailing + " cache purge(s) keep failing. see purges; the runbook's BLOCKED_IDS step works without them." }));
      if (o.storageUsed + o.storageReserved > o.storageCap * 0.9) warn.push(el("p", { "class": "bad", text: "storage is over 90% of the cap." }));
      if (s.varReadOnly) warn.push(el("p", { "class": "bad", text: "READ_ONLY is on (a dashboard variable)." }));
      if (s.varUploadsClosed) warn.push(el("p", { "class": "bad", text: "UPLOADS_OPEN=false is set (a dashboard variable)." }));
      add(view, warn);
      add(view, el("div", { "class": "panel" }, kv([
        ["live entries", o.packages.live || 0], ["hidden", o.packages.hidden || 0], ["removed", o.packages.removed || 0],
        ["deleted by uploaders", o.packages.deleted || 0], ["quarantined", o.packages.quarantined || 0],
        ["uploads today", o.uploadsToday], ["upload attempts today", o.attemptsToday], ["new keys today", o.newKeysToday],
        ["open reports", o.openReports], ["pictures waiting", o.picturesWaiting],
        ["purges pending / failing", o.purgesPending + " / " + o.purgesFailing],
        ["storage", mb(o.storageUsed) + " used + " + mb(o.storageReserved) + " arriving, cap " + mb(o.storageCap)],
        ["database", mb(o.d1Size) + " (uploads close at " + mb(o.d1Close) + ")"],
        ["download counts", o.counts ? "on" : "off (optional: STATS_TOKEN and STATS_ACCOUNT_ID)"],
        ["discord webhook", s.webhook ? "on" : "off"],
        ["uploads", s.uploadsOpen ? "open" : "closed"], ["new keys", s.newKeysOpen ? "open" : "closed"],
        ["uploads paused for keys younger than", s.closeUploadsForKeysYoungerThanDays + " days"],
        ["BLOCKED_IDS", s.blockedIds.length ? s.blockedIds.join(", ") : "none"],
        ["server time", when(o.time)]
      ])));
    }, function (e) { say(e.message, true); });
  };

  views.reports = function (view) {
    api("GET", "reports").then(function (r) {
      if (!r.items.length) { add(view, el("p", { text: "no open reports." })); return; }
      add(view, table(["entry", "status", "reports", "reasons", "last", ""], r.items.map(function (x) {
        return [x.title || x.packageId, x.status || "-", x.count, Object.keys(x.reasons).map(function (k) { return k + " " + x.reasons[k]; }).join(", "), when(x.last),
          el("button", { text: "open", onclick: function () { openPackage(x.packageId); } })];
      })));
    }, function (e) { say(e.message, true); });
  };

  views.pictures = function (view) {
    api("GET", "pictures").then(function (r) {
      if (!r.items.length) { add(view, el("p", { text: "no pictures waiting." })); return; }
      var grid = el("div", { "class": "grid" });
      r.items.forEach(function (p) {
        var card = el("div", { "class": "card" },
          el("img", { src: "data:image/jpeg;base64," + p.thumb, alt: "" }),
          el("div", { text: p.title }), el("div", { "class": "msg", text: "shows by itself at " + when(p.due) }));
        add(card, el("div", { "class": "row" },
          el("button", { text: "show", onclick: function () { act("show picture", api("POST", "packages/" + p.id + "/picture", { show: true }), function () { card.remove(); }); } }),
          el("button", { "class": "danger", text: "refuse", onclick: function () { act("refuse picture", api("POST", "packages/" + p.id + "/picture", { show: false }), function () { card.remove(); }); } }),
          el("button", { text: "open", onclick: function () { openPackage(p.id); } })));
        grid.appendChild(card);
      });
      add(view, grid);
    }, function (e) { say(e.message, true); });
  };

  views.packages = function (view) {
    var status = el("select", null, ["", "live", "hidden", "removed", "deleted", "quarantined"].map(function (s) { return el("option", { value: s, text: s || "any status" }); }));
    var hours = el("input", { type: "number", min: "0", placeholder: "changed in the last N hours", size: "8" });
    var age = el("input", { type: "number", min: "0", placeholder: "keys younger than N days", size: "8" });
    var q = el("input", { type: "search", placeholder: "title, entry id, uploader id or battle id", size: "34" });
    var list = el("div");
    var next = null;
    function load(more) {
      var parts = [];
      if (status.value) parts.push("status=" + encodeURIComponent(status.value));
      if (hours.value) parts.push("since=" + (Math.floor(Date.now() / 1000) - Number(hours.value) * 3600));
      if (age.value) parts.push("keyAge=" + Number(age.value));
      if (q.value.trim()) parts.push("q=" + encodeURIComponent(q.value.trim()));
      if (more && next) parts.push("cursor=" + next);
      api("GET", "packages" + (parts.length ? "?" + parts.join("&") : "")).then(function (r) {
        if (!more) clear(list);
        next = r.next;
        add(list, table(["entry", "status", "uploader", "key made", "updated", "reports", "picture", ""], r.items.map(function (p) {
          return [p.title + " (" + p.kind + ", v" + p.version + ")", p.status, p.uploader.name + " #" + p.uploader.tag + (p.uploader.status !== "ok" ? " (" + p.uploader.status + ")" : ""),
            when(p.uploader.keyCreated), when(p.updatedAt), p.reportsOpen, p.pictureState, el("button", { text: "open", onclick: function () { openPackage(p.id); } })];
        })));
        if (next) add(list, el("button", { text: "more", onclick: function (ev) { ev.target.remove(); load(true); } }));
      }, function (e) { say(e.message, true); });
    }
    add(view, el("form", { "class": "row", onsubmit: function (ev) { ev.preventDefault(); load(false); } }, status, hours, age, q, el("button", { type: "submit", text: "list" })));
    add(view, list);
    load(false);
  };

  function download(id, version) {
    say("downloading...");
    fetch("/v1/admin/files/" + id + "/" + version, { headers: { "Authorization": "Bearer " + key, "X-NBB-Client": "1" }, cache: "no-store" })
      .then(function (res) { if (!res.ok) throw new Error("error " + res.status); return res.blob(); })
      .then(function (blob) {
        var a = el("a", { href: URL.createObjectURL(blob), download: id + "-v" + version + ".zip" });
        document.body.appendChild(a); a.click(); a.remove();
        say("downloaded. open it as a zip; NEVER run anything inside.");
      }, function (e) { say(e.message, true); });
  }

  function openPackage(id) {
    var box = clear($("detail"));
    api("GET", "packages/" + id).then(function (p) {
      var panel = el("div", { "class": "panel" });
      add(panel, el("h2", { text: p.title + " (" + p.kind + ", v" + p.version + ", " + p.status + ")" }));
      if (p.thumb) add(panel, el("img", { "class": "thumb", src: "data:image/jpeg;base64," + p.thumb, alt: "" }));
      add(panel, kv([
        ["entry id", p.id], ["artist", p.artist], ["charter", p.author], ["lanes", p.lanes], ["battle id", p.battleId],
        ["uploader", el("button", { text: p.uploader.name + " #" + p.uploader.tag + " (" + p.uploader.status + ", " + p.uploader.strikes + " strikes)", onclick: function () { openUploader(p.uploader.id); } })],
        ["key made", when(p.uploader.keyCreated)], ["created", when(p.createdAt)], ["updated", when(p.updatedAt)], ["size", mb(p.size)],
        ["downloads", p.downloads], ["picture", p.pictureState], ["removed", p.removedReason ? p.removedReason + " at " + when(p.removedAt) + (p.removedNote ? ": " + p.removedNote : "") : null],
        ["inside", JSON.stringify(p.contents)], ["flags", JSON.stringify(p.flags)], ["sha-256", p.sha256]
      ]));
      if (p.description) add(panel, el("p", { "class": "pre", text: p.description }));
      var note = el("input", { placeholder: "note shown to the uploader (for remove)", size: "40" });
      var reason = el("select", null, REMOVE_REASONS.map(function (r) { return el("option", { value: r, text: r }); }));
      var strike = el("input", { type: "checkbox" });
      function reload() { openPackage(id); }
      // Any version still held can be downloaded: reports say which version they were about.
      var held = [p.version].concat((p.oldVersions || []).map(function (o) { return o.version; }));
      var version = el("input", { type: "number", min: "1", max: String(p.version), value: String(p.version), size: "4", title: "version" });
      add(panel, el("p", { "class": "msg", text: "versions still held: " + held.join(", ") + (p.status === "quarantined" ? " (and every version moved to quarantine)" : "") }));
      add(panel, el("div", { "class": "row" },
        el("button", { text: "download to review", onclick: function () { download(p.id, Number(version.value) || p.version); } }), "version", version,
        el("button", { text: "hide", onclick: function () { act("hide", api("POST", "packages/" + id + "/hide", { note: note.value }), reload); } }),
        el("button", { text: "restore", onclick: function () { act("restore", api("POST", "packages/" + id + "/restore", {}), reload); } }),
        el("button", { text: "show picture", onclick: function () { act("show picture", api("POST", "packages/" + id + "/picture", { show: true }), reload); } }),
        el("button", { text: "refuse picture", onclick: function () { act("refuse picture", api("POST", "packages/" + id + "/picture", { show: false }), reload); } })));
      add(panel, el("div", { "class": "row" }, "remove:", reason, note, el("label", null, strike, " copyright strike"),
        el("button", { "class": "danger", text: "remove", onclick: function () {
          act("remove", api("POST", "packages/" + id + "/remove", { reason: reason.value, note: note.value, strike: strike.checked }), reload);
        } })));
      add(panel, el("div", { "class": "row" },
        el("button", { "class": "danger", text: "quarantine (illegal material)", onclick: function () {
          if (window.confirm("quarantine keeps every version of the file privately and takes the entry down. continue?")) {
            act("quarantine", api("POST", "packages/" + id + "/quarantine", { note: note.value }), function (r) {
              reload();
              say("quarantine: versions kept: " + ((r.versions || []).join(", ") || "none left to move") + (r.more ? ". more are left: press quarantine again" : ""));
            });
          }
        } }),
        p.battleId ? el("button", { text: "release battle id", onclick: function () { act("release battle id", api("POST", "battle-ids/" + p.battleId + "/release", {}), reload); } }) : null,
        el("button", { text: "resolve all reports", onclick: function () { act("resolve reports", api("POST", "reports/resolve", { packageId: id, resolution: "resolved" }), reload); } })));
      if (p.reports.length) {
        add(panel, el("h3", { text: "reports" }));
        add(panel, table(["from", "version", "reason", "note", "when", "state", ""], p.reports.map(function (r) {
          return [r.reporter, "v" + r.version, r.reason, r.note, when(r.createdAt), r.resolvedAt ? r.resolution + " " + when(r.resolvedAt) : "open",
            r.resolvedAt ? "" : el("button", { text: "resolve", onclick: function () { act("resolve", api("POST", "reports/resolve", { packageId: id, reporterHash: r.reporterHash, resolution: "resolved" }), reload); } })];
        })));
      }
      if (p.audit.length) {
        add(panel, el("h3", { text: "history" }));
        add(panel, table(["when", "who", "what", "detail"], p.audit.map(function (a) { return [when(a.at), a.actor, a.action, a.detail || ""]; })));
      }
      add(box, panel);
      box.scrollIntoView();
    }, function (e) { say(e.message, true); });
  }

  function openUploader(id) {
    var box = clear($("detail"));
    api("GET", "uploaders/" + id).then(function (r) {
      var u = r.uploader;
      var panel = el("div", { "class": "panel" });
      add(panel, el("h2", { text: u.name + " #" + u.tag }));
      add(panel, kv([["uploader id", u.id], ["status", u.status], ["strikes", u.strikes], ["key made", when(u.created_at)], ["trusted since", when(u.trusted_at)]]));
      var strikes = el("input", { type: "number", min: "0", value: String(u.strikes), size: "4" });
      var to = el("input", { placeholder: "move entries to uploader id", size: "22" });
      function reload() { openUploader(id); }
      add(panel, el("div", { "class": "row" },
        el("button", { "class": "danger", text: "ban", onclick: function () { act("ban", api("POST", "uploaders/" + id + "/ban", {}), reload); } }),
        el("button", { text: "unban", onclick: function () { act("unban", api("POST", "uploaders/" + id + "/unban", {}), reload); } }),
        el("button", { "class": "danger", text: "turn the key off (revoke)", onclick: function () {
          if (window.confirm("the key stops working for good. continue?")) act("revoke", api("POST", "uploaders/" + id + "/revoke-key", {}), reload);
        } }),
        strikes, el("button", { text: "set strikes", onclick: function () { act("strikes", api("POST", "uploaders/" + id + "/strikes", { value: Number(strikes.value) }), reload); } })));
      add(panel, el("div", { "class": "row" },
        el("button", { "class": "danger", text: "remove all their entries (spam)", onclick: function () {
          if (!window.confirm("remove every live entry of this uploader?")) return;
          (function step() { act("remove all", api("POST", "uploaders/" + id + "/remove-all", { reason: "spam" }), function (x) { if (x && x.more) step(); else reload(); }); })();
        } }),
        to, el("button", { text: "move entries", onclick: function () {
          if (window.confirm("only do this on a request you could check. continue?")) act("move entries", api("POST", "uploaders/" + id + "/transfer", { to: to.value.trim() }), reload);
        } })));
      add(panel, table(["entry", "status", "updated", ""], r.packages.map(function (p) {
        return [p.title + " (v" + p.version + ")", p.status, when(p.updatedAt), el("button", { text: "open", onclick: function () { openPackage(p.id); } })];
      })));
      add(box, panel);
      box.scrollIntoView();
    }, function (e) { say(e.message, true); });
  }

  views.floods = function (view) {
    var action = el("select", null, el("option", { value: "hide", text: "hide" }), el("option", { value: "remove", text: "remove (reason spam)" }));
    var since = el("input", { type: "datetime-local" });
    var keys = el("input", { type: "datetime-local" });
    function secs(input) { return input.value ? Math.floor(new Date(input.value + "Z").getTime() / 1000) : null; }
    add(view, el("div", { "class": "panel" },
      el("p", { text: "hide or remove everything uploaded since a time (utc), or everything from keys made since a time. 30 entries a step; it keeps going until done." }),
      el("div", { "class": "row" }, action, "uploaded since", since, "or keys made since", keys,
        el("button", { "class": "danger", text: "run", onclick: function () {
          var body = { action: action.value, uploadedSince: secs(since), keysCreatedSince: secs(keys) };
          var total = 0;
          (function step() {
            api("POST", "bulk", body).then(function (r) {
              total += r.done; say("bulk " + body.action + ": " + total + " so far");
              if (r.more) step(); else say("bulk " + body.action + ": " + total + " done");
            }, function (e) { say(e.message, true); });
          })();
        } }))));
    var days = el("input", { type: "number", min: "0", value: "2", size: "4" });
    add(view, el("div", { "class": "panel" },
      el("p", { text: "pause uploads from hub keys younger than this many days (0 turns it off)." }),
      el("div", { "class": "row" }, days, el("button", { text: "set", onclick: function () {
        act("pause new keys", api("PUT", "gates", { closeUploadsForKeysYoungerThanDays: Number(days.value) }));
      } }))));
    add(view, el("div", { "class": "panel" }, el("div", { "class": "row" },
      el("button", { "class": "danger", text: "close all uploads", onclick: function () { act("close uploads", api("PUT", "settings", { uploads_open: "0" })); } }),
      el("button", { text: "open uploads", onclick: function () { act("open uploads", api("PUT", "settings", { uploads_open: "1" })); } }),
      el("button", { text: "stop new keys", onclick: function () { act("stop new keys", api("PUT", "settings", { new_keys_open: "0" })); } }),
      el("button", { text: "allow new keys", onclick: function () { act("allow new keys", api("PUT", "settings", { new_keys_open: "1" })); } }))));
  };

  views.settings = function (view) {
    api("GET", "settings").then(function (r) {
      var inputs = {};
      var form = el("form", { onsubmit: function (ev) {
        ev.preventDefault();
        var changes = {};
        r.editable.forEach(function (k) { if (inputs[k].value !== r.settings[k]) changes[k] = inputs[k].value; });
        if (!Object.keys(changes).length) { say("nothing changed"); return; }
        act("save settings", api("PUT", "settings", changes), function () { show("settings"); });
      } });
      var rows = r.editable.map(function (k) {
        inputs[k] = el("input", { value: r.settings[k], size: k === "takedown_contact" || k === "message" ? "50" : "16" });
        return [k, inputs[k], "default " + (r.defaults[k] === "" ? "(empty)" : r.defaults[k])];
      });
      add(form, el("p", { text: "takedown_contact is shown on /legal and in the game. use an address you're fine being public." }));
      add(form, table(["setting", "value", ""], rows));
      add(form, el("div", { "class": "row" }, el("button", { type: "submit", text: "save" })));
      add(view, form);
      add(view, el("div", { "class": "panel" }, kv(Object.keys(r.settings).filter(function (k) { return r.editable.indexOf(k) < 0; }).map(function (k) { return [k, r.settings[k]]; }))));
    }, function (e) { say(e.message, true); });
  };

  views.purges = function (view) {
    add(view, el("div", { "class": "row" }, el("button", { text: "retry pending purges now", onclick: function () {
      act("retry purges", api("POST", "purges/retry", {}), function () { show("purges"); });
    } })));
    api("GET", "purges").then(function (r) {
      add(view, table(["id", "tags", "queued", "tries", "done", "last error"], r.items.map(function (p) {
        return [p.id, p.tags.join(" "), when(p.created_at), p.tries, p.done_at ? when(p.done_at) : "pending", p.last_error || ""];
      })));
    }, function (e) { say(e.message, true); });
  };

  views.backup = function (view) {
    var out = el("p", { "class": "msg" });
    add(view, el("div", { "class": "panel" },
      el("p", { text: "writes a copy of the database tables as json into the R2 bucket under backup/<date>/, a little at a time. the download-count salts are left out on purpose. do it once a month; cloudflare keeps 7 days of database history (time travel) on top." }),
      el("button", { text: "back up now", onclick: function () {
        var files = 0;
        (function step(cursor) {
          api("POST", "backup", { cursor: cursor }).then(function (r) {
            files += r.written.length; out.textContent = files + " files written";
            if (!r.done) step(r.cursor); else out.textContent = "done: " + files + " files written";
          }, function (e) { out.textContent = e.message; });
        })(null);
      } }), out));
    var out2 = el("p", { "class": "msg" });
    add(view, el("div", { "class": "panel" },
      el("p", { text: "rebuild the search index (only needed after restoring the database from a backup)." }),
      el("button", { text: "rebuild search", onclick: function () {
        var n = 0;
        (function step(after) {
          api("POST", "reindex", { after: after }).then(function (r) {
            n += r.indexed; out2.textContent = n + " entries indexed";
            if (!r.done) step(r.after); else out2.textContent = "done: " + n + " entries indexed";
          }, function (e) { out2.textContent = e.message; });
        })(0);
      } }), out2));
  };

  if (plainHttp) signOut(HTTP_WARNING); else if (key) signIn(); else signOut("");
})();
`;
