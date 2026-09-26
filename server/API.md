# hub api v1

what the mod talks to. base address: `https://hub.nocturnbutbetter.com` (a qa build may use `http://127.0.0.1:8787`, the local stand-in). everything is under `/v1/`; inside v1 changes only ever add fields, so ignore fields you don't know. a breaking change would be `/v2/`, next to v1.

## conventions

- json in and out, utf-8. times are unix seconds.
- every request sends `User-Agent: NocturneButBetter/<version> (<BepInEx|MelonLoader>)` and `X-NBB-Client: 1`.
- every write (post, put, delete) MUST send `X-NBB-Client: 1`, and a body-carrying write exactly `Content-Type: application/json` (charset utf-8 allowed) or, for upload parts, `application/octet-stream`. anything else is 400 `bad_client`. `OPTIONS` is 405.
- keyed routes send `Authorization: Bearer <key>` on that request only. public routes never need it (the server ignores it there).
- ids are lower-case crockford base32: packages `^[0-9a-hjkmnp-tv-z]{10}$`, uploaders `^u[0-9a-hjkmnp-tv-z]{12}$`, uploads `^up[0-9a-hjkmnp-tv-z]{16}$`. check them before using them in a path. versions are positive integers.
- the mod builds every url itself from the base address. the server never hands out urls to follow (apart from `legalUrl`, for showing).
- errors are always `{ "error": "<code>", "message": "<plain words>", ...extra }`, never cached. show `message` only for codes you don't know. 429s carry `Retry-After` and `retryAfter`.
- a response that isn't json (for example cloudflare's error 1027 page, when the free plan's daily allowance is spent) means "too busy today".

codes the mod should know: `bad_client`, `bad_query`, `bad_request` (+`problems`), `not_found`, `method_not_allowed`, `no_key`, `bad_key`, `unknown_key`, `banned`, `revoked`, `not_yours`, `removed`, `deleted`, `unavailable`, `slow_down`, `daily_limit`, `rename_limit`, `bad_name`, `bad_reason`, `too_big`, `length_required`, `bad_part`, `upload_in_progress`, `upload_closed`, `busy`, `missing_parts` (+`missing`), `package_refused` (+`problems`), `battle_taken` (+`packageId`), `battle_yours` (+`packageId`), `battle_changed`, `removed_before`, `duplicate` (+`packageId`), `not_live`, `no_package`, `too_many_live`, `key_too_new`, `key_in_use`, `client_too_old`, `uploads_closed`, `closed`, `read_only`, `storage_full`, `busy_today`, `db_unavailable`, `not_set_up`, `server_error`.

## text rules (the mod applies the same ones)

every free-text field: nfc; remove c0 and c1 control characters (descriptions keep line breaks), u+00ad, u+034f, u+061c, u+115f, u+1160, u+180e, u+200b-200f, u+202a-202e, u+2060-2064, u+2066-2069, u+3164, u+feff, u+ffa0 and u+e0000-e007f; collapse whitespace and trim; cap in unicode code points: title 100, artist 100, author 64, pack title 100, description 1000, name 32, report note 500, song 100, difficulty name 32. a name must keep a letter or digit and can't be admin, owner, moderator or hub (ignoring case and punctuation). show server text with rich text OFF.

## public routes

### `GET /v1/info` (cached 5 minutes)

```
{ "api": 1, "hub": "nocturne but better hub", "minClient": "2.8.0", "uploadsOpen": true, "counts": false,
  "maxPackageBytes": 104857600, "maxUnpackedBytes": 209715200, "partSize": 8388608, "maxEntries": 1000,
  "maxSongsPerPack": 40, "maxThumbB64": 16384,
  "text": { "title": 100, "artist": 100, "author": 64, "packTitle": 100, "description": 1000, "name": 32, "note": 500 },
  "media": { "audio": ["wav-pcm","ogg-vorbis","mp3"], "pictures": ["png","jpeg","gif"], "video": ["webm-vp8"] },
  "message": "", "legalUrl": "https://hub.nocturnbutbetter.com/legal", "time": 1790380800 }
```

a mod older than `minClient` disables browse and upload. `counts` false means cards carry `downloads: null` and "most downloaded" should be hidden.

### `GET /v1/packages` (cached 60 s)

parameters, all optional, IN THIS ORDER, each at most once, never empty, percent-encoded exactly like javascript's `encodeURIComponent` (spaces as `%20`). leave defaults out. anything else is 400 `bad_query`.

| parameter | values |
|---|---|
| `kind` | `battle` or `charts` (leave out for both) |
| `lanes` | `4` or `5` (leave out for both) |
| `uploader` | an uploader id: "more by this uploader" (newest first, unless searching) |
| `q` | the normalized search (below) |
| `sort` | `popular`, `title` or `new` (`new` only together with `q`; without `q` the default is new, with `q` best match) |
| `cursor` | the `next` of the previous page, as it came |

search normalization: lower-case, split into words on anything that isn't a letter, digit or combining mark, drop words under 2 characters, cut each word at 32 characters, keep the first 4 words, join them with single spaces. `q` must be exactly that; if nothing is left, don't send `q`. the last word also matches as a prefix when it has 3 or more characters. a search shows at most its best 200 matches (8 pages).

