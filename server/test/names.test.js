import { assert, assertEquals } from "./assert.js";
import { LIMITS, cleanLine, cleanName, cleanText, indexText, matchString, normalizeSearch, titleKey } from "../src/names.js";
import { SEARCH_TEXTS } from "./make-fixtures.js";

Deno.test("text: control, bidi, invisible and tag characters are removed", () => {
  const nasty = "Moon\u202Elit\u2066 \u200BDuel\u0000\u0007\u00AD\u034F\u061C\u115F\u1160\u3164\uFFA0\uFEFF\u{E0041}\u2060\u180E";
  assertEquals(cleanLine(nasty, 100), "Moonlit Duel");
  assertEquals(cleanLine("  a \t\n  b  ", 100), "a b");
  assertEquals(cleanLine(42, 100), null);
});

Deno.test("text: NFC, and caps count code points", () => {
  assertEquals(cleanLine("Cafe\u0301", 100), "Caf\u00E9");
  assertEquals(cleanLine("x".repeat(150), LIMITS.title).length, 100);
  assertEquals([...cleanLine("\u{1F600}".repeat(40), LIMITS.name)].length, 32);
});

Deno.test("text: descriptions keep single line breaks", () => {
  assertEquals(cleanText("line one\r\n\r\n\r\n\r\nline  two\n", 1000), "line one\n\nline two");
  assertEquals(cleanText("a\u2028b", 1000), "a b");
});

Deno.test("text: rich-text attack strings pass through as plain text (the mod shows them with rich text off)", () => {
  for (const s of ["</no</noparse>parse>", "</NOPARSE>", "<size=500>BIG", "<color=#0000>x", "<sprite=0>"]) assertEquals(cleanLine(s, 100), s);
});

Deno.test("names: empty, punctuation-only, invisible-only and reserved names are refused", () => {
  for (const bad of ["", "   ", "!!!", "\u3164\u3164", "\u115F", "Admin", "OWNER", "mod-erator", "h.u.b", 12, null]) assertEquals(cleanName(bad), null, String(bad));
  assertEquals(cleanName("  Bryce  "), "Bryce");
  assertEquals(cleanName("\u6708\u5149"), "\u6708\u5149");
  assertEquals(cleanName("hubert"), "hubert");
});

Deno.test("title sort key: NFKD, marks removed, lower case", () => {
  assertEquals(titleKey("\u00C9toile \uFF21"), "etoile a");
});

Deno.test("search: normalized form, 4 words, 2+ characters, prefix only on a last word of 3+", () => {
  assertEquals(normalizeSearch("  Moonlit   DUEL!! "), "moonlit duel");
  assertEquals(normalizeSearch("a moon b"), "moon");
  assertEquals(normalizeSearch("one two three four five"), "one two three four");
  assertEquals(normalizeSearch("\u00E9toile"), "\u00E9toile");
  assertEquals(matchString("moonlit duel"), '"moonlit" "duel"*');
  assertEquals(matchString("mo"), '"mo"');
  assertEquals(matchString("moo"), '"moo"*');
});

Deno.test("search: capitals outside A-Z use the full Unicode lower-case mapping; lengths count code points", () => {
  assertEquals(normalizeSearch("\u00C9milie"), "\u00E9milie");
  assertEquals(normalizeSearch("\u041D\u043E\u0447\u044C"), "\u043D\u043E\u0447\u044C");
  assertEquals(normalizeSearch("\u039F\u0394\u039F\u03A3 Remix"), "\u03BF\u03B4\u03BF\u03C2 remix"); // a final sigma, as JavaScript lower-cases it
  assertEquals(normalizeSearch("\u0130stanbul"), "i\u0307stanbul"); // dotted capital I: i + a combining dot
  assertEquals(normalizeSearch("E\u0301toile"), "\u00E9toile"); // NFC first
  assertEquals(normalizeSearch("zero\u200Bwidth"), "zerowidth");
  assertEquals(normalizeSearch("\u{1D400}\u{1D401}"), "\u{1D400}\u{1D401}"); // 2 code points (4 UTF-16 units): kept
  assertEquals([...normalizeSearch("\u{1D400}".repeat(40))].length, 32);
  // Every shared search fixture normalizes to something the server accepts again unchanged.
  for (const q of SEARCH_TEXTS) {
    const n = normalizeSearch(q);
    assertEquals(normalizeSearch(n), n, q);
  }
});

Deno.test("search: FTS operators and quotes can't get through", () => {
  for (const q of ['"', "*", "NEAR(a b)", "title:moon", "moon OR duel", '"unbalanced', "a AND b NOT c", "^moon", "moon)"]) {
    const n = normalizeSearch(q);
    const m = matchString(n);
    assert(!/[():^]/.test(m), m);
    for (const word of m.split(" ").filter(Boolean)) assert(/^"[\p{L}\p{N}\p{M}]+"\*?$/u.test(word), word);
  }
});

Deno.test("index text splits words the way searches do", () => {
  assertEquals(indexText("Love\u2665Song (Remix)"), "love song remix");
});
