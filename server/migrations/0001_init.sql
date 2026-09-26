-- nocturne but better hub: the whole schema (DESIGN-HUB 2.4).
-- Later migrations are ADDITIVE ONLY (new tables, new nullable columns, new indexes): migrations run
-- before the new code deploys, so the old code must keep working against the new schema.

CREATE TABLE uploaders (
  seq             INTEGER PRIMARY KEY,
  id              TEXT NOT NULL UNIQUE,                  -- 'u' + 12 base32
  key_hash        TEXT NOT NULL UNIQUE,                  -- hex SHA-256 of the player's key
  name            TEXT NOT NULL DEFAULT '',
  name_changed_at INTEGER,                               -- renames: once a day
  created_at      INTEGER NOT NULL,
  status          TEXT NOT NULL DEFAULT 'ok' CHECK (status IN ('ok','banned','revoked')),
  strikes         INTEGER NOT NULL DEFAULT 0,            -- copyright removals
  trusted_at      INTEGER,                               -- set by the daily cron: a live package older than 7 days, no strikes
  picture_refused_at INTEGER                             -- the owner refused one of this key's thumbnails: its new ones always wait
);
CREATE INDEX ul_created ON uploaders(created_at);

CREATE TABLE packages (
  seq            INTEGER PRIMARY KEY,                    -- the rowid alias; the FTS rows are keyed on it
  id             TEXT NOT NULL UNIQUE,
  kind           TEXT NOT NULL CHECK (kind IN ('battle','charts')),
  uploader_id    TEXT NOT NULL REFERENCES uploaders(id),
  status         TEXT NOT NULL CHECK (status IN ('live','hidden','removed','deleted','quarantined')),
  version        INTEGER NOT NULL,
  title          TEXT NOT NULL,                          -- from battle.json / manifest.json
  title_key      TEXT NOT NULL,                          -- NFKD, marks removed, lower case: the Title sort
  artist         TEXT NOT NULL,
  author         TEXT NOT NULL,                          -- the charter
  description    TEXT NOT NULL DEFAULT '',
  lanes          INTEGER NOT NULL CHECK (lanes IN (4,5)),
  difficulties   TEXT NOT NULL,                          -- JSON (declared; the client checks it against the file)
  songs          TEXT,                                   -- JSON (charts: the songs from manifest.json with their declared difficulties)
  battle_id      TEXT,                                   -- battles: battle.json id (lower-case GUID), from the file
  flags          TEXT NOT NULL DEFAULT '{}',             -- JSON: gear, level, dialogue, video (from the file)
  source         TEXT,                                   -- JSON: battle.json source (osu!mania mapper)
  contents       TEXT NOT NULL,                          -- JSON: the contents summary (from the central directory)
  format         INTEGER NOT NULL,
  requires       TEXT NOT NULL DEFAULT '[]',             -- JSON: feature names (2.14)
  length_s REAL, bpm_min REAL, bpm_max REAL,
  file_size      INTEGER NOT NULL,
  file_sha256    TEXT NOT NULL,                          -- declared; R2 checks it for single-part uploads; the client always checks it
  r2_key         TEXT NOT NULL,                          -- the current version's object: pkg/<id>/<version>/<upload id>, one per upload
  fingerprint    TEXT NOT NULL,                          -- server-computed: SHA-256 of the sorted (name, size, CRC-32) list
  entries        INTEGER NOT NULL,
  picture_state  TEXT NOT NULL DEFAULT 'waiting' CHECK (picture_state IN ('waiting','shown','refused')),
  picture_due    INTEGER,                                -- shown automatically after this time unless refused
  bytes_stored   INTEGER NOT NULL,
  created_at     INTEGER NOT NULL,
  updated_at     INTEGER NOT NULL,
  downloads      INTEGER NOT NULL DEFAULT 0,             -- folded in from Analytics Engine (2.12)
  reports_open   INTEGER NOT NULL DEFAULT 0,
  removed_reason TEXT, removed_note TEXT, removed_at INTEGER
);
-- Browse: one index per sort, with the equality filters first. The Worker asks each (kind, lanes) facet
-- separately and merges them, so every page read walks an index, never the table.
CREATE INDEX pk_new   ON packages(status, kind, lanes, created_at DESC, seq DESC);
CREATE INDEX pk_pop   ON packages(status, kind, lanes, downloads DESC, seq DESC);
CREATE INDEX pk_title ON packages(status, kind, lanes, title_key, seq);
CREATE INDEX pk_owner ON packages(uploader_id, status, created_at DESC, seq DESC);   -- My uploads, More by this uploader
CREATE INDEX pk_fp    ON packages(fingerprint);                                     -- exact re-uploads
CREATE INDEX pk_pic   ON packages(picture_state, picture_due);
CREATE INDEX pk_upd   ON packages(updated_at);                                      -- the owner's recent list and bulk actions
CREATE INDEX pk_key   ON packages(r2_key);                                          -- the orphan sweep
CREATE UNIQUE INDEX pk_bid_live ON packages(battle_id)
  WHERE battle_id IS NOT NULL AND status IN ('live','hidden');                      -- one live package per battle id

