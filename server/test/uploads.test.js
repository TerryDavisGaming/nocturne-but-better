// The upload flow: start checks, parts, complete (retries, resumes, refusals), expiry, and every quota.

import { assert, assertEquals, assertMatch } from "./assert.js";
import { FAKE_ADMIN, ORIGIN, advance, fakeKey, makeHub, publish, register, startBody, upload, zipFingerprint } from "./helpers.js";
import worker from "../src/worker.js";
import { goodBattle, goodPack, media, thumbB64 } from "./make-fixtures.js";
import { sha256Hex } from "../src/ids.js";

const HOURLY = "7 * * * *";

async function ready(o = {}) {
  const hub = await makeHub(o);
  const key = fakeKey(o.n ?? 1);
  assertEquals((await register(hub, key)).status, 200);
  return { hub, key };
}

async function startOnly(hub, key, bytes, o = {}) {
  const body = startBody(bytes, { sha256: await sha256Hex(bytes), entriesSha256: await zipFingerprint(bytes), ...o });
  return hub.call("POST", "/v1/uploads", { key, json: body });
}

/** A battle over 8 MiB, so it goes up in two parts through R2 multipart. */
const bigBattle = () => goodBattle({ song: (() => {
  const b = new Uint8Array(9 * 1024 * 1024);
  b.set(media.ogg("vorbis"));
  return b;
})() });

Deno.test("upload: a battle goes live at once and its listing comes from the file", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle({ title: "Moonlit Duel" });
  // Anything the uploader declares about the title, id or lanes is ignored: the listing comes from battle.json.
  const r = await upload(hub, key, bytes, {
    meta: { title: "A declared title", battleId: "not-this", lanes: 5, description: "Boss fight\n\nwith a video", difficulties: [{ name: "Hard", level: 8, notes: 812 }], bpm: [128, 172] },
  });
  assertEquals(r.start.status, 201);
  assertEquals(r.start.body.parts, 1);
  assertEquals(r.start.body.partSize, 8388608);
  assertEquals(r.complete.status, 200);
  assertEquals(r.complete.body.status, "live");
  assertEquals(r.complete.body.version, 1);
  const d = await hub.call("GET", "/v1/packages/" + r.complete.body.packageId);
  assertEquals(d.body.title, "Moonlit Duel");
  assertEquals(d.body.battleId, "3f2b8c1e-7d6a-4b5c-9e8f-0a1b2c3d4e5f");
  assertEquals(d.body.description, "Boss fight\n\nwith a video");
  assertEquals(d.body.difficulties, [{ name: "Hard", level: 8, notes: 812 }]);
  assertEquals(d.body.file.sha256, await sha256Hex(bytes));
  assertEquals(d.body.file.fingerprint, await zipFingerprint(bytes));
  assertEquals(d.body.bpm, [128, 172]);
  assertEquals(d.body.lanes, 4);
  assertEquals(d.body.uploader.name, "Bryce");
  assertEquals(hub.r2.objects.size, 1);
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, String(bytes.length));
});

Deno.test("upload: a pack goes live with its songs and their difficulties", async () => {
  const { hub, key } = await ready();
  const bytes = await goodPack({ songs: ["Firefly - 1", "Firefly - 2"] });
  const r = await upload(hub, key, bytes, { kind: "charts", songs: ["Firefly - 2", "Firefly - 1"] });
  assertEquals(r.complete.status, 200, JSON.stringify(r.complete.body));
  const d = await hub.call("GET", "/v1/packages/" + r.complete.body.packageId);
  assertEquals(d.body.kind, "charts");
  assertEquals(d.body.songs.map((s) => s.song), ["Firefly - 1", "Firefly - 2"]);
  assertEquals(d.body.difficulties.length, 2);
  const bad = await upload(hub, key, await goodPack({ songs: ["Firefly - 3"], title: "Other" }), { kind: "charts", songs: ["Firefly - 1"] });
  assertEquals(bad.complete.status, 422);
  assertMatch(bad.complete.body.problems[0], /doesn't match the pack's songs/);
});

Deno.test("upload: two parts through R2 multipart", async () => {
  const { hub, key } = await ready();
  const bytes = await bigBattle();
  const r = await upload(hub, key, bytes);
  assertEquals(r.start.body.parts, 2);
  assertEquals(r.parts.map((p) => p.status), [200, 200]);
  assertEquals(r.complete.status, 200, JSON.stringify(r.complete.body));
  assertEquals(hub.r2.multipart.size, 0);
  const file = await hub.call("GET", `/v1/files/${r.complete.body.packageId}/1/package`);
  assertEquals(await sha256Hex(file.body), await sha256Hex(bytes));
});

