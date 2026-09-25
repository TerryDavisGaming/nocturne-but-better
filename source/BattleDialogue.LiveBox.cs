using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace NocturneFlatScroll;

/// <summary>
/// A live line's box is see-through. While a line during the song shows (the game's box raised
/// over the far end of the note track), the box's background images, the translucent panel and
/// its border, are drawn at <see cref="LiveBoxAlpha"/> of the alpha the game gives them, so the
/// notes behind stay readable. The text and the portraits stay solid, and the colours stay the
/// game's. The game may set the images' colours again for a line, so it's done every frame. The
/// game's own alpha comes back once the box has closed after its line, as soon as a block of lines
/// (before the fight, a break, after the battle) takes the box, and at the cleanup, so those lines
/// keep the opaque box. Nothing here can stop the lines: a failure logs once and leaves the box be.
/// </summary>
internal static partial class BattleDialogue
{
    /// <summary>A live line's box background, as a share of the game's own alpha. The battle creator's preview uses it too.</summary>
    internal const float LiveBoxAlpha = 0.72f;
    // An alpha read back within this of one set here is taken as the one set here.
    private const float AlphaSame = 0.0005f;

    /// <summary>
    /// A box's background images. First the style's own references: the Normal box's translucent
    /// panel and border on each side (DialogueStyleNormal.leftSide and rightSide), or the Narrator
    /// box's (DialogueStyleNarrator.translucentImage and borderImage). If those can't be read,
    /// every enabled Image under the style that isn't in the text's part of the box, isn't a
    /// portrait and isn't part of the "next" arrow. <paramref name="how"/> says which way.
    /// </summary>
    private static List<Graphic> BoxBackground(DialogueStyleBehaviour style, out string how)
    {
        var found = new List<Graphic>();
        void Add(Graphic? g)
        {
            if (g != null && g && !found.Any(x => x.Pointer == g.Pointer)) found.Add(g);
        }
        how = "the box's own";
        var normal = style.TryCast<DialogueStyleNormal>();
        var narrator = normal == null ? style.TryCast<DialogueStyleNarrator>() : null;
        try
        {
            if (normal != null)
            {
                foreach (var side in new[] { normal.leftSide, normal.rightSide })
                {
                    if (side == null) continue;
                    Add(side.translucentImage);
                    Add(side.borderImage);
                }
            }
            else if (narrator != null)
            {
                Add(narrator.translucentImage);
                Add(narrator.borderImage);
            }
        }
        catch (Exception) { found.Clear(); }
        if (found.Count > 0) return found;

        how = "looked for under the box";
        TMP_Text? text = normal != null ? normal.dialogueText : narrator?.text;
        var root = style.transform;
        // The text's part of the box: the nearest group above the text (the Normal box's "Text", the Narrator's "Line Text").
        Transform? textPart = text != null && text ? text.transform : null;
        for (var t = textPart; t != null && t.Pointer != root.Pointer; t = t.parent)
        {
            var group = t.GetComponent<CanvasGroup>();
            if (group != null && group)
            {
                textPart = t;
                break;
            }
        }
        foreach (var image in style.GetComponentsInChildren<Image>(true))
        {
            if (image == null || !image || !image.enabled) continue;
            var t = image.transform;
            if (textPart != null && t.IsChildOf(textPart)) continue;
            if (PortraitOrArrow(t, root)) continue;
            Add(image);
        }
        return found;
    }

