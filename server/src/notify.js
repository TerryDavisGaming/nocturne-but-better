// The optional Discord webhook (NOTIFY_WEBHOOK secret): new uploads with their picture, and reports, so the
// owner hears of them without polling /admin. Never pings anyone; user text goes in inline code.

import { base64ToBytes } from "./ids.js";

const plain = (text) => "`" + String(text ?? "").replace(/`/g, "'").replace(/[\r\n]+/g, " ").slice(0, 150) + "`";

export function notify(env, ctx, text, thumbB64 = null) {
  const url = env.NOTIFY_WEBHOOK;
  if (typeof url !== "string" || !url.startsWith("https://")) return;
  const payload = { content: text.slice(0, 1900), allowed_mentions: { parse: [] } };
  let body;
  if (thumbB64) {
    const form = new FormData();
    form.append("payload_json", JSON.stringify(payload));
    form.append("files[0]", new Blob([base64ToBytes(thumbB64)], { type: "image/jpeg" }), "thumb.jpg");
    body = form;
  } else body = JSON.stringify(payload);
  const send = fetch(url, {
    method: "POST",
    headers: thumbB64 ? {} : { "Content-Type": "application/json" },
    body,
  }).then(
    (r) => {
      if (!r.ok) console.error(`route=notify code=http_${r.status}`);
    },
    () => console.error("route=notify code=failed"),
  );
  if (ctx && typeof ctx.waitUntil === "function") ctx.waitUntil(send);
}

export const uploadText = (pkg, name, isNew) =>
  `${isNew ? "new upload" : "new version"}: ${plain(pkg.title)} by ${plain(name)} (${pkg.kind}, id ${pkg.id}, v${pkg.version}). ` +
  `/admin to review.`;

export const reportText = (pkg, reason) => `report (${reason}) on ${plain(pkg.title)} (id ${pkg.id}). /admin to review.`;