Deno.test("upload start: every field is checked", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle();
  let r = await hub.call("POST", "/v1/uploads", { key, json: { kind: "song" } });
  assertEquals(r.status, 400);
  assert(r.body.problems.length >= 5, JSON.stringify(r.body));
  r = await startOnly(hub, key, bytes, { rightsConfirmed: false });
  assertMatch(r.body.problems.join(), /rightsConfirmed/);
  r = await startOnly(hub, key, bytes, { thumb: thumbB64({ progressive: true }) });
  assertMatch(r.body.problems.join(), /baseline/);
  r = await startOnly(hub, key, bytes, { meta: { difficulties: [{ name: "", level: 200, notes: -1 }] } });
  assertEquals(r.status, 400);
  r = await startOnly(hub, key, bytes, { meta: { difficulties: [{ name: "Hard", level: 8, notes: 1 }], requires: ["Bad Feature!"] } });
  assertMatch(r.body.problems.join(), /requires/);
  r = await startOnly(hub, key, bytes, { client: "2.6.9" });
  assertEquals(r.status, 403);
  assertEquals(r.body.error, "client_too_old");
  const big = await hub.call("POST", "/v1/uploads", { key, json: { ...startBody(bytes, { sha256: "0".repeat(64), entriesSha256: "0".repeat(64) }), file: { size: 104857601, sha256: "0".repeat(64), entriesSha256: "0".repeat(64) } } });
  assertEquals(big.status, 413);
  assertEquals(big.body.error, "too_big");
  const huge = await hub.call("POST", "/v1/uploads", { key, raw: JSON.stringify({ pad: "x".repeat(40000) }), headers: { "X-NBB-Client": "1", "Content-Type": "application/json" } });
  assertEquals(huge.status, 413);
});

Deno.test("upload start: unknown, malformed, banned and revoked keys", async () => {
  const hub = await makeHub();
  const bytes = await goodBattle();
  assertEquals((await startOnly(hub, fakeKey(9), bytes)).body.error, "unknown_key");
  assertEquals((await hub.call("POST", "/v1/uploads", { key: "nbbk1_short", json: {} })).body.error, "bad_key");
  assertEquals((await hub.call("POST", "/v1/uploads", { json: {} })).body.error, "no_key");
  await register(hub, fakeKey(1));
  const uid = hub.d1.one("SELECT id FROM uploaders").id;
  await hub.call("POST", `/v1/admin/uploaders/${uid}/ban`, { admin: true, json: {} });
  assertEquals((await startOnly(hub, fakeKey(1), bytes)).body.error, "banned");
  await hub.call("POST", `/v1/admin/uploaders/${uid}/revoke-key`, { admin: true, json: {} });
  assertEquals((await startOnly(hub, fakeKey(1), bytes)).body.error, "revoked");
});

Deno.test("upload start: a retried start with the same clientUploadId gets the same upload", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle();
  const a = await startOnly(hub, key, bytes, { clientUploadId: "retry-0000-0001" });
  const b = await startOnly(hub, key, bytes, { clientUploadId: "retry-0000-0001" });
  assertEquals(a.status, 201);
  assertEquals(b.status, 200);
  assertEquals(b.body.uploadId, a.body.uploadId);
  const c = await startOnly(hub, key, bytes, { clientUploadId: "retry-0000-0002" });
  assertEquals(c.status, 409);
  assertEquals(c.body.error, "upload_in_progress");
});

Deno.test("upload: an upload with no part for 15 minutes expires on the spot at the next start", async () => {
  const { hub, key } = await ready();
  const bytes = await bigBattle();
  const a = await startOnly(hub, key, bytes);
  assertEquals(hub.r2.multipart.size, 1);
  advance(16 * 60);
  const b = await startOnly(hub, key, await goodBattle());
  assertEquals(b.status, 201, JSON.stringify(b.body));
  assertEquals(hub.r2.multipart.size, 0);
  assertEquals(hub.d1.one("SELECT state FROM uploads WHERE id = ?", a.body.uploadId).state, "expired");
  const late = await hub.call("PUT", `/v1/uploads/${a.body.uploadId}/parts/1`, { key, bytes: bytes.subarray(0, 8388608) });
  assertEquals(late.status, 409);
});

Deno.test("upload parts: exact lengths, numbers, content type and owner", async () => {
  const { hub, key } = await ready();
  const bytes = await bigBattle();
  const { body: up } = await startOnly(hub, key, bytes);
  const url = `/v1/uploads/${up.uploadId}/parts/`;
  assertEquals((await hub.call("PUT", url + "1", { key, bytes: bytes.subarray(0, 100) })).body.error, "bad_part");
  assertEquals((await hub.call("PUT", url + "1", { key, bytes: bytes.subarray(0, 8388609) })).status, 413);
  assertEquals((await hub.call("PUT", url + "3", { key, bytes: bytes.subarray(0, 10) })).body.error, "bad_part");
  assertEquals((await hub.call("PUT", url + "1", { key, bytes: bytes.subarray(0, 8388608), noLength: true })).status, 411);
  assertEquals((await hub.call("PUT", url + "1", { key, bytes: bytes.subarray(0, 8388608), headers: { "Content-Type": "text/plain" } })).body.error, "bad_client");
  await register(hub, fakeKey(2), "Other");
  assertEquals((await hub.call("PUT", url + "1", { key: fakeKey(2), bytes: bytes.subarray(0, 8388608) })).status, 404);
  assertEquals((await hub.call("PUT", url + "2", { key, bytes: bytes.subarray(8388608) })).status, 200);
  // A part sent again replaces the first try and isn't counted twice.
  assertEquals((await hub.call("PUT", url + "2", { key, bytes: bytes.subarray(8388608) })).status, 200);
  assertEquals(hub.d1.one("SELECT received_bytes FROM uploads WHERE id = ?", up.uploadId).received_bytes, bytes.length - 8388608);
});