answer: `{ "items": [Card], "next": "<cursor>" | null }`, 24 items a page.

```
Card = { "id", "kind": "battle"|"charts", "version", "status": "live", "title", "artist", "author",
  "uploader": { "id", "name", "tag" }, "lanes": 4|5,
  "difficulties": [ { "name", "level", "notes" } ],
  "songs": null | [ { "song": "Firefly - 1", "difficulties": [...] } ],
  "battleId": "<guid>" | null, "size", "downloads": <int> | null, "createdAt", "updatedAt",
  "lengthSeconds": <number> | null, "bpm": [low, high] | null,
  "flags": { "gear", "level", "dialogue", "video" },
  "source": null | { "kind": "osu!mania", "mapper" }, "format", "requires": [feature names],
  "thumb": "<base64 baseline jpeg>" }   // thumb is left out while the picture waits or was refused
```

title, artist, author, lanes, battle id, songs, flags and format come from the package file itself. difficulties, length and bpm are what the uploader declared: check them against the file after download. show NEEDS A NEWER MOD when `format` is above what the mod reads or `requires` names a feature it doesn't know.

### `GET /v1/packages/lookup?ids=a,b,c` (cached 60 s)

1 to 50 package ids, sorted, no repeats, commas not encoded. answer `{ "items": [...] }` in the same order:

- `{ "id", "status": "live", "version", "updatedAt", "title", "sha256" }`
- `{ "id", "status": "unavailable" | "removed" | "deleted", "version", "updatedAt", "title", "reason"? }` (unavailable means under review; `reason` only for removals, like `copyright`)
- `{ "id", "status": "missing" }`

### `GET /v1/packages/:id` (cached 60 s, no query string)

a `Card` plus `"description"`, `"contents": { "files", "unpacked", "songs", "charts", "pictures", "videos", "json", "other", "mediaChecked", "mediaTotal" }` and `"file": { "size", "sha256", "fingerprint" }`. 404 `not_found`; 410 `removed` (+`reason`), `deleted` or `unavailable`.

### `GET /v1/files/:id/:version/package` (cached 1 day, no query string)

the package bytes, `application/octet-stream`, as an attachment. an older version keeps answering for 24 hours after a new one. 404, 410 as above. check, in this order: the size and the sha-256 against the detail; the zip rules and the fingerprint; every entry's crc-32 while inflating with caps; media by decoder; then the mod's own loader. only then move it into place.

### `POST /v1/packages/:id/installed` `{ "version": 3 }`

send once after a VERIFIED install. 204. counts a download (no key needed; 5 a minute per address).

### `POST /v1/packages/:id/report` `{ "reason", "note" }` with a key

`reason`: `copyright`, `offensive`, `picture`, `broken`, `malicious`, `spam`, `other`. the key needn't be registered. 201 `{ "status": "received" }`, 200 `{ "status": "already" }`, 400 `bad_reason`, 404, 429.

## keyed routes (always sent with `Authorization`)

the key is `nbbk1_` + 43 base64url characters (32 random bytes, no padding). the server stores its sha-256 (hex of the utf-8 key).

| route | answer |
|---|---|
| `PUT /v1/me` `{ "name" }` | 200 `{ "uploader": Uploader, "created": bool }`. registers the key at the first upload, or renames (once a day: 429 `rename_limit`). 400 `bad_name`, 403 `banned`/`revoked`, 429, 503 `closed` |
| `GET /v1/me` | 200 `{ "uploader": Uploader, "limits": { "probation", "probationUntil", "uploadsToday", "uploadsPerDay", "bytesToday", "bytesPerDay", "livePackages", "livePerKey" } }`; 401 `unknown_key` |
| `POST /v1/me/rotate` `{ "newKeyHash" }` | 200; the old key stops working at once. only the new key's sha-256 travels. save the new key after the 200; if the answer is lost, try `GET /v1/me` with each key |
| `GET /v1/me/packages` | 200 `{ "items": [MyCard] }`: every status, up to 200 |
| `DELETE /v1/packages/:id` | 204; 403 `not_yours`; 404; 410 |

`Uploader = { "id", "name", "tag", "createdAt", "status": "ok"|"banned", "strikes", "trusted" }`. `MyCard` = a `Card` (with the thumbnail whatever its state) plus `"description", "removedReason", "removedNote", "removedAt", "pictureState": "waiting"|"shown"|"refused"`. a hidden entry is "under review".

## uploading

