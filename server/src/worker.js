// nocturne but better hub: one Worker, plain JavaScript, no runtime dependencies (DESIGN-HUB 2.1).
// fetch() routes the versioned /v1/ API and the few pages; scheduled() runs the cron jobs. One top-level
// try/catch turns failures into JSON and logs only the route and an error code: never a request, header,
// body, key or address.

import { CACHE_INFO, HubError, errorResponse, finish, json } from "./http.js";
import { PKG_ID, UPLOAD_ID, VERSION, now } from "./ids.js";
import { classifyDbError, clientIp, countsOn, ipKey, rateLimit, readOnly, uploadsVarClosed } from "./guard.js";
import { loadSettings } from "./settings.js";
import { LIMITS } from "./names.js";
import { MAX_THUMB_B64 } from "./media.js";
import { PART_SIZE, abortUpload, completeUpload, putPart, startUpload } from "./uploads.js";
import { deleteOwnPackage, listPackages, lookupPackages, packageDetail, packageFile, packageInstalled } from "./packages.js";
import { getMe, myPackages, putMe, rotateKey } from "./me.js";
import { reportPackage } from "./reports.js";
import { adminRoute } from "./admin.js";
import { requireAdmin } from "./auth.js";
import { runCron } from "./cron.js";
import { adminPage, asset, homePage, legalPage, robots } from "./pages.js";

export const API_VERSION = 1;
const WRITES = new Set(["POST", "PUT", "PATCH", "DELETE"]);
const PAGES = new Set(["home", "legal", "robots", "admin-page", "asset"]); // these work without the database

const LOOPBACK = /^(127\.0\.0\.1|localhost|\[::1\])$/;

/**
 * The /legal address for /v1/info. Workers Cache keys a cached answer on its path and query only, not the
 * scheme or host, so the address is always https (a plain-http request mustn't put an http link into the
 * answer every player gets); only the local stand-in on a loopback address keeps http.
 */
export function legalUrl(requestUrl) {
  const url = new URL(requestUrl);
  const scheme = LOOPBACK.test(url.hostname) ? url.protocol : "https:";
  return `${scheme}//${url.host}/legal`;
}

async function info(env, request) {
  const s = await loadSettings(env.DB);
  return json({
    api: API_VERSION,
    hub: s.str("hub_name"),
    minClient: s.str("min_client"),
    uploadsOpen: s.on("uploads_open") && !uploadsVarClosed(env) && !readOnly(env),
    counts: countsOn(env),
    maxPackageBytes: s.num("max_package_bytes"),
    maxUnpackedBytes: s.num("max_unpacked_bytes"),
    partSize: PART_SIZE,
    maxEntries: s.num("max_entries"),
    maxSongsPerPack: s.num("max_songs_per_pack"),
    maxThumbB64: MAX_THUMB_B64,
    text: { title: LIMITS.title, artist: LIMITS.artist, author: LIMITS.author, packTitle: LIMITS.packTitle, description: LIMITS.description, name: LIMITS.name, note: LIMITS.note },
    media: { audio: ["wav-pcm", "ogg-vorbis", "mp3"], pictures: ["png", "jpeg", "gif"], video: ["webm-vp8"] },
    message: s.str("message"),
    takedownContact: s.str("takedown_contact"),
    legalUrl: legalUrl(request.url),
    time: now(),
  }, 200, CACHE_INFO, { "Cache-Tag": "info" });
}

const notFound = () => new HubError(404, "not_found", "There's nothing here.");
const methodNotAllowed = (allow) => new HubError(405, "method_not_allowed", "That method isn't allowed here.", {}, { Allow: allow });