Deno.test("upload: a single-part upload whose bytes don't match the SHA-256 is refused by R2", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle();
  const r = await upload(hub, key, bytes, { sha256: "a".repeat(64), noComplete: true });
  assertEquals(r.parts[0].status, 400);
  assertMatch(r.parts[0].body.message, /SHA-256/);
});

Deno.test("complete: missing parts, then a repeat returns the stored answer", async () => {
  const { hub, key } = await ready();
  const bytes = await bigBattle();
  const r = await upload(hub, key, bytes, { skipParts: [2] });
  assertEquals(r.complete.status, 409);
  assertEquals(r.complete.body.error, "missing_parts");
  assertEquals(r.complete.body.missing, [2]);
  const id = r.start.body.uploadId;
  await hub.call("PUT", `/v1/uploads/${id}/parts/2`, { key, bytes: bytes.subarray(8388608) });
  const done = await hub.call("POST", `/v1/uploads/${id}/complete`, { key, json: {} });
  assertEquals(done.status, 200);
  const again = await hub.call("POST", `/v1/uploads/${id}/complete`, { key, json: {} });
  assertEquals(again.status, 200);
  assertEquals(again.body, done.body);
});

Deno.test("complete: stopped after R2 joined the parts, then picked up again after 60 s", async () => {
  const { hub, key } = await ready();
  const bytes = await bigBattle();
  const r = await upload(hub, key, bytes, { noComplete: true });
  const id = r.start.body.uploadId;
  hub.d1.failNextBatch = new Error("D1_ERROR: Network connection lost.");
  const first = await hub.call("POST", `/v1/uploads/${id}/complete`, { key, json: {} });
  assertEquals(first.status, 503);
  assertEquals(first.body.error, "db_unavailable");
  assertEquals(hub.d1.one("SELECT state FROM uploads WHERE id = ?", id).state, "completing");
  assertEquals(hub.r2.multipart.size, 0); // R2 already joined the parts
  const soon = await hub.call("POST", `/v1/uploads/${id}/complete`, { key, json: {} });
  assertEquals(soon.status, 409);
  assertEquals(soon.body.error, "busy");
  advance(61);
  const later = await hub.call("POST", `/v1/uploads/${id}/complete`, { key, json: {} });
  assertEquals(later.status, 200, JSON.stringify(later.body));
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM packages").n, 1);
});

Deno.test("abandon: start, one part, walk away, run the cron: R2 is empty and the bytes are released", async () => {
  const { hub, key } = await ready();
  const bytes = await bigBattle();
  const r = await upload(hub, key, bytes, { skipParts: [2], noComplete: true });
  assertEquals(r.parts[0].status, 200);
  assertEquals(hub.d1.one("SELECT coalesce(sum(received_bytes), 0) AS n FROM uploads WHERE state = 'open'").n, 8388608);
  advance(61 * 60);
  await hub.cron(HOURLY);
  assertEquals(hub.r2.multipart.size, 0);
  assertEquals(hub.r2.objects.size, 0);
  assertEquals(hub.d1.one("SELECT coalesce(sum(received_bytes), 0) AS n FROM uploads WHERE state = 'open'").n, 0);
  assertEquals(hub.d1.one("SELECT state FROM uploads").state, "expired");
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM upload_parts").n, 0);
});

Deno.test("abort: DELETE stops the upload and frees its object", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle();
  const r = await upload(hub, key, bytes, { noComplete: true });
  assertEquals(hub.r2.objects.size, 1);
  const del = await hub.call("DELETE", `/v1/uploads/${r.start.body.uploadId}`, { key });
  assertEquals(del.status, 204);
  assertEquals(hub.r2.objects.size, 0);
  assertEquals(hub.d1.one("SELECT state FROM uploads").state, "aborted");
});

Deno.test("complete: a refused package is deleted from R2 and its problems are kept for a repeat", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle({ files: { "audio/song.ogg": { data: media.flac() } } });
  const r = await upload(hub, key, bytes);
  assertEquals(r.complete.status, 422);
  assertEquals(r.complete.body.error, "package_refused");
  assertMatch(r.complete.body.problems[0], /FLAC/);
  assertEquals(hub.r2.objects.size, 0);
  const again = await hub.call("POST", `/v1/uploads/${r.start.body.uploadId}/complete`, { key, json: {} });
  assertEquals(again.status, 422);
  assertEquals(hub.d1.one("SELECT meta FROM uploads").meta, null);
});