CREATE TABLE battle_blocks (                             -- "removed before": a battle id removed for copyright
  battle_id TEXT NOT NULL, uploader_id TEXT NOT NULL, package_id TEXT NOT NULL, at INTEGER NOT NULL,
  PRIMARY KEY (battle_id, uploader_id)) WITHOUT ROWID;

-- A regular FTS5 table (it stores its own copy of the text). Kept in step by the Worker inside the publish
-- and delete batches (no triggers, so a download count never rewrites it). rowid = packages.seq.
-- The text is the Worker's normalized search form (lower case, letters, digits and marks, single spaces),
-- so the index and the query split words the same way. Rebuild it with POST /v1/admin/reindex.
CREATE VIRTUAL TABLE packages_fts USING fts5(
  title, artist, author, songs, description,
  tokenize = 'unicode61 remove_diacritics 2');
INSERT INTO packages_fts(packages_fts, rank) VALUES('rank', 'bm25(10.0, 6.0, 6.0, 3.0, 1.0)');

CREATE TABLE thumbs (package_id TEXT PRIMARY KEY, version INTEGER NOT NULL, b64 TEXT NOT NULL) WITHOUT ROWID;

CREATE TABLE uploads (
  id TEXT PRIMARY KEY, uploader_id TEXT NOT NULL, client_upload_id TEXT NOT NULL,
  package_id TEXT NOT NULL, version INTEGER NOT NULL, is_update INTEGER NOT NULL, kind TEXT NOT NULL,
  r2_key TEXT NOT NULL, r2_upload_id TEXT,               -- null for a single-part upload
  size INTEGER NOT NULL, sha256 TEXT NOT NULL, entries_sha256 TEXT NOT NULL,
  part_size INTEGER NOT NULL, parts INTEGER NOT NULL, received_bytes INTEGER NOT NULL DEFAULT 0,
  meta TEXT,                                             -- JSON incl. the thumbnail; cleared when the upload leaves 'open'/'completing'
  state TEXT NOT NULL CHECK (state IN ('open','completing','live','refused','aborted','expired')),
  result TEXT,                                           -- stored answer for a repeated complete
  created_at INTEGER NOT NULL, last_part_at INTEGER, completing_at INTEGER, updated_at INTEGER NOT NULL,
  UNIQUE (uploader_id, client_upload_id));
CREATE INDEX up_owner   ON uploads(uploader_id, created_at);                -- per-key quotas
CREATE INDEX up_day     ON uploads(state, updated_at);                      -- global quotas, cron, reserved storage
CREATE INDEX up_created ON uploads(created_at);                             -- upload attempts, whole hub
CREATE UNIQUE INDEX up_one_key ON uploads(uploader_id) WHERE state IN ('open','completing');   -- one open upload per key
CREATE UNIQUE INDEX up_one_pkg ON uploads(package_id)  WHERE state IN ('open','completing');

