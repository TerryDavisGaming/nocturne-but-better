// Tiny assertions (no std library download): equal by JSON shape, truthy, throws.

export class AssertionError extends Error {}

function canon(v) {
  if (v instanceof Uint8Array) return { bytes: [...v] };
  if (Array.isArray(v)) return v.map(canon);
  if (v && typeof v === "object") {
    const out = {};
    for (const k of Object.keys(v).sort()) out[k] = canon(v[k]);
    return out;
  }
  return v;
}

export function assertEquals(actual, expected, message) {
  const a = JSON.stringify(canon(actual)), b = JSON.stringify(canon(expected));
  if (a !== b) throw new AssertionError(`${message ? message + ": " : ""}expected ${b.slice(0, 600)}, got ${a.slice(0, 600)}`);
}

export function assert(value, message) {
  if (!value) throw new AssertionError(message || "assertion failed");
}

export function assertMatch(text, re, message) {
  if (!re.test(String(text))) throw new AssertionError(`${message ? message + ": " : ""}${JSON.stringify(String(text).slice(0, 300))} doesn't match ${re}`);
}

export async function assertRejects(fn, re) {
  try {
    await fn();
  } catch (e) {
    if (re && !re.test(String(e && e.message))) throw new AssertionError(`threw ${e && e.message}, expected ${re}`);
    return e;
  }
  throw new AssertionError("expected an error");
}

export function assertThrows(fn, re) {
  try {
    fn();
  } catch (e) {
    if (re && !re.test(String(e && e.message))) throw new AssertionError(`threw ${e && e.message}, expected ${re}`);
    return e;
  }
  throw new AssertionError("expected an error");
}