Deno.test("complete: the declared file list must match the real one", async () => {
  const { hub, key } = await ready();
  const r = await upload(hub, key, await goodBattle(), { entriesSha256: "b".repeat(64) });
  assertEquals(r.complete.status, 422);
  assertMatch(r.complete.body.problems[0], /entriesSha256/);
});

Deno.test("battle ids: another player's live id is refused, your own asks for a new version, a changed id on update is refused", async () => {
  const { hub, key } = await ready();
  const first = await publish(hub, key, await goodBattle());
  const mine = await upload(hub, key, await goodBattle({ title: "Again", files: { "notes.json": { data: "{}" } } }));
  assertEquals(mine.complete.status, 409);
  assertEquals(mine.complete.body.error, "battle_yours");
  assertEquals(mine.complete.body.packageId, first);
  await register(hub, fakeKey(2), "Squatter");
  const other = await upload(hub, fakeKey(2), await goodBattle({ files: { "extra.json": { data: "{}" } } }));
  assertEquals(other.complete.body.error, "battle_taken");
  assertEquals(other.complete.body.packageId, first);
  advance(3 * 86400);
  const changed = await upload(hub, key, await goodBattle({ id: "11111111-2222-3333-4444-555555555555" }), { packageId: first });
  assertEquals(changed.complete.body.error, "battle_changed");
});

Deno.test("battle ids: removed for copyright blocks only the same uploader, and the owner can release it", async () => {
  const { hub, key } = await ready();
  const id = await publish(hub, key, await goodBattle());
  await hub.call("POST", `/v1/admin/packages/${id}/remove`, { admin: true, json: { reason: "copyright", note: "notice 1", strike: true } });
  advance(3 * 86400);
  const again = await upload(hub, key, await goodBattle());
  assertEquals(again.complete.body.error, "removed_before");
  await register(hub, fakeKey(2), "Real Creator");
  const creator = await upload(hub, fakeKey(2), await goodBattle());
  assertEquals(creator.complete.status, 200, JSON.stringify(creator.complete.body));
  await hub.call("POST", `/v1/admin/packages/${creator.complete.body.packageId}/remove`, { admin: true, json: { reason: "other" } });
  await hub.call("POST", "/v1/admin/battle-ids/3f2b8c1e-7d6a-4b5c-9e8f-0a1b2c3d4e5f/release", { admin: true, json: {} });
  const released = await upload(hub, key, await goodBattle({ files: { "x.json": { data: "{}" } } }));
  assertEquals(released.complete.status, 200, JSON.stringify(released.complete.body));
});

Deno.test("duplicates: the same files already live are refused by fingerprint, not by a declared hash", async () => {
  const { hub, key } = await ready();
  const bytes = await goodPack();
  const id = await publish(hub, key, bytes, { kind: "charts" });
  await register(hub, fakeKey(2), "Copier");
  // The start already says so, before any part is sent.
  const dup = await upload(hub, fakeKey(2), bytes, { kind: "charts" });
  assertEquals(dup.start.status, 409);
  assertEquals(dup.start.body.error, "duplicate");
  assertEquals(dup.start.body.packageId, id);
  // Declaring another fingerprint doesn't get past it: complete computes it from the file.
  const lying = await upload(hub, fakeKey(2), bytes, { kind: "charts", entriesSha256: "0".repeat(64) });
  assertEquals(lying.start.status, 201);
  assertEquals(lying.complete.status, 422);
  assertMatch(lying.complete.body.problems[0], /file list doesn't match/);
});

Deno.test("new versions: same id, version 2, old file kept 24 h in the trash, created date and downloads kept", async () => {
  const { hub, key } = await ready();
  const v1 = await goodBattle();
  const id = await publish(hub, key, v1);
  hub.d1.sqlite.prepare("UPDATE packages SET downloads = 42").run();
  advance(3 * 86400);
  const v2 = await goodBattle({ title: "Moonlit Duel (fixed)", files: { "images/extra.png": { data: media.png() } } });
  const r = await upload(hub, key, v2, { packageId: id });
  assertEquals(r.start.body.version, 2);
  assertEquals(r.complete.status, 200, JSON.stringify(r.complete.body));
  const row = hub.d1.one("SELECT version, title, downloads, created_at, updated_at FROM packages WHERE id = ?", id);
  assertEquals(row.version, 2);
  assertEquals(row.title, "Moonlit Duel (fixed)");
  assertEquals(row.downloads, 42);
  assert(row.updated_at > row.created_at);
  const v1Key = hub.d1.one("SELECT r2_key FROM uploads WHERE version = 1").r2_key;
  assertEquals(hub.d1.one("SELECT r2_key FROM trash").r2_key, v1Key);
  assertEquals(hub.d1.one("SELECT r2_key FROM packages").r2_key, hub.d1.one("SELECT r2_key FROM uploads WHERE version = 2").r2_key);
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 200);
  assertEquals((await hub.call("GET", `/v1/files/${id}/2/package`)).status, 200);
  assertEquals((await hub.call("GET", `/v1/files/${id}/3/package`)).status, 404);
  advance(86401);
  await hub.cron(HOURLY);
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 404);
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, String(v2.length));
});

