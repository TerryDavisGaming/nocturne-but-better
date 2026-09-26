# nocturne but better hub

this folder is the server behind GET CUSTOM BATTLES in the mod: an online list of custom battles and custom difficulties that players upload and download from inside the game. it runs on YOUR OWN cloudflare account at `https://hub.nocturnbutbetter.com`, as one cloudflare worker with a d1 database and an r2 bucket.

uploads go live the moment they finish. there are no accounts: each player's game makes a random secret key, and the hub stores only a scrambled form of it (sha-256). you can remove anything, and players can report entries. nothing is hidden just because it was reported.

this readme is the owner's runbook: deploying, the admin page, takedowns, spam waves, backups and updates. `API.md` describes the api the mod talks to.

[![deploy to cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/TerryDavisGaming/nocturne-but-better/tree/main/server)

## before you deploy

you need:

- the cloudflare account that owns the `nocturnbutbetter.com` domain (no "e" after "nocturn"). the hub lives at `hub.nocturnbutbetter.com`, and cloudflare only attaches a worker to a domain in the same account.
- r2 turned on in that account: r2 in the dashboard, then finish the checkout. it asks for a payment card even though the hub stays inside the free amounts. the hub closes uploads at 9 GB of files so storage can't bill you by surprise.
- two-factor sign-in on cloudflare AND on github. the deploy button builds from a github repo in your account, and whoever can push to that repo can change the server.
- an admin key, made on your own pc. open windows powershell and paste:

  ```
  $b=New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
  ```

  it prints 44 random characters. that's your `ADMIN_KEY`. put it in a password manager. NEVER paste it into a chat, an issue, the repo or the mod. it's the only key that can open `/admin`.

## deploy with the button

1. click the deploy to cloudflare button above and sign in to cloudflare and github.
2. keep the names it suggests: worker `nbb-hub`, database `nbb-hub`, bucket `nbb-hub-files`. cloudflare copies this folder into a new repo in your github account and makes the database and the bucket.
3. when it asks for `ADMIN_KEY`, paste the key you made. nothing else is required.
4. wait for the build. it installs wrangler, sets up the database tables, then deploys the worker and attaches `hub.nocturnbutbetter.com` to it.
5. in the dashboard, open workers & pages > `nbb-hub` > settings > variables and secrets. `ADMIN_KEY` must be listed as a SECRET (its value hidden). if it shows as plain text, delete it and add it again with type secret.

if the first build fails while setting up the database (the database didn't exist yet when that step ran), press retry build. until the tables exist the hub answers every api call with 503 `not_set_up`, so nothing breaks in the meantime.

the button needs `package-lock.json` in this folder, which pins the exact wrangler version the build installs. it isn't here yet: making it means running npm once, and that waits for your ok.

## the domain

the config asks cloudflare for a custom domain, `hub.nocturnbutbetter.com`, on your `nocturnbutbetter.com` zone. cloudflare makes the dns record and the certificate itself during the deploy.

- if the deploy says the name is already in use, a dns record for `hub` exists already. delete that record under your domain's dns settings and retry the build.
- to check it by hand: workers & pages > `nbb-hub` > settings > domains & routes. `hub.nocturnbutbetter.com` should be listed as a custom domain, and `workers.dev` should be OFF. the config turns workers.dev off on purpose: while it's on, a flood can go to the workers.dev address and skip the domain's rate-limiting rule below.
- leave the domain's "normalize incoming urls" setting as it is.
- turn on ALWAYS USE HTTPS: your domain > ssl/tls > edge certificates > always use https. without it the hub also answers plain http, and an owner key typed into `/admin` over http would cross the network readable (the admin page refuses to send it then).

the address must NEVER change once a mod release has it built in.

## after deploying (5 minutes)

1. open `https://hub.nocturnbutbetter.com/v1/info` in a browser. you should see json that starts with `"api":1`.
2. open `https://hub.nocturnbutbetter.com/admin` and paste your admin key. you should see an empty overview. a wrong key says "wrong key".
3. on `/admin` > settings, fill in `takedown_contact`: an address you're fine with being public, ideally not your personal e-mail. it shows on `/legal` and in the game's rules before every upload. you can also change `hub_name` and leave a `message` for players there.
4. get the check onto your pc: on github, open the repo the button made, then code > download zip, and extract it. open windows powershell in the extracted `server` folder (shift + right-click the folder > open powershell window here) and run:

   ```
   powershell -ExecutionPolicy Bypass -File tools\Check-Hub.ps1
   ```

   it reads the hub from the outside and sends two writes the hub must refuse. it needs no key. everything should say ok.
5. in your cloudflare profile, make sure the e-mail that gets abuse reports is one you read. cloudflare expects an answer within 24 hours.
6. once billing exists, set a budget alert (for example at $5) under billing. it only sends an e-mail; it doesn't stop anything.

## optional: a rate-limiting rule

your own domain gets one free waf rate-limiting rule. it drops a flood from a single address before it counts against the worker's daily allowance.

in the dashboard, open your domain, then security > security rules > create rule > rate limiting rules (older dashboards: security > waf > rate limiting rules):

- if incoming requests match: field "uri path", operator "starts with", value `/`
- counting by: ip, over 10 seconds
- when requests exceed: 50
- then: block, for 10 seconds
- if the form offers "also apply rate limiting to cached assets", leave it ON: cached answers still count against the daily allowance. the free plan doesn't show it and always counts them.

the free plan's rule can only match on the path, not the hostname, so it covers every name in your domain. that's fine while the domain serves only the hub. 50 in 10 seconds is far more than a player needs. raise it if players behind a shared address (a school, a phone network) start seeing "slow down". the free plan allows only this shape of rule: by ip, 10 second windows, 10 second blocks. the worker keeps its own per-address limits too.

## the admin page

`/admin` is your control panel. paste the admin key once per browser tab; it stays in that tab only and is gone when you close it. titles and names are shown as plain text, so an upload can't run anything on the page.

- overview: counts, storage, the database size, today's uploads and new keys, open reports, pictures waiting, and cache purges that are stuck.
- reports: open reports grouped per entry, with a count per reason.
- pictures: uploaders' thumbnails waiting to show. a new uploader's thumbnail shows after 24 hours unless you refuse it, or at once if you press show. uploaders whose entries have been live for a week with no strikes get theirs shown straight away, unless you've refused one of their pictures before: then their new ones wait too.
- entries: every entry, filtered by status, how recently it changed, how new its uploader's key is, or text. open one to hide, restore, remove, quarantine, show or refuse its picture, release its battle id, resolve its reports, or download it to look at.
- the uploader panel (from an entry): ban, unban, set strikes, turn the key off for good, remove all their entries, or move their entries to another uploader.
- spam waves: bulk hide or remove, pause uploads from new keys, close uploads, stop new keys.
- settings: every limit and text the hub uses, with the defaults beside them.
- purges and backup: see below.

when you download an entry to look at it, open it as a zip. NEVER run anything inside.

## takedowns

a copyright notice (see `/legal` for what one must contain):

1. open the entry on `/admin` and remove it with reason copyright, tick copyright strike, and write a note the uploader will see in the game.
2. the entry disappears from the hub at once. its file is kept 30 days, so a counter-notice can bring it back.
3. 3 copyright strikes ban the uploader's key. that's the repeat-infringer policy on `/legal`.
4. the same uploader can't upload that battle again. another uploader can (so removing a copy never locks out the real creator); release the battle id if you removed it by mistake.
5. a valid counter-notice: wait 10 to 14 business days, and if the claimant doesn't go to court, press restore. a restore doesn't take the strike back; set the uploader's strikes on their panel if it shouldn't count.

offensive, spam or broken entries: remove with that reason. the file is kept 24 hours. hide instead if you want to look at it first; the uploader sees "under review".

clearly illegal material: QUARANTINE it, don't delete it. quarantine takes the entry down and moves EVERY version that still exists (the current one, and older ones still waiting in the trash) to a private place that no cleanup job deletes, so the version that was reported is kept even if the uploader has since put up a new one. the entry's panel downloads any version by number, and reports say which version they were about. restore brings back only the current version; older ones stay in quarantine. quarantined files count toward the 9 GB until you delete them by hand (r2 > `nbb-hub-files` > `quarantine/<entry id>/`). in the us, child sexual abuse material must be reported to ncmec's cybertipline (report.cybertip.org) and the report kept. check the law where you live. this isn't legal advice.

when a takedown can't get through the cache (purges keep failing on `/admin` > purges) or the database is out of its daily limit (every `/admin` action errors):

1. workers & pages > `nbb-hub` > settings > variables and secrets > add, type text, name `BLOCKED_IDS`, value the entry's id. more ids go comma-separated.
2. save and deploy. the entry's page and file stop answering at once, without the database, and the new worker version starts with an empty cache.
3. once the database is back and the entry is removed on `/admin`, take the id out of `BLOCKED_IDS` again.

files are cached for one day at most, so even a stuck purge ends within a day.

## spam waves

when lots of junk arrives from new keys:

1. `/admin` > spam waves: hide (or remove) everything uploaded since the wave started, or everything from keys made since then. it works 30 entries at a time and keeps going by itself.
2. if it keeps coming, pause uploads from keys younger than 2 days, or stop new keys.
3. still going: close uploads. downloads keep working.
4. in an emergency without the database: add the variable `UPLOADS_OPEN` with value `false`, or `READ_ONLY` with value `true` (every player write stops, downloads keep working). remove it when you're done.

## a player who lost their key

their key lived on their pc; without it they can't manage their uploads. you can't tell whether the person asking is really the uploader, so the policy is: HIDE PENDING REVIEW, never delete or move entries on that request alone. a hidden entry still holds its battle id; to let them upload it again with a new key, remove the old entry with reason other (that blocks nobody). only move entries to another uploader when you can check the request.

## backups

- cloudflare keeps 7 days of database history (time travel) by itself.
- once a month, press `/admin` > backup > back up now. it writes the database tables as json files into your r2 bucket under `backup/<date>/`, a little at a time. the download-count salts are left out on purpose (see what the hub stores).
- restoring is rare and done together with the mod's developer: a fresh database is filled from those files, then `/admin` > backup > rebuild search rebuilds the search index.

## updating the server

the deployed code lives in the repo the button made in your github account. a server fix is a change pushed to that repo (by you, or by the developer with your ok); cloudflare rebuilds and redeploys on its own. database changes run before the new code and only ever add things, so the old code keeps working for the minute in between. a new version starts with an empty cache, so expect a short burst of database reads.

## optional extras

- download counts: two things go into workers & pages > `nbb-hub` > settings > variables and secrets.
  - your account id, as the text variable `STATS_ACCOUNT_ID`. it's on the workers & pages overview page, on the right, under "account id", with a copy button.
  - an api token that can only read analytics, as the secret `STATS_TOKEN`: click your profile icon (top right) > my profile > api tokens > create token > custom token > get started. give it a name, then under permissions pick "account", "account analytics", "read". under account resources pick "include" and this account. continue to summary > create token, and copy it straight into the secret (cloudflare shows it only once).

  without them, downloads are still recorded but the game shows no counts and hides "most downloaded". a token added later counts everything from the last 3 months.
- a discord webhook: add its url as the secret `NOTIFY_WEBHOOK` to get a message for every new upload (with its picture, before players see it) and every report. recommended, since uploads go live at once. it never pings anyone.
- a dmca agent: registering one with the us copyright office costs $6 for 3 years. WITHOUT a registered agent there's no section 512(c) safe harbor for you. this isn't legal advice.

## what the free plan can't stop

cloudflare's free plan allows 100,000 worker requests a day for the whole account, and cached answers count too. anyone can use that up by asking for the same page 100,000 times from many addresses, and then the hub is down for everyone until 00:00 utc. the game then says the hub is too busy today. nothing is lost, and takedowns still work through `BLOCKED_IDS`.

the rate-limiting rule above stops a flood from one address. workers paid ($5 a month) removes the daily cap, so a flood becomes a bill instead of an outage (budget alerts only e-mail you). that's worth it once real use passes about 60,000 requests a day.

the database has daily limits of its own (5 million rows read, 100,000 written). the hub counts downloads outside the database, bounds every search, and only answers list pages it handed out itself, so players can't use those up.

## what the hub stores

- a scrambled form (sha-256) of each uploader's key, never the key
- the display name each uploader picks
- what was uploaded and the details in its listing
- reports: reason, note and the reporter's scrambled key, until 90 days after you resolve them
- your actions, for a year
- download counts: an entry id and a scrambled form of the address, with the scrambling changed every day and the old one deleted after 2 days
- a count of reports and new hub keys per scrambled address (so one network can't use up the whole day's limits), deleted every day when the scrambling changes

no e-mails, steam ids, windows user names or ip addresses. the worker's per-request logs are off; its error lines hold only the route and an error code. cloudflare itself sees ip addresses as the network.

## emergency switches

add these under variables and secrets (type text). each change deploys a new version, which also empties the cache.

| variable | value | what it does |
|---|---|---|
| `BLOCKED_IDS` | ids, comma-separated | those entries' pages and files answer "not available", without the database |
| `UPLOADS_OPEN` | `false` | no uploads, and no new keys |
| `READ_ONLY` | `true` | no player writes at all; downloads and `/admin` still work |

## for developers

```
server/
  wrangler.jsonc          the worker, its bindings, the custom domain, crons, cache
  package.json            the deploy script: database migrations, then deploy (wrangler only as a build tool)
  migrations/             d1 schema (later migrations are additive only)
  src/                    the worker, plain javascript with no runtime dependencies
  test/                   deno tests with fakes for d1, r2, the rate limiters, the cache and analytics engine
  dev/serve.js            a local stand-in: the real worker with the fakes on 127.0.0.1
  tools/Check-Hub.ps1     the after-deploy check
```

the tests run offline with deno and download nothing. from the repo root:

```
set DENO_DIR=<a cache folder>
set DENO_NO_UPDATE_CHECK=1
deno test --no-remote --no-npm --allow-read=server server/test/
```

the local stand-in keeps its database and files in a data folder and prints a throwaway admin key each time it starts. `--seed` adds 60 sample entries through the real upload path. point a qa build of the mod at it with `NFS_QA_HUB_URL=http://127.0.0.1:8787`.

```
deno run --no-remote --no-npm --allow-net=127.0.0.1:8787 --allow-read=server,<data> --allow-write=<data> server/dev/serve.js <data> --seed
```

`<data>/faults.json` makes routes misbehave for qa, and is read on every request. each rule matches a method and path (`*` is one path part) and can use `times` to fire only that often:

```
{ "rules": [
  { "match": "GET /v1/files/*/*/package", "flipByte": 100, "times": 1 },
  { "match": "GET /v1/files/*/*/package", "stallAfter": 50000, "stallMs": 60000 },
  { "match": "GET /v1/packages", "delayMs": 3000 },
  { "match": "PUT /v1/uploads/*/parts/2", "status": 500, "times": 1 },
  { "match": "GET /v1/info", "html1027": true },
  { "match": "GET /v1/info", "patchJson": { "minClient": "9.9.9" } },
  { "match": "GET /v1/packages", "status": 429, "error": "slow_down", "retryAfter": 20 },
  { "match": "GET /v1/packages/*", "patchJson": { "file.sha256": "0000000000000000000000000000000000000000000000000000000000000000" } }
] }
```

other options: `truncateAt` (cut a body), `status` with `error` and `message` (any json error, like 503 `busy_today`), and `html1102: true` (cloudflare's "worker exceeded resource limits" page, which the mod must NOT show as "too busy today").
