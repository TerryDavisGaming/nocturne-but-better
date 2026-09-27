// D1 errors in plain words, rate limits, client IP keys, and the environment switches.

import { HubError } from "./http.js";

/** Turns a D1 failure into the hub's error, or returns null when it's something else. */
export function classifyDbError(e) {
  const m = String(e && e.message ? e.message : e);
  if (/no such table/i.test(m)) return new HubError(503, "not_set_up", "The hub isn't set up yet.");
  if (/limit/i.test(m) && /(exceed|daily|free)/i.test(m)) {
    return new HubError(503, "busy_today", "The hub is too busy today. It resets at 00:00 UTC.");
  }
  if (/(network connection lost|internal error|D1_ERROR: .*(unavailable|timeout|reset|overloaded))/i.test(m)) {
    return new HubError(503, "db_unavailable", "The hub had a problem. Try again in a while.");
  }
  return null;
}

export const isUniqueError = (e, what) => {
  const m = String(e && e.message ? e.message : e);
  return /UNIQUE constraint failed/i.test(m) && (!what || m.includes(what));
};

// ---- the environment's emergency switches (DESIGN-HUB 2.1) ------------------------------------------

export function blockedIds(env) {
  return new Set(String(env.BLOCKED_IDS || "").split(",").map((s) => s.trim().toLowerCase()).filter(Boolean));
}
export const readOnly = (env) => String(env.READ_ONLY || "").trim().toLowerCase() === "true";
export const uploadsVarClosed = (env) => String(env.UPLOADS_OPEN || "").trim().toLowerCase() === "false";
export const countsOn = (env) => Boolean(env.STATS_TOKEN && env.STATS_ACCOUNT_ID);

export function refuseReadOnly(env) {
  if (readOnly(env)) throw new HubError(503, "read_only", "The hub is read-only right now. Downloads still work.");
}

// ---- client IP keys (never stored) --------------------------------------------------------------------

export function clientIp(request) {
  return (request.headers.get("CF-Connecting-IP") || "").trim();
}

function expandV6(ip) {
  let text = ip.toLowerCase();
  const zone = text.indexOf("%");
  if (zone >= 0) text = text.slice(0, zone);
  let tail = [];
  const v4 = text.match(/(\d+)\.(\d+)\.(\d+)\.(\d+)$/);
  if (v4) {
    const n = v4.slice(1).map(Number);
    tail = [((n[0] << 8) | n[1]).toString(16), ((n[2] << 8) | n[3]).toString(16)];
    text = text.slice(0, text.length - v4[0].length) + tail.join(":");
  }
  const halves = text.split("::");
  if (halves.length > 2) return null;
  const head = halves[0] ? halves[0].split(":") : [];
  const rest = halves.length === 2 && halves[1] ? halves[1].split(":") : [];
  const fill = halves.length === 2 ? 8 - head.length - rest.length : 0;
  const groups = [...head, ...Array(Math.max(fill, 0)).fill("0"), ...rest];
  if (groups.length !== 8 || groups.some((g) => !/^[0-9a-f]{1,4}$/.test(g))) return null;
  return groups.map((g) => parseInt(g, 16));
}

/**
 * The rate-limit key for an address: IPv4 as it is; IPv6 cut to its /64 (or /56 for making keys), since
 * ISPs hand customers whole prefixes. An IPv4-mapped IPv6 address counts as the IPv4 address.
 */
export function ipKey(ip, bits = 64) {
  if (!ip) return "unknown";
  if (/^\d+\.\d+\.\d+\.\d+$/.test(ip)) return "4:" + ip;
  const g = expandV6(ip);
  if (!g) return "unknown";
  if (g.slice(0, 5).every((x) => x === 0) && g[5] === 0xffff) return `4:${g[6] >> 8}.${g[6] & 255}.${g[7] >> 8}.${g[7] & 255}`;
  if (bits === 56) return "6:" + g.slice(0, 3).map((x) => x.toString(16)).join(":") + ":" + (g[3] >> 8).toString(16) + "/56";
  return "6:" + g.slice(0, 4).map((x) => x.toString(16)).join(":") + "/64";
}

const warned = new Set();

/**
 * One rate-limit check with a Workers rate limiting binding. Counts are per Cloudflare location and
 * approximate; the exact limits are the D1 counts. A missing binding is logged once per isolate and the
 * request carries on.
 */
export async function rateLimit(env, binding, key, retryAfter = 30) {
  const rl = env[binding];
  if (!rl || typeof rl.limit !== "function") {
    if (!warned.has(binding)) {
      warned.add(binding);
      console.error(`route=ratelimit code=missing_binding binding=${binding}`);
    }
    return;
  }
  let outcome;
  try {
    outcome = await rl.limit({ key });
  } catch {
    if (!warned.has(binding + ":err")) {
      warned.add(binding + ":err");
      console.error(`route=ratelimit code=binding_failed binding=${binding}`);
    }
    return;
  }
  if (outcome && outcome.success === false) {
    throw new HubError(429, "slow_down", `Slow down a little. Try again in ${retryAfter} s.`, { retryAfter }, { "Retry-After": String(retryAfter) });
  }
}