Deno.test("new versions: only your own live entry, of the same kind", async () => {
  const { hub, key } = await ready();
  const id = await publish(hub, key, await goodBattle());
  await register(hub, fakeKey(2), "Other");
  assertEquals((await startOnly(hub, fakeKey(2), await goodBattle(), { packageId: id })).body.error, "not_yours");
  assertEquals((await startOnly(hub, key, await goodPack(), { packageId: id, kind: "charts" })).status, 400);
  assertEquals((await startOnly(hub, key, await goodBattle(), { packageId: "zzzzzzzzzz" })).body.error, "no_package");
  await hub.call("POST", `/v1/admin/packages/${id}/hide`, { admin: true, json: {} });
  assertEquals((await startOnly(hub, key, await goodBattle(), { packageId: id })).body.error, "not_live");
});

Deno.test("new versions: an entry hidden while its new version uploads isn't brought back", async () => {
  const { hub, key } = await ready();
  const id = await publish(hub, key, await goodBattle());
  advance(3 * 86400);
  const v2 = await goodBattle({ title: "v2" });
  const r = await upload(hub, key, v2, { packageId: id, noComplete: true });
  await hub.call("POST", `/v1/admin/packages/${id}/hide`, { admin: true, json: {} });
  const done = await hub.call("POST", `/v1/uploads/${r.start.body.uploadId}/complete`, { key, json: {} });
  assertEquals(done.status, 409);
  assertEquals(done.body.error, "not_live");
  assertEquals(hub.d1.one("SELECT status, version FROM packages").status, "hidden");
  assertEquals(hub.d1.one("SELECT version FROM packages").version, 1);
});

Deno.test("pictures: a new uploader's thumbnail waits 24 h; a trusted uploader's shows at once", async () => {
  const { hub, key } = await ready();
  const thumb = thumbB64();
  const id = await publish(hub, key, await goodBattle(), { thumb });
  let list = await hub.call("GET", "/v1/packages");
  assertEquals(list.body.items[0].thumb, undefined);
  advance(24 * 3600 + 1);
  await hub.cron(HOURLY);
  list = await hub.call("GET", "/v1/packages?kind=battle");
  assertEquals(list.body.items[0].thumb, thumb);
  // Seven days later the key is trusted, and a changed thumbnail shows at once.
  advance(7 * 86400);
  await hub.cron("17 3 * * *");
  const r = await upload(hub, key, await goodBattle({ title: "v2" }), { packageId: id, thumb: thumbB64({ width: 100 }) });
  assertEquals(r.complete.status, 200);
  assertEquals(hub.d1.one("SELECT picture_state FROM packages").picture_state, "shown");
});

Deno.test("pictures: an unchanged thumbnail keeps its state on a new version", async () => {
  const { hub, key } = await ready();
  const thumb = thumbB64();
  const id = await publish(hub, key, await goodBattle(), { thumb });
  await hub.call("POST", `/v1/admin/packages/${id}/picture`, { admin: true, json: { show: false } });
  advance(3 * 86400);
  await publish(hub, key, await goodBattle({ title: "v2" }), { packageId: id, thumb });
  assertEquals(hub.d1.one("SELECT picture_state FROM packages").picture_state, "refused");
});

Deno.test("quotas: a new key may upload 2 a day for its first 48 h, then 10 a day", async () => {
  const { hub, key } = await ready();
  await publish(hub, key, await goodPack({ title: "a" }), { kind: "charts" });
  await publish(hub, key, await goodPack({ title: "b", songs: ["Firefly - 2"] }), { kind: "charts", songs: ["Firefly - 2"] });
  const third = await startOnly(hub, key, await goodPack({ title: "c", songs: ["Firefly - 3"] }), { kind: "charts", songs: ["Firefly - 3"] });
  assertEquals(third.status, 429);
  assertEquals(third.body.error, "daily_limit");
  assertEquals(third.body.probation, true);
  advance(49 * 3600);
  for (let i = 0; i < 10; i++) {
    const s = [`Song ${i}`];
    await publish(hub, key, await goodPack({ title: "p" + i, songs: s }), { kind: "charts", songs: s });
  }
  const eleventh = await startOnly(hub, key, await goodPack({ title: "x", songs: ["Song x"] }), { kind: "charts", songs: ["Song x"] });
  assertEquals(eleventh.body.error, "daily_limit");
  assertEquals(eleventh.body.probation, false);
  const me = await hub.call("GET", "/v1/me", { key });
  assertEquals(me.body.limits.uploadsToday, 10);
});

Deno.test("quotas: bytes per day for a new key", async () => {
  const { hub, key } = await ready();
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { probation_bytes_day: "10000" } });
  const r = await startOnly(hub, key, await goodBattle({ song: new Uint8Array(20000).fill(1) }));
  assertEquals(r.body.error, "daily_limit");
});