/** Matches a request to its handler. Returns [routeName, handler]. */
function route(request, env, ctx) {
  const url = new URL(request.url);
  const method = request.method;
  const path = url.pathname;
  const parts = path.split("/").slice(1);
  const is = (m, allow) => {
    if (method === m) return true;
    throw methodNotAllowed(allow || m);
  };

  if (method === "OPTIONS") throw methodNotAllowed("GET, POST, PUT, DELETE");
  if (!path.startsWith("/v1/")) {
    if (path === "/" && is("GET")) return ["home", () => homePage()];
    if (path === "/legal" && is("GET")) return ["legal", () => legalPage(env)];
    if (path === "/robots.txt" && is("GET")) return ["robots", () => robots()];
    if (path === "/admin" && is("GET")) return ["admin-page", () => adminPage()];
    if ((path === "/admin.js" || path === "/admin.css" || path === "/site.css") && is("GET")) return ["asset", () => asset(path.slice(1))];
    if (/^\/v[0-9]+(\/|$)/.test(path)) throw new HubError(404, "not_found", "This hub speaks API v1.");
    throw notFound();
  }

  const [, a, b, c, d] = parts; // "v1", then the route's parts
  const rest = parts.slice(2);
  if (a === "info" && rest.length === 0 && is("GET")) return ["info", () => info(env, request)];
  if (a === "packages") {
    if (rest.length === 0 && is("GET")) return ["list", () => listPackages(env, request)];
    if (b === "lookup" && rest.length === 1 && is("GET")) return ["lookup", () => lookupPackages(env, request)];
    if (b && PKG_ID.test(b)) {
      if (rest.length === 1) {
        if (method === "GET") return ["detail", () => packageDetail(env, request, b)];
        if (is("DELETE", "GET, DELETE")) return ["delete", () => deleteOwnPackage(env, request, ctx, b)];
      }
      if (c === "installed" && rest.length === 2 && is("POST")) return ["installed", () => packageInstalled(env, request, b)];
      if (c === "report" && rest.length === 2 && is("POST")) return ["report", () => reportPackage(env, request, ctx, b)];
    }
    throw notFound();
  }
  if (a === "files" && rest.length === 3 && d === "package" && b && PKG_ID.test(b) && VERSION.test(c || "") && is("GET")) {
    return ["file", () => packageFile(env, request, b, Number(c))];
  }
  if (a === "me") {
    if (rest.length === 0) {
      if (method === "GET") return ["me", () => getMe(env, request)];
      if (is("PUT", "GET, PUT")) return ["me-put", () => putMe(env, request)];
    }
    if (b === "rotate" && rest.length === 1 && is("POST")) return ["rotate", () => rotateKey(env, request)];
    if (b === "packages" && rest.length === 1 && is("GET")) return ["my-packages", () => myPackages(env, request)];
    throw notFound();
  }
  if (a === "uploads") {
    if (rest.length === 0 && is("POST")) return ["upload-start", () => startUpload(env, request)];
    if (b && UPLOAD_ID.test(b)) {
      if (rest.length === 1 && is("DELETE")) return ["upload-abort", () => abortUpload(env, request, b)];
      if (c === "complete" && rest.length === 2 && is("POST")) return ["upload-complete", () => completeUpload(env, request, ctx, b)];
      if (c === "parts" && rest.length === 3 && /^[1-9][0-9]{0,4}$/.test(d || "") && is("PUT")) return ["upload-part", () => putPart(env, request, b, Number(d))];
    }
    throw notFound();
  }
  if (a === "admin" && rest.length >= 1) {
    return ["admin", async () => {
      await requireAdmin(env, request);
      return adminRoute(env, request, ctx, rest);
    }];
  }
  throw notFound();
}

function codeOf(err) {
  if (err && typeof err.code === "string" && /^[a-z0-9_]{1,40}$/.test(err.code)) return err.code;
  return "exception";
}

export default {
  async fetch(request, env, ctx) {
    let name = "unknown";
    try {
      if (WRITES.has(request.method) && request.headers.get("X-NBB-Client") !== "1") {
        throw new HubError(400, "bad_client", "Writes need the X-NBB-Client header.");
      }
      const [routeName, handler] = route(request, env, ctx);
      name = routeName;
      if (!PAGES.has(name) && (!env.DB || !env.FILES)) throw new HubError(503, "not_set_up", "The hub isn't set up yet.");
      // Every request that reaches the Worker (cache hits never do) counts against the per-address limit.
      await rateLimit(env, "RL_IP", ipKey(clientIp(request)));
      return finish(await handler());
    } catch (err) {
      if (err instanceof HubError) return finish(errorResponse(err));
      const db = classifyDbError(err);
      if (db) {
        console.error(`route=${name} code=${db.code}`);
        return finish(errorResponse(db));
      }
      console.error(`route=${name} code=${codeOf(err)}`);
      return finish(errorResponse(new HubError(500, "server_error", "The hub had a problem. Try again in a while.")));
    }
  },

  async scheduled(event, env, ctx) {
    try {
      if (!env.DB || !env.FILES) return;
      await runCron(event, env, ctx);
    } catch (err) {
      const db = classifyDbError(err);
      console.error(`route=cron code=${db ? db.code : codeOf(err)}`);
    }
  },
};