CREATE TABLE upload_parts (upload_id TEXT NOT NULL, n INTEGER NOT NULL, etag TEXT NOT NULL, bytes INTEGER NOT NULL,
  PRIMARY KEY (upload_id, n)) WITHOUT ROWID;

CREATE TABLE reports (
  package_id TEXT NOT NULL, reporter_hash TEXT NOT NULL, version INTEGER NOT NULL,   -- the key's SHA-256; the key needn't be registered
  reason TEXT NOT NULL CHECK (reason IN ('copyright','offensive','picture','broken','malicious','spam','other')),
  note TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL, resolved_at INTEGER, resolution TEXT,
  PRIMARY KEY (package_id, reporter_hash)) WITHOUT ROWID;
CREATE INDEX rp_open     ON reports(resolved_at, created_at);
CREATE INDEX rp_reporter ON reports(reporter_hash, created_at);
CREATE INDEX rp_created  ON reports(created_at);

CREATE TABLE purge_queue (id INTEGER PRIMARY KEY, tags TEXT NOT NULL, created_at INTEGER NOT NULL,
  tries INTEGER NOT NULL DEFAULT 0, done_at INTEGER, last_error TEXT);
CREATE INDEX pq_pending ON purge_queue(done_at, id);

CREATE TABLE settings (k TEXT PRIMARY KEY, v TEXT NOT NULL) WITHOUT ROWID;
INSERT INTO settings (k, v) VALUES
 ('uploads_open','1'), ('new_keys_open','1'), ('min_client','2.7.0'), ('message',''), ('hub_name','nocturne but better hub'),
 ('takedown_contact',''), ('max_package_bytes','104857600'), ('max_entries','1000'), ('max_unpacked_bytes','209715200'),
 ('storage_cap_bytes','9000000000'), ('storage_used','0'), ('d1_size_bytes','0'), ('d1_size_at','0'), ('d1_close_bytes','400000000'),
 ('uploads_per_key_day','10'), ('bytes_per_key_day','524288000'), ('probation_hours','48'),
 ('probation_uploads_day','2'), ('probation_bytes_day','104857600'), ('live_per_key','50'),
 ('uploads_global_day','200'), ('attempts_global_day','1000'), ('new_keys_global_day','2000'),
 ('reports_per_key_day','20'), ('reports_global_day','1000'), ('close_uploads_key_days','0'),
 ('strikes_to_ban','3'), ('picture_delay_hours','24'), ('max_songs_per_pack','40'),
 ('stats_salt',''), ('stats_salt_prev',''), ('stats_folded_until','0'), ('orphan_cursor',''),
 ('reports_per_address_day','50'), ('new_keys_per_address_day','20'),
 ('cursor_key', lower(hex(randomblob(32))));                -- signs the list's page cursors; never shown or backed up

CREATE TABLE audit (id INTEGER PRIMARY KEY, at INTEGER NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL,
  package_id TEXT, uploader_id TEXT, detail TEXT);
CREATE INDEX au_at  ON audit(at);
CREATE INDEX au_pkg ON audit(package_id, at);

CREATE TABLE trash (r2_key TEXT PRIMARY KEY, bytes INTEGER NOT NULL, delete_after INTEGER NOT NULL, why TEXT) WITHOUT ROWID;
CREATE INDEX tr_due ON trash(delete_after);

-- Per-address daily counts for the whole-hub caps that one address could otherwise fill (reports, new keys).
-- h is an HMAC of the address with the day's stats salt; the daily cron empties the table when it replaces
-- the salt, so a row lives at most a day and can't be traced back once the salt is gone.
CREATE TABLE address_day (h TEXT NOT NULL, what TEXT NOT NULL, n INTEGER NOT NULL, PRIMARY KEY (h, what)) WITHOUT ROWID;