Deno.test("quotas: whole-hub caps on completed uploads from untrusted keys, and on attempts; trusted keys are outside", async () => {
  const hub = await makeHub();
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { uploads_global_day: "1" } });
  await register(hub, fakeKey(1), "One");
  await register(hub, fakeKey(2), "Two");
  await publish(hub, fakeKey(1), await goodPack({ title: "a" }), { kind: "charts" });
  const blocked = await startOnly(hub, fakeKey(2), await goodPack({ title: "b", songs: ["Firefly - 2"] }), { kind: "charts", songs: ["Firefly - 2"] });
  assertEquals(blocked.body.error, "daily_limit");
  hub.d1.sqlite.prepare("UPDATE uploaders SET trusted_at = 1 WHERE name = 'Two'").run();
  const trusted = await startOnly(hub, fakeKey(2), await goodPack({ title: "b", songs: ["Firefly - 2"] }), { kind: "charts", songs: ["Firefly - 2"] });
  assertEquals(trusted.status, 201);
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { attempts_global_day: "2" } });
  await register(hub, fakeKey(3), "Three");
  const attempt = await startOnly(hub, fakeKey(3), await goodPack({ title: "c", songs: ["Firefly - 3"] }), { kind: "charts", songs: ["Firefly - 3"] });
  assertEquals(attempt.body.error, "daily_limit");
  // A trusted key is outside the whole-hub attempts cap too (DESIGN-HUB 2.9).
  hub.d1.sqlite.prepare("UPDATE uploaders SET trusted_at = 1 WHERE name = 'Three'").run();
  const trustedAttempt = await startOnly(hub, fakeKey(3), await goodPack({ title: "c", songs: ["Firefly - 3"] }), { kind: "charts", songs: ["Firefly - 3"] });
  assertEquals(trustedAttempt.status, 201);
});

Deno.test("quotas: live entries per key", async () => {
  const { hub, key } = await ready();
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { live_per_key: "1" } });
  await publish(hub, key, await goodPack(), { kind: "charts" });
  const r = await startOnly(hub, key, await goodPack({ title: "b", songs: ["Firefly - 2"] }), { kind: "charts", songs: ["Firefly - 2"] });
  assertEquals(r.body.error, "too_many_live");
});

Deno.test("storage: bytes are reserved as parts arrive, and uploads close when storage or the database is full", async () => {
  const { hub, key } = await ready();
  const bytes = await bigBattle();
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { storage_cap_bytes: String(bytes.length + 100) } });
  const { body: up } = await startOnly(hub, key, bytes);
  assertEquals((await hub.call("PUT", `/v1/uploads/${up.uploadId}/parts/1`, { key, bytes: bytes.subarray(0, 8388608) })).status, 200);
  hub.d1.sqlite.prepare("UPDATE settings SET v = '8388608' WHERE k = 'storage_used'").run();
  const full = await hub.call("PUT", `/v1/uploads/${up.uploadId}/parts/2`, { key, bytes: bytes.subarray(8388608) });
  assertEquals(full.status, 503);
  assertEquals(full.body.error, "storage_full");
  await hub.call("DELETE", `/v1/uploads/${up.uploadId}`, { key });
  hub.d1.sqlite.prepare("UPDATE settings SET v = '0' WHERE k = 'storage_used'").run();
  hub.d1.sqlite.prepare("UPDATE settings SET v = '500000000' WHERE k = 'd1_size_bytes'").run();
  assertEquals((await startOnly(hub, key, await goodBattle())).body.error, "storage_full");
});

Deno.test("switches: uploads closed in settings or by UPLOADS_OPEN=false; READ_ONLY; new keys younger than the gate", async () => {
  const { hub, key } = await ready();
  hub.vars.UPLOADS_OPEN = "false";
  assertEquals((await startOnly(hub, key, await goodBattle())).body.error, "uploads_closed");
  assertEquals((await register(hub, fakeKey(5), "Late")).body.error, "closed");
  hub.vars.UPLOADS_OPEN = "true";
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { uploads_open: "0" } });
  assertEquals((await startOnly(hub, key, await goodBattle())).body.error, "uploads_closed");
  await hub.call("PUT", "/v1/admin/settings", { admin: true, json: { uploads_open: "1" } });
  await hub.call("PUT", "/v1/admin/gates", { admin: true, json: { closeUploadsForKeysYoungerThanDays: 2 } });
  assertEquals((await startOnly(hub, key, await goodBattle())).body.error, "key_too_new");
  advance(3 * 86400);
  hub.vars.READ_ONLY = "true";
  assertEquals((await startOnly(hub, key, await goodBattle())).body.error, "read_only");
  hub.vars.READ_ONLY = "false";
  assertEquals((await startOnly(hub, key, await goodBattle())).status, 201);
  const info = await hub.call("GET", "/v1/info");
  assertEquals(info.body.uploadsOpen, true);
  assertEquals(FAKE_ADMIN.length >= 32, true);
});

