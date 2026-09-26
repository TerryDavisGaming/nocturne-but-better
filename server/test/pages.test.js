// The pages: /, /legal, /robots.txt, /admin and its files: security headers, no inline script, and the
// public text in the project's docs style (lower case, emphasis in capitals, no em dashes).

import { assert, assertEquals, assertMatch } from "./assert.js";
import { makeHub } from "./helpers.js";
import { ADMIN_HTML, ADMIN_JS } from "../src/static.js";

const CSP = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

function visibleText(html) {
  return html.replace(/<(script|style)[\s\S]*?<\/\1>/g, " ").replace(/<[^>]+>/g, " ").replace(/&[a-z#0-9]+;/g, " ");
}

function assertDocsStyle(text, where) {
  assert(!/\u2014/.test(text), `${where} has an em dash`);
  for (const word of text.split(/[^A-Za-z']+/).filter((w) => /[A-Za-z]/.test(w))) {
    const lower = word === word.toLowerCase(), caps = word === word.toUpperCase() && word.length > 1;
    assert(lower || caps || word === "I", `${where}: "${word}" is neither lower case nor ALL CAPS`);
  }
}

Deno.test("pages: the home page, /legal and robots.txt", async () => {
  const hub = await makeHub();
  const home = await hub.call("GET", "/");
  assertEquals(home.status, 200);
  assertEquals(home.headers.get("Content-Security-Policy"), CSP);
  assertDocsStyle(visibleText(home.body), "/");
  const legal = await hub.call("GET", "/legal");
  assertEquals(legal.headers.get("Content-Security-Policy"), CSP);
  assertEquals(legal.headers.get("Cache-Control"), "public, max-age=3600");
  assertEquals(legal.headers.get("Cache-Tag"), "legal");
  assertMatch(legal.body, /NOT SET UP YET/);
  assertMatch(legal.body, /counter-notice/);
  assertMatch(legal.body, /3 times/);
  assertDocsStyle(visibleText(legal.body).replace(/osu!/g, "osu"), "/legal");
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { takedown_contact: "dmca@example.org", hub_name: "my hub" } });
  const set = await hub.call("GET", "/legal");
  assertMatch(set.body, /<strong>dmca@example\.org<\/strong>/);
  assertMatch(set.body, /<h1>my hub: rules/);
  const robots = await hub.call("GET", "/robots.txt");
  assertEquals(robots.body, "User-agent: *\nDisallow: /\n");
  hub.d1.down = true;
  assertEquals((await hub.call("GET", "/legal")).status, 200);
});

Deno.test("pages: /admin has no inline script or style, and its script puts server text in with textContent only", async () => {
  const hub = await makeHub();
  const page = await hub.call("GET", "/admin");
  assertEquals(page.headers.get("Content-Security-Policy"), CSP);
  assertEquals(page.headers.get("Cache-Control"), "no-store");
  assert(!/<script>[^<]/.test(ADMIN_HTML) && /<script src="\/admin\.js"><\/script>/.test(ADMIN_HTML));
  assert(!/ style=|<style/.test(ADMIN_HTML));
  assert(!/innerHTML|outerHTML|insertAdjacentHTML|document\.write|eval\(|new Function/.test(ADMIN_JS));
  assert(ADMIN_JS.includes("sessionStorage") && !ADMIN_JS.includes("localStorage"));
  assert(!/location\.(search|hash)/.test(ADMIN_JS), "the key never goes in the address");
  const js = await hub.call("GET", "/admin.js");
  assertEquals(js.headers.get("Content-Type"), "text/javascript; charset=utf-8");
  new Function(js.body); // it parses
  assertEquals((await hub.call("GET", "/admin.css")).status, 200);
  assertEquals((await hub.call("GET", "/site.css")).status, 200);
  assertDocsStyle(visibleText(ADMIN_HTML).replace("ADMIN_KEY", "ADMINKEY"), "/admin");
});