1. `PUT /v1/me` at the first upload.
2. `POST /v1/uploads` (body at most 32 KB):

   ```
   { "clientUploadId": "<8-64 of A-Za-z0-9->",  "kind": "battle" | "charts",  "packageId": null | "<your live entry>",
     "file": { "size", "sha256": "<64 hex>", "entriesSha256": "<the fingerprint, 64 hex>" },
     "meta": { "description", "difficulties": [ { "name", "level": 0-99, "notes" } ],     // battles
               "songs": [ { "song", "difficulties": [...] } ],                            // packs: the manifest's songs
               "lengthSeconds", "bpm": [low, high], "requires": [] },
     "thumb": "<base64 baseline jpeg, at most 16384 characters, at most 256 px>",
     "rightsConfirmed": true, "client": "2.8.0" }
   ```

   201 `{ "uploadId", "packageId", "version", "partSize": 8388608, "parts", "expiresAt" }`. the same `clientUploadId` again while open gives 200 and the same body. errors: 400 `bad_request` (+`problems`), 401, 403 `banned`/`not_yours`/`key_too_new`/`client_too_old`, 404 `no_package`, 409 `upload_in_progress`/`not_live`/`too_many_live`, 413 `too_big`, 429 `slow_down`/`daily_limit` (+`retryAfter`), 503 `uploads_closed`/`storage_full`/`read_only`.
3. `PUT /v1/uploads/:u/parts/:n` for n = 1 to `parts`, one after another, raw bytes with an exact `Content-Length`: `partSize`, or the rest for the last part. 200 `{ "n", "etag" }`; 400 `bad_part` (a wrong length, or for a one-part upload bytes that don't match `sha256`), 409 `upload_closed`, 411, 413, 503 `storage_full`. a part that fails can be sent again (3 tries: after 1 s, then 5 s).
4. `POST /v1/uploads/:u/complete` (body `{}`): 200 `{ "packageId", "version", "status": "live" }`. safe to repeat: a repeat returns the stored answer. 409 `missing_parts` (+`missing`), 409 `busy` (a complete is running; retry after `retryAfter`), 422 `package_refused` (+`problems`, plain words to show), 409 `battle_taken`/`battle_yours`/`battle_changed`/`removed_before`/`duplicate`/`not_live`.
5. `DELETE /v1/uploads/:u` stops an upload (204).

an upload with no part for 15 minutes, or no new part for an hour, expires.

limits: one open upload per key; a key's first 48 hours: 2 uploads and 100 MB a day, then 10 uploads and 500 MB a day; 50 live entries per key. the owner can change these.

## the package rules the server checks at complete

the mod's builder must produce packages that pass these, and the mod checks the same (and more) on every download.

- a zip that starts with `PK\3\4` at byte 0 and ends with its end record: no comment, one disk, no zip64, at most 1000 entries, a file list of at most 1 MB that ends where the end record starts.
- each entry: not encrypted; stored or deflate; version needed at most 2.0; no entry comment; a stored entry's sizes equal; a declared size a deflate stream can actually reach; no overlap with the next entry.
- names: utf-8 (flag bit 11) or plain ascii; none of `\ : < > " | ? *`, control, bidi or invisible characters; no leading `/`; no empty, `.` or `..` part; no part ending in a dot or a space; no device names (`CON`, `PRN`, `AUX`, `NUL`, `COM0-9`, `LPT0-9`, and `COM`/`LPT` with superscript digits, with or without an extension); at most 200 characters and at most 6 folders deep; no two names the same ignoring case.
- a battle: `battle.json` at the root or inside its ONE top folder, and every entry inside that folder. a pack: `manifest.json` at the root.
- extensions: battles `json sm ogg wav mp3 png jpg jpeg gif webm`, packs `json sm`.
- per file (by the name inside the folder): json 1 MB, sm 8 MB, pictures 16 MB (32 MB under `art/`, 4 MB under `portraits/`), anything else 512 MB; 200 MB unpacked in all.
- `battle.json` / `manifest.json`: at most 256 KB, strict json (no comments, no trailing commas), matching its crc-32. keys are read ignoring case.
  - battles: `format` 2, `kind` "battle", `id` a guid (lower-cased), a `title`, `lanes` 4 or 5. flags: `gear` is an object with mode "set", `level` is a number or an object with mode "set" and a value, `dialogue` is present, `video` when a `.webm` sits under `art/`.
  - packs: `format` 1, a `title`, `lanes` 4 or 5, and `charts[]` whose `source` is "game" and whose `file` is in the zip. at most 40 songs. `meta.songs` must name exactly the manifest's songs.
- media by decoder, from the bytes: audio must be pcm or float wav (format 1 or 3, or extensible with those), ogg whose first packet is vorbis, or mp3; pictures png, jpeg or gif; video webm (doctype "webm") whose video track is `V_VP8`. at most 16 song and video files. the server checks every song and video and as many pictures as a small read budget allows (`contents.mediaChecked` of `mediaTotal`); the mod checks all of them.
- the fingerprint below must equal `entriesSha256`.

## the fingerprint

sha-256 (lower-case hex) of one line per central directory entry, `<name>\0<uncompressed size>\0<crc-32 as 8 lower-case hex>\n`, with the name exactly as stored and the size in decimal. sort the lines by the name with only `A`-`Z` lowered, comparing utf-16 code units (ordinal). it depends only on names, sizes and crcs, never on how the deflate stream came out.

## the thumbnail

a baseline jpeg (sof0) of at most 256 by 256 px, at most 20 segments before its scan, ending with eoi, at most 16384 characters of base64. the server reads its markers and never decodes it; show it only through the game's own `LoadImage` after the same check.