Deno.test("rows written: a publish stays small and needs no cache purge", async () => {
  const { hub, key } = await ready();
  const before = hub.d1.statements.length;
  await publish(hub, key, await goodBattle());
  const writes = hub.d1.statements.slice(before).filter((s) => s.changes > 0);
  const rows = writes.reduce((n, s) => n + s.changes, 0);
  assert(rows <= 25, `rows written: ${rows}`);
  assertEquals(hub.cache.purged.length, 0);
});

Deno.test("races: a refusal never deletes the file of an upload that another complete just published", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle({ files: { "audio/song.ogg": { data: media.flac() } } });
  const r = await upload(hub, key, bytes, { noComplete: true });
  const id = r.start.body.uploadId;
  const stored = JSON.stringify({ status: 200, body: { packageId: r.start.body.packageId, version: 1, status: "live" } });
  // Just before the refusal is written, the other complete wins.
  hub.d1.beforeBatch = () => hub.d1.sqlite.prepare("UPDATE uploads SET state = 'live', result = ? WHERE id = ?").run(stored, id);
  const done = await hub.call("POST", `/v1/uploads/${id}/complete`, { key, json: {} });
  assertEquals(done.status, 200);
  assertEquals(done.body.status, "live");
  assertEquals(hub.r2.objects.size, 1);
});

Deno.test("races: an expiry never deletes the file of an upload that completed meanwhile", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle();
  const r = await upload(hub, key, bytes, { noComplete: true });
  advance(2 * 3600);
  hub.d1.beforeBatch = () => hub.d1.sqlite.prepare("UPDATE uploads SET state = 'live' WHERE id = ?").run(r.start.body.uploadId);
  const report = await hub.cron(HOURLY);
  assertEquals(report.expire.expired, 0);
  assertEquals(hub.r2.objects.size, 1);
});

// ---- parts that arrive after their upload was stopped (the review's SEC-01, SEC-03, SEC-04) ---------------

const tick = () => new Promise((r) => setTimeout(r, 0));

/** PUT part n with a body the test feeds: { done: Promise<Response>, push(bytes), close() }. */
function slowPart(hub, key, uploadId, n, length) {
  let controller;
  const stream = new ReadableStream({ start(c) { controller = c; } });
  const headers = new Headers({
    "Authorization": `Bearer ${key}`, "X-NBB-Client": "1", "Content-Type": "application/octet-stream",
    "Content-Length": String(length), "CF-Connecting-IP": "203.0.113.7",
  });
  const request = new Request(`${ORIGIN}/v1/uploads/${uploadId}/parts/${n}`, { method: "PUT", headers, body: stream, duplex: "half" });
  const done = worker.fetch(request, hub.env(), hub.ctx());
  return { done, push: (b) => controller.enqueue(b), close: () => controller.close() };
}

async function waitFor(fn) {
  for (let i = 0; i < 200 && !fn(); i++) await tick();
  assert(fn(), "timed out waiting");
}

Deno.test("races: a part still arriving after its upload was stopped lands on that upload's own key and is deleted; the version published meanwhile is untouched", async () => {
  const { hub, key } = await ready();
  const v1 = await goodBattle({ title: "Moonlit Duel" });
  const id = await publish(hub, key, v1);
  // U1: a new version declaring the SHA-256 of bytes that were never checked (not even a zip).
  const evil = new TextEncoder().encode("MZ this is not a zip and was never checked by the hub ".repeat(40));
  const u1 = await hub.call("POST", "/v1/uploads", { key, json: startBody(evil, { packageId: id, sha256: await sha256Hex(evil), entriesSha256: "0".repeat(64) }) });
  assertEquals(u1.status, 201);
  const putsBefore = hub.r2.ops.put;
  const part = slowPart(hub, key, u1.body.uploadId, 1, evil.length);
  await waitFor(() => hub.r2.ops.put > putsBefore); // past every check, streaming into R2
  assertEquals((await hub.call("DELETE", `/v1/uploads/${u1.body.uploadId}`, { key })).status, 204);
  // U2 publishes a real v2 while U1's part is still arriving.
  const v2 = await goodBattle({ title: "Moonlit Duel (remaster)" });
  const u2 = await upload(hub, key, v2, { packageId: id });
  assertEquals(u2.complete.status, 200);
  assertEquals(u2.complete.body.version, 2);
  part.push(evil);
  part.close();
  const late = await part.done;
  assertEquals(late.status, 409);
  assertEquals((await late.json()).error, "upload_closed");
  // The served v2 is the file that was checked, and U1's late object is gone again.
  const served = await hub.call("GET", `/v1/files/${id}/2/package`);
  assertEquals(await sha256Hex(served.body), await sha256Hex(v2));
  const keys = [...hub.r2.objects.keys()].sort();
  const want = hub.d1.q("SELECT r2_key FROM uploads WHERE state = 'live' ORDER BY version").map((r) => r.r2_key).sort();
  assertEquals(keys, want);
  assert(!keys.some((k) => k.endsWith(u1.body.uploadId)), "no object for the stopped upload");
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, String(v1.length + v2.length));
});

