import { assertEquals } from "./assert.js";
import { fakeKey, makeHub, register, upload } from "./helpers.js";
import { goodBattle } from "./make-fixtures.js";

Deno.test("smoke: register, upload, list, detail, download", async () => {
  const hub = await makeHub();
  const key = fakeKey(1);
  const reg = await register(hub, key);
  assertEquals(reg.status, 200, JSON.stringify(reg.body));
  const bytes = await goodBattle();
  const r = await upload(hub, key, bytes);
  assertEquals(r.start.status, 201, JSON.stringify(r.start.body));
  assertEquals(r.complete.status, 200, JSON.stringify(r.complete.body));
  const list = await hub.call("GET", "/v1/packages");
  assertEquals(list.status, 200, JSON.stringify(list.body));
  assertEquals(list.body.items.length, 1);
  const id = r.complete.body.packageId;
  const detail = await hub.call("GET", "/v1/packages/" + id);
  assertEquals(detail.status, 200, JSON.stringify(detail.body));
  const file = await hub.call("GET", `/v1/files/${id}/1/package`);
  assertEquals(file.status, 200);
  assertEquals(file.body.length, bytes.length);
});
