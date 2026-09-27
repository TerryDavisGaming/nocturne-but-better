// The pages the Worker serves: /, /legal, /robots.txt, /admin and their script and styles (DESIGN-HUB 2.2,
// 5.3). Public page text is lower case with emphasis in capitals, like the project's docs. No inline script or
// style: the pages run under a CSP that allows only this origin's files.

import { NO_STORE, PAGE_CSP } from "./http.js";
import { ADMIN_CSS, ADMIN_HTML, ADMIN_JS, SITE_CSS } from "./static.js";

export const escapeHtml = (s) =>
  String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

function page(title, body) {
  return "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
    `<meta name="robots" content="noindex"><title>${escapeHtml(title)}</title><link rel="stylesheet" href="/site.css"></head>` +
    `<body><main>${body}</main></body></html>`;
}

const html = (text, cache, extra = {}) =>
  new Response(text, { status: 200, headers: { "Content-Type": "text/html; charset=utf-8", "Content-Security-Policy": PAGE_CSP, "Cache-Control": cache, ...extra } });

export function homePage() {
  return html(page("nocturne but better hub",
    "<h1>nocturne but better hub</h1>" +
      "<p>this is the online hub for the nocturne but better mod: custom battles and custom difficulties made by players.</p>" +
      "<p>open it from the game's title screen with GET CUSTOM BATTLES. there's nothing to browse here; the game does the browsing.</p>" +
      "<p><a href=\"/legal\">rules, takedowns and privacy</a></p>"),
  "public, max-age=3600");
}

/** /legal reads the hub name and the takedown contact from the settings the owner fills in on /admin. */
export async function legalPage(env) {
  let name = "nocturne but better hub", contact = "";
  try {
    const { results } = await env.DB.prepare("SELECT k, v FROM settings WHERE k IN ('hub_name', 'takedown_contact')").all();
    for (const r of results) {
      if (r.k === "hub_name" && r.v) name = r.v;
      if (r.k === "takedown_contact") contact = r.v;
    }
  } catch {
    // the page still works without the database
  }
  const who = contact ? `<strong>${escapeHtml(contact)}</strong>` : "the address the hub's owner will put here (NOT SET UP YET)";
  const body =
    `<h1>${escapeHtml(name)}: rules, takedowns and privacy</h1>` +
    "<h2>what this is</h2>" +
    "<p>a place to share custom battles and custom difficulties made for the nocturne but better mod. it ISN'T run by or connected to the makers of nocturne.</p>" +
    "<h2>rules</h2>" +
    "<p>upload only charts you made yourself. a battle made from an osu! beatmap is fine only if you mapped it or its mapper said yes. songs, pictures and videos usually belong to someone else, so include them only if you're allowed to share them.</p>" +
    "<p>don't upload:</p><ul>" +
    "<li>material you have no right to share</li>" +
    "<li>anything illegal</li>" +
    "<li>malware, or anything meant to harm players or their pcs</li>" +
    "<li>sexual content involving minors. it's removed, kept where the law requires it, and reported where the law requires it</li>" +
    "<li>harassment or offensive pictures</li>" +
    "<li>spam</li></ul>" +
    "<p>uploads go live straight away. the hub's owner can remove anything, and players can report entries from the game.</p>" +
    "<h2>copyright notices</h2>" +
    `<p>send notices to ${who}. a notice needs:</p><ul>` +
    "<li>the work you say is being infringed</li>" +
    "<li>the hub entry (its id or its title)</li>" +
    "<li>how to reach you</li>" +
    "<li>a statement that you believe in good faith that the use isn't allowed by you, your agent or the law</li>" +
    "<li>a statement that the notice is accurate and, under penalty of perjury, that you own the right or may act for its owner</li>" +
    "<li>your signature (a typed name is fine)</li></ul>" +
    "<p>the entry is removed promptly, and its uploader sees the reason in the game.</p>" +
    "<h2>counter-notices</h2>" +
    "<p>if your entry was removed by mistake, send a counter-notice to the same address. the entry may come back after 10 to 14 business days unless the person who sent the notice goes to court. files are kept for 30 days after a copyright removal for this reason.</p>" +
    "<h2>repeat infringers</h2>" +
    "<p>a hub key that has uploads removed for copyright 3 times can't upload any more.</p>" +
    "<h2>privacy</h2>" +
    "<p>the hub stores:</p><ul>" +
    "<li>a scrambled form (sha-256) of your game's hub key, never the key itself</li>" +
    "<li>the display name you pick</li>" +
    "<li>what you upload and the details in its listing</li>" +
    "<li>reports: the reason, the note and the scrambled key of whoever sent it, until 90 days after the report is dealt with</li>" +
    "<li>download counts: a package id and a scrambled form of the address that downloaded it. the scrambling changes every day and the old one is deleted after 2 days, so after that nobody can tell who downloaded what</li>" +
    "<li>a count of reports and new hub keys per scrambled address, so one network can't use up the whole day's limits. it's deleted every day when the scrambling changes</li></ul>" +
    "<p>NO ACCOUNTS, e-mail addresses, steam ids, windows user names or ip addresses are stored. there's no tracking and there are no cookies. cloudflare, which runs the hub's servers, sees ip addresses as the network that carries the traffic.</p>" +
    "<h2>contact</h2>" +
    `<p>${who}</p>`;
  return html(page(name + ": rules", body), "public, max-age=3600", { "Cache-Tag": "legal" });
}

export function robots() {
  return new Response("User-agent: *\nDisallow: /\n", { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "public, max-age=86400" } });
}

export function adminPage() {
  return html(ADMIN_HTML, NO_STORE);
}

export function asset(name) {
  const files = {
    "admin.js": [ADMIN_JS, "text/javascript; charset=utf-8", NO_STORE],
    "admin.css": [ADMIN_CSS, "text/css; charset=utf-8", NO_STORE],
    "site.css": [SITE_CSS, "text/css; charset=utf-8", "public, max-age=3600"],
  };
  const [text, type, cache] = files[name];
  return new Response(text, { status: 200, headers: { "Content-Type": type, "Cache-Control": cache } });
}