Deno.test("quotas: start-then-stop is capped per key (3 starts for each upload it may finish), and late parts leave nothing in R2", async () => {
  const { hub, key } = await ready(); // a new key: 2 uploads a day, so 6 starts
  const blob = new Uint8Array(1024 * 1024);
  blob[0] = 1;
  const hash = await sha256Hex(blob);
  let rounds = 0, last;
  for (;;) {
    last = await hub.call("POST", "/v1/uploads", { key, json: startBody(blob, { sha256: hash, entriesSha256: "0".repeat(64) }) });
    if (last.status !== 201) break;
    const before = hub.r2.ops.put;
    const part = slowPart(hub, key, last.body.uploadId, 1, blob.length);
    await waitFor(() => hub.r2.ops.put > before);
    assertEquals((await hub.call("DELETE", `/v1/uploads/${last.body.uploadId}`, { key })).status, 204);
    part.push(blob);
    part.close();
    assertEquals((await part.done).status, 409);
    rounds++;
  }
  assertEquals(rounds, 6);
  assertEquals([last.status, last.body.error, last.body.attemptsPerDay], [429, "daily_limit", 6]);
  assert(Number(last.headers.get("Retry-After")) > 0);
  assertEquals([...hub.r2.objects.keys()].filter((k) => k.startsWith("pkg/")), []);
  assertEquals(hub.d1.one("SELECT v FROM settings WHERE k = 'storage_used'").v, "0");
  const me = await hub.call("GET", "/v1/me", { key });
  assertEquals([me.body.limits.uploadsToday, me.body.limits.attemptsToday, me.body.limits.attemptsPerDay], [0, 6, 6]);
});

Deno.test("parts: a part for an upload that expired while it arrived is refused and its object deleted", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle();
  const s = await startOnly(hub, key, bytes);
  const before = hub.r2.ops.put;
  const part = slowPart(hub, key, s.body.uploadId, 1, bytes.length);
  await waitFor(() => hub.r2.ops.put > before);
  advance(16 * 60);
  await hub.cron(HOURLY); // expires it: nothing was stored yet
  part.push(bytes);
  part.close();
  assertEquals((await part.done).status, 409);
  assertEquals(hub.r2.objects.size, 0);
  assertEquals(hub.d1.one("SELECT count(*) AS n FROM upload_parts").n, 0);
});

// ---- new versions with the same files, pictures after a refusal (the review's C4, SEC-10) ----------------

Deno.test("new versions: the same files with a new description or picture publish (only another entry's files are a duplicate)", async () => {
  const { hub, key } = await ready();
  const bytes = await goodBattle();
  const id = await publish(hub, key, bytes, { description: "tpyo" });
  const r = await upload(hub, key, bytes, { packageId: id, description: "typo fixed", thumb: thumbB64() });
  assertEquals(r.start.status, 201);
  assertEquals(r.complete.status, 200, JSON.stringify(r.complete.body));
  const d = await hub.call("GET", `/v1/packages/${id}`);
  assertEquals([d.body.version, d.body.description], [2, "typo fixed"]);
  // Both versions are served from their own objects while v1 waits in the trash.
  assertEquals((await hub.call("GET", `/v1/files/${id}/1/package`)).status, 200);
  assertEquals((await hub.call("GET", `/v1/files/${id}/2/package`)).status, 200);
  assertEquals(hub.r2.objects.size, 2);
});

Deno.test("pictures: after the owner refuses one, the uploader's new thumbnails wait even once the key is trusted", async () => {
  const { hub, key } = await ready();
  const id = await publish(hub, key, await goodBattle({ title: "Trusted entry" }), { thumb: thumbB64() });
  advance(8 * 86400);
  await hub.cron("17 3 * * *");
  assert(hub.d1.one("SELECT trusted_at FROM uploaders").trusted_at !== null, "trusted");
  assertEquals((await hub.call("POST", `/v1/admin/packages/${id}/picture`, { admin: true, json: { show: false } })).status, 200);
  // A new version with the picture changed a little: it waits instead of showing at once.
  await publish(hub, key, await goodBattle({ title: "Trusted entry (v2)" }), { packageId: id, thumb: thumbB64({ width: 120 }) });
  assertEquals(hub.d1.one("SELECT picture_state FROM packages").picture_state, "waiting");
  assertEquals((await hub.call("GET", `/v1/packages/${id}`)).body.thumb, undefined);
  // The same for their other entries, and the daily trust run doesn't undo it.
  await hub.cron("17 3 * * *");
  const other = await publish(hub, key, await goodBattle({ title: "Another", id: "4f2b8c1e-7d6a-4b5c-9e8f-0a1b2c3d4e5f" }), { thumb: thumbB64({ width: 64 }) });
  assertEquals(hub.d1.one("SELECT picture_state FROM packages WHERE id = ?", other).picture_state, "waiting");
  // The owner's OK still shows it at once.
  await hub.call("POST", `/v1/admin/packages/${other}/picture`, { admin: true, json: { show: true } });
  assertEquals(hub.d1.one("SELECT picture_state FROM packages WHERE id = ?", other).picture_state, "shown");
});