    // Whether an image is a portrait (the Normal box's Image_L_Character1 and the like) or part of
    // the "next" arrow (which has an Animator), up to the style.
    private static bool PortraitOrArrow(Transform t, Transform root)
    {
        for (var at = t; at != null && at.Pointer != root.Pointer; at = at.parent)
        {
            if ((at.name ?? "").IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var animator = at.GetComponent<Animator>();
            if (animator != null && animator) return true;
        }
        return false;
    }

    private static string BoxName(DialogueStyleBehaviour style) => style.TryCast<DialogueStyleNarrator>() != null ? "Narrator" : "Normal";

    /// <summary>For QA: a box's background alphas as they are now ("0.72", or "0.72/1.00" when they differ).</summary>
    private static string BoxAlphaText(DialogueStyleBehaviour style)
    {
        var images = BoxBackground(style, out _);
        if (images.Count == 0) return "none found";
        return string.Join("/", images.Select(i => i.color.a.ToString("0.00", CultureInfo.InvariantCulture)).Distinct());
    }

    private sealed partial class Director
    {
        // The boxes made see-through: the live line's, and one still closing after its line.
        private readonly List<SeeThroughBox> seeThrough = new();
        // Boxes with no background images to find (said once).
        private readonly HashSet<IntPtr> seeThroughMissing = new();
        private bool seeThroughSaid, seeThroughFailed;

        private sealed class SeeThroughBox
        {
            internal DialogueStyleBehaviour Style = null!;
            internal IntPtr Pointer;
            internal readonly List<SeeThroughImage> Images = new();
            /// <summary>When its live line went (the box closing); -1 while it shows.</summary>
            internal float ClosingAt = -1;
        }

        private sealed class SeeThroughImage
        {
            internal Graphic Image = null!;
            /// <summary>The alpha the game gave it, and the one set here (NaN until then).</summary>
            internal float Original, Applied = float.NaN;
        }

        /// <summary>
        /// Every frame: the live line's box see-through, and a box whose line has gone back to the
        /// game's alpha once it has closed (or after SettleLimit).
        /// </summary>
        private void UpdateSeeThrough()
        {
            if (seeThroughFailed) return;
            try
            {
                float now = Time.unscaledTime;
                var live = liveStyle;
                var showing = live != null && live ? SeeThroughFor(live) : null;
                for (int i = seeThrough.Count - 1; i >= 0; i--)
                {
                    var box = seeThrough[i];
                    if (box == showing)
                    {
                        box.ClosingAt = -1;
                        Apply(box);
                        continue;
                    }
                    if (box.ClosingAt < 0) box.ClosingAt = now;
                    bool closing = box.Style != null && box.Style && box.Style.IsAnimating && now - box.ClosingAt < SettleLimit;
                    if (closing)
                    {
                        Apply(box);
                        continue;
                    }
                    seeThrough.RemoveAt(i);
                    Restore(box);
                    QaDump("the live box is back");
                }
            }
            catch (Exception ex)
            {
                seeThroughFailed = true;
                ModLog.Error($"Battle dialogue: the see-through live box failed: {ex.Message}. Live lines show the game's box as it is.");
                EndSeeThrough(null);
            }
        }

        // The see-through state of a style's box, made the first time a live line uses it; null
        // when it has no background images to find.
        private SeeThroughBox? SeeThroughFor(DialogueStyleBehaviour style)
        {
            var box = seeThrough.FirstOrDefault(b => b.Pointer == style.Pointer);
            if (box != null || seeThroughMissing.Contains(style.Pointer)) return box;
            var images = BoxBackground(style, out string how);
            if (images.Count == 0)
            {
                seeThroughMissing.Add(style.Pointer);
                ModLog.Info($"Battle dialogue: no background images found in the {BoxName(style)} box, so its live lines show it as it is.");
                return null;
            }
            box = new SeeThroughBox { Style = style, Pointer = style.Pointer };
            foreach (var image in images) box.Images.Add(new SeeThroughImage { Image = image });
            seeThrough.Add(box);
            if (!seeThroughSaid)
            {
                seeThroughSaid = true;
                ModLog.Info($"Battle dialogue: live box see-through ({Math.Round(LiveBoxAlpha * 100).ToString(CultureInfo.InvariantCulture)}%).");
            }
            if (QaOn) ModLog.Info($"Battle dialogue QA: the {BoxName(style)} box's background is {string.Join(", ", images.Select(i => i.name))} ({how}).");
            return box;
        }

        // Each image at LiveBoxAlpha of the game's alpha, with the game's colour. An alpha the game
        // set since the last frame (for a new line, or a fade) is the one to come back to.
        private static void Apply(SeeThroughBox box)
        {
            foreach (var s in box.Images)
            {
                if (s.Image == null || !s.Image) continue;
                var c = s.Image.color;
                if (float.IsNaN(s.Applied) || Mathf.Abs(c.a - s.Applied) > AlphaSame) s.Original = c.a;
                s.Applied = s.Original * LiveBoxAlpha;
                if (Mathf.Abs(c.a - s.Applied) > AlphaSame) s.Image.color = new Color(c.r, c.g, c.b, s.Applied);
            }
        }

        // The game's alpha back, unless the game has set one of its own since.
        private static void Restore(SeeThroughBox box)
        {
            foreach (var s in box.Images)
            {
                if (float.IsNaN(s.Applied) || s.Image == null || !s.Image) continue;
                var c = s.Image.color;
                if (Mathf.Abs(c.a - s.Applied) <= AlphaSame) s.Image.color = new Color(c.r, c.g, c.b, s.Original);
            }
        }

        /// <summary>
        /// Every see-through box back to the game's alpha now: a block of lines is about to show in
        /// the box, or the battle's dialogue ends. <paramref name="qa"/> names the QA dump after it.
        /// </summary>
        internal void EndSeeThrough(string? qa)
        {
            if (seeThrough.Count == 0) return;
            var boxes = seeThrough.ToList();
            seeThrough.Clear();
            foreach (var box in boxes)
            {
                try { Restore(box); }
                catch (Exception ex) { ModLog.Error($"Battle dialogue: putting a live box's own alpha back failed: {ex.Message}"); }
            }
            if (qa != null) QaDump(qa);
        }
    }
}
