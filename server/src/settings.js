// The settings table: everything the owner may change lives here and is edited on /admin, so ADMIN_KEY is
// the only thing the owner must type into Cloudflare (DESIGN-HUB 2.1, 2.4).

import { HubError } from "./http.js";
import { LIMITS, cleanLine } from "./names.js";

export const DEFAULTS = {
  uploads_open: "1", new_keys_open: "1", min_client: "2.7.0", message: "", hub_name: "nocturne but better hub",
  takedown_contact: "", max_package_bytes: "104857600", max_entries: "1000", max_unpacked_bytes: "209715200",
  storage_cap_bytes: "9000000000", storage_used: "0", d1_size_bytes: "0", d1_size_at: "0", d1_close_bytes: "400000000",
  uploads_per_key_day: "10", bytes_per_key_day: "524288000", probation_hours: "48", probation_uploads_day: "2",
  probation_bytes_day: "104857600", live_per_key: "50", uploads_global_day: "200", attempts_global_day: "1000",
  new_keys_global_day: "2000", reports_per_key_day: "20", reports_global_day: "1000", close_uploads_key_days: "0",
  strikes_to_ban: "3", picture_delay_hours: "24", max_songs_per_pack: "40", stats_salt: "", stats_salt_prev: "",
  stats_folded_until: "0", orphan_cursor: "", reports_per_address_day: "50", new_keys_per_address_day: "20", cursor_key: "",
};

const int = (min, max) => (v) => {
  const s = String(v).trim();
  if (!/^[0-9]{1,13}$/.test(s)) throw new Error(`a whole number from ${min} to ${max}`);
  const n = Number(s);
  if (n < min || n > max) throw new Error(`a whole number from ${min} to ${max}`);
  return String(n);
};
const flag = (v) => {
  const s = String(v).trim();
  if (s === "1" || s === "true" || v === true) return "1";
  if (s === "0" || s === "false" || v === false) return "0";
  throw new Error("on (1) or off (0)");
};
const GiB = 1024 * 1024 * 1024;

/** Keys the owner may change on /admin, with their checks. */
export const EDITABLE = {
  uploads_open: flag,
  new_keys_open: flag,
  min_client: (v) => {
    const s = String(v).trim();
    if (!/^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$/.test(s)) throw new Error("a version like 2.8.0");
    return s;
  },
  message: (v) => cleanLine(String(v), 300) ?? "",
  hub_name: (v) => {
    const s = cleanLine(String(v), 60);
    if (!s) throw new Error("a name");
    return s;
  },
  takedown_contact: (v) => cleanLine(String(v), 200) ?? "",
  max_package_bytes: int(1024, GiB),
  max_entries: int(1, 4000),
  max_unpacked_bytes: int(1024, 4 * GiB),
  storage_cap_bytes: int(0, 1024 * GiB),
  d1_close_bytes: int(0, 10 * GiB),
  uploads_per_key_day: int(0, 10000),
  bytes_per_key_day: int(0, 100 * GiB),
  probation_hours: int(0, 24 * 365),
  probation_uploads_day: int(0, 10000),
  probation_bytes_day: int(0, 100 * GiB),
  live_per_key: int(0, 100000),
  uploads_global_day: int(0, 1000000),
  attempts_global_day: int(0, 1000000),
  new_keys_global_day: int(0, 1000000),
  reports_per_key_day: int(0, 10000),
  reports_global_day: int(0, 1000000),
  reports_per_address_day: int(0, 1000000),
  new_keys_per_address_day: int(0, 1000000),
  close_uploads_key_days: int(0, 3650),
  strikes_to_ban: int(1, 100),
  picture_delay_hours: int(0, 24 * 365),
  max_songs_per_pack: int(1, 100),
};

/** Shown on /admin but not editable there. The salts and the cursor key are never shown. */
export const READ_ONLY_KEYS = ["storage_used", "d1_size_bytes", "stats_folded_until"];

export class Settings {
  constructor(map) {
    this.map = map;
  }
  str(k) {
    return this.map.has(k) ? this.map.get(k) : DEFAULTS[k] ?? "";
  }
  num(k) {
    const n = Number(this.str(k));
    return Number.isFinite(n) ? n : Number(DEFAULTS[k] ?? 0);
  }
  on(k) {
    return this.str(k) === "1";
  }
}

export async function loadSettings(db) {
  const { results } = await db.prepare("SELECT k, v FROM settings").all();
  return new Settings(new Map(results.map((r) => [r.k, r.v])));
}

export async function getSetting(db, k) {
  const row = await db.prepare("SELECT v FROM settings WHERE k = ?1").bind(k).first();
  return row ? row.v : DEFAULTS[k] ?? "";
}

/** Validates the owner's changes: returns [key, value] pairs, or throws 400 with the reasons. */
export function checkSettingChanges(body) {
  const changes = [];
  const problems = [];
  for (const [k, v] of Object.entries(body)) {
    const check = EDITABLE[k];
    if (!check) {
      problems.push(`${k} can't be changed here`);
      continue;
    }
    try {
      changes.push([k, check(v)]);
    } catch (e) {
      problems.push(`${k} must be ${e.message}`);
    }
  }
  if (problems.length || changes.length === 0) {
    throw new HubError(400, "bad_request", problems.length ? problems.join("; ") : "Nothing to change.", { problems });
  }
  return changes;
}

export const upsertSetting = (db, k, v) =>
  db.prepare("INSERT INTO settings (k, v) VALUES (?1, ?2) ON CONFLICT(k) DO UPDATE SET v = excluded.v").bind(k, String(v));

export const TEXT_LIMITS = LIMITS;
