// Database rows to the API's Card, Detail and MyCard shapes (DESIGN-HUB 2.3).

import { tagOf } from "./ids.js";

export const CARD_COLUMNS =
  "p.seq, p.id, p.kind, p.version, p.status, p.title, p.title_key, p.artist, p.author, p.uploader_id, p.lanes, p.difficulties, " +
  "p.songs, p.battle_id, p.file_size, p.downloads, p.created_at, p.updated_at, p.length_s, p.bpm_min, p.bpm_max, p.flags, " +
  "p.source, p.format, p.requires, p.picture_state";

const parse = (text, fallback) => {
  if (text === null || text === undefined) return fallback;
  try {
    return JSON.parse(text);
  } catch {
    return fallback;
  }
};

export function card(row, counts) {
  const out = {
    id: row.id,
    kind: row.kind,
    version: row.version,
    status: row.status,
    title: row.title,
    artist: row.artist,
    author: row.author,
    uploader: { id: row.uploader_id, name: row.uploader_name ?? "", tag: tagOf(row.uploader_id) },
    lanes: row.lanes,
    difficulties: parse(row.difficulties, []),
    songs: parse(row.songs, null),
    battleId: row.battle_id ?? null,
    size: row.file_size,
    downloads: counts ? row.downloads : null,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    lengthSeconds: row.length_s ?? null,
    bpm: row.bpm_min !== null && row.bpm_min !== undefined ? [row.bpm_min, row.bpm_max] : null,
    flags: parse(row.flags, {}),
    source: parse(row.source, null),
    format: row.format,
    requires: parse(row.requires, []),
  };
  if (row.thumb) out.thumb = row.thumb;
  return out;
}

export function detail(row, counts) {
  return {
    ...card(row, counts),
    description: row.description ?? "",
    contents: parse(row.contents, {}),
    file: { size: row.file_size, sha256: row.file_sha256, fingerprint: row.fingerprint },
  };
}

/**
 * The uploader's own view: status live, hidden (under review), removed or deleted. A quarantined entry shows
 * as removed for breaking the rules; the uploader isn't told that its file is being kept.
 */
export function myCard(row, counts) {
  const out = {
    ...card(row, counts),
    description: row.description ?? "",
    removedReason: row.removed_reason ?? null,
    removedNote: row.status === "removed" ? row.removed_note ?? null : null,
    removedAt: row.removed_at ?? null,
    pictureState: row.picture_state,
  };
  if (row.status === "quarantined") {
    out.status = "removed";
    out.removedReason = "rules";
  }
  return out;
}

/** The public status of a package that isn't live: 410 codes and lookup statuses. */
export function goneStatus(status) {
  if (status === "hidden") return "unavailable";
  if (status === "deleted") return "deleted";
  return "removed"; // removed, quarantined
}
