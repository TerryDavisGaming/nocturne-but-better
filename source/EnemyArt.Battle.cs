using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// The battle half of custom enemy art. A custom-art enemy fights like its placeholder but is
/// drawn on the game's own Mantis rig (one SpriteRenderer and an Animator): the Animator's attack
/// clip is swapped for an empty one, so the rig itself shows nothing, sounds nothing and hits
/// nothing, and the mod sets the renderer's sprite every frame from the art. The game's flash,
/// shake, stun tint and death dissolve all still work, since they only touch the renderer's
/// colour and material. The attack's hit comes from the art: the mod opens the parry window and
/// lands the hit at the art's times, with the same calls the stock animation events make.
/// </summary>
internal static partial class EnemyArt
{
    private static readonly int AttackTag = Animator.StringToHash("Attack");
    private static readonly int IdleState = Animator.StringToHash("Base Layer.Idle");
    /// <summary>The Mantis rig's shadow, and the Mantis's visible width it's made for.</summary>
    private const float ShadowBaseWidth = 43f;
    private const string CouldNotLoad = "Custom art couldn't load";

    private static Fight? fight;
    // Handed from Initialize's prefix to its postfix.
    private static IntPtr pendingView;
    private static CustomBattles.Battle? pendingBattle;
    private static ArtSet? pendingSet;
    private static string? pendingText;
    private static bool reportedHook;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Everything is looked up first, so a missing method installs nothing.
        var initialize = AccessTools.DeclaredMethod(typeof(CombatEnemyView), "Initialize", new[] { typeof(CombatEnemyModel) })
            ?? throw new MissingMethodException(typeof(CombatEnemyView).FullName, "Initialize");
        var playAttack = Method(typeof(CombatEnemyView), "PlayAttack");
        var playHit = Method(typeof(CombatEnemyView), "PlayHit");
        var attacking = Method(typeof(CombatEnemyView), "IsPlayingAttackAnimation");
        var click = Method(typeof(ArcadeMenuV2), "ArcadeSongGroup_OnClick");
        harmony.Patch(initialize, prefix: Hook(nameof(InitializePrefix)), postfix: Hook(nameof(InitializePostfix)));
        harmony.Patch(playAttack, postfix: Hook(nameof(PlayAttackPostfix)));
        harmony.Patch(playHit, postfix: Hook(nameof(PlayHitPostfix)));
        harmony.Patch(attacking, postfix: Hook(nameof(IsPlayingAttackAnimationPostfix)));
        harmony.Patch(click, postfix: Hook(nameof(ArcadeClickPostfix)));
        installed = true;
    }

    private static System.Reflection.MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(EnemyArt), name);

    /// <summary>Whether a custom-art attack is showing on this enemy (the game's attack-animation checks count it).</summary>
    internal static bool AttackShowing(CombatEnemyView? view)
    {
        var f = fight;
        return f != null && view != null && f.AttackShowing && f.ViewPointer == view.Pointer;
    }

    // ---- hooks ------------------------------------------------------------------------------------

    // Before the game loads the enemy's art: a custom-art enemy's art is loaded now, and if its
    // idle is usable the enemy is switched to the Mantis rig. That choice is made on a battle's
    // first fight and kept until the battle is rebuilt: the game releases whatever art reference
    // the enemy has when the fight's view goes, so it must never change between fights.
    private static void InitializePrefix(CombatEnemyView __instance, CombatEnemyModel model)
    {
        try
        {
            pendingView = IntPtr.Zero;
            pendingSet = null;
            pendingText = null;
            var data = model?.Data;
            var battle = data != null && data ? CustomBattles.FindEnemy(data) : null;
            if (battle == null || !battle.Package.CustomArt) return;
            pendingView = __instance.Pointer;
            pendingBattle = battle;
            var package = battle.Package;
            if (package.Art == null || battle.RigArt == null)
            {
                CantUse(battle, package.ArtUnusable ?? "the Mantis rig couldn't be set up");
                return;
            }
            if (battle.Look == CustomBattles.ArtLook.Placeholder)
            {
                CantUse(battle, battle.LookReason ?? "its idle couldn't be loaded");
                return;
            }
            var set = Collect(battle);
            if (battle.Look == CustomBattles.ArtLook.Undecided)
            {
                if (!set.IdleReady)
                {
                    string why = set.Result.Failed.TryGetValue("idle", out var failed) ? "idle: " + failed
                        : set.Done ? "its idle couldn't be loaded" : $"its idle took longer than {CollectSeconds:0} s to load";
                    battle.Look = CustomBattles.ArtLook.Placeholder;
                    battle.LookReason = why;
                    Release(set);
                    CantUse(battle, why);
                    return;
                }
                data!.addressableArtPrefab = battle.RigArt;
                data.combatPositionOffset = new Vector3((float)package.Art.OffsetX, (float)package.Art.OffsetY, 0f);
                // The rig's own animator layer and dissolve settings, not the placeholder's.
                data.overideAnimatorBaseLayerIndex = false;
                data.animatorBaseLayerIndex = 0;
                data.dissolveAffectChildRenderer = battle.RigDissolvesChildren;
                battle.Look = CustomBattles.ArtLook.Rig;
            }
            pendingSet = set;
            var idle = set.Result.Measured.TryGetValue("idle", out var m) ? m : null;
            ModLog.Info($"Enemy art for {battle.Title}: custom art on the Mantis rig (fights like {package.EnemyPlaceholder}); size {set.Result.Size:0.##}, " +
                $"offset ({package.Art.OffsetX:0.##}, {package.Art.OffsetY:0.##}), feet ({idle?.FeetX:0.##}, {idle?.FeetY:0.##}).");
            if (!set.IdleReady)
            {
                ModLog.Error($"Enemy art for {battle.Title}: the idle didn't load this time, so the first frame of another animation stands in.");
                pendingText = CouldNotLoad;
            }
        }
        catch (Exception ex) { ReportHook(ex); }
    }

    private static void CantUse(CustomBattles.Battle battle, string why)
    {
        ModLog.Error($"Enemy art for {battle.Title}: can't use the custom art ({why}); the enemy looks like {battle.Package.EnemyPlaceholder}.");
        pendingText = CouldNotLoad;
    }

    // After the game made the enemy: the rig is set up to show the art.
    private static void InitializePostfix(CombatEnemyView __instance)
    {
        try
        {
            if (pendingView == IntPtr.Zero || __instance == null || __instance.Pointer != pendingView) return;
            var set = pendingSet;
            var old = fight;
            fight = new Fight(__instance, pendingBattle!, set) { PendingText = pendingText };
            pendingView = IntPtr.Zero;
            pendingSet = null;
            pendingBattle = null;
            old?.End(keepSet: old.Set == set);
            if (set != null) fight.SetUpRig();
        }
        catch (Exception ex) { ReportHook(ex); }
    }

    private static void PlayAttackPostfix(CombatEnemyView __instance)
    {
        try
        {
            var f = fight;
            if (f != null && f.Set != null && __instance != null && __instance.Pointer == f.ViewPointer) f.StartAttack(false);
        }
        catch (Exception ex) { ReportHook(ex); }
    }

    private static void PlayHitPostfix(CombatEnemyView __instance)
    {
        try
        {
            var f = fight;
            if (f != null && f.Set != null && __instance != null && __instance.Pointer == f.ViewPointer) f.Hurt();
        }
        catch (Exception ex) { ReportHook(ex); }
    }

    // The game holds back the enemy's energy while its attack animation plays; a custom attack
    // counts for as long as it shows.
    private static void IsPlayingAttackAnimationPostfix(CombatEnemyView __instance, ref bool __result)
    {
        try
        {
            if (!__result && fight != null && AttackShowing(__instance)) __result = true;
        }
        catch (Exception ex) { ReportHook(ex); }
    }

    // Clicking a battle in the arcade starts its fight a moment later; its art starts loading now.
    private static void ArcadeClickPostfix(ArcadeSongGroup songGroup)
    {
        try
        {
            if (songGroup == null || !songGroup) return;
            var info = songGroup.CurrentSong;
            var battle = info != null ? CustomBattles.Find(info.songData) : null;
            if (battle?.Package.Art != null) Warm(battle, "the arcade");
        }
        catch (Exception ex) { ReportHook(ex); }
    }

    private static void ReportHook(Exception ex)
    {
        if (reportedHook) return;
        reportedHook = true;
        ModLog.Error("Custom enemy art hooks failed: " + ex);
    }

    private static void UpdateFight()
    {
        var f = fight;
        if (f == null) return;
        if (!f.Update())
        {
            fight = null;
            f.End(keepSet: false);
        }
    }

    // ---- one fight --------------------------------------------------------------------------------

    /// <summary>A custom-art enemy's fight: what it shows, and its attack's timeline.</summary>
    private sealed class Fight
    {
        internal readonly CombatEnemyView View;
        internal readonly IntPtr ViewPointer;
        internal readonly CustomBattles.Battle Battle;
        /// <summary>Null when the art couldn't be used (the enemy looks like its placeholder).</summary>
        internal readonly ArtSet? Set;
        internal string? PendingText;
        private int frames;
        private SpriteRenderer? renderer;
        private Animator? animator;
        private AnimatorOverrideController? overrides;
        private CombatManagerV3? manager;
        private float nextLookup;
        // The stock Empty_Attack's own hit times the attack (the runtime override failed).
        private bool stockHit;

        // What shows.
        private Clip? clip;
        private float clipTime;
        private int shown = -1;
        private bool frozen, defeated;
        private float hurtTime = -1;

        // The attack.
        private bool attacking, parryDone, hitDone, watchdogDone, speedChanged, wasAttacking;
        private float attackTime, tagOn = -1, tagOff = -1;
        private double attackLength, hitAt, parryAt;
        private bool attackArt;

        internal Fight(CombatEnemyView view, CustomBattles.Battle battle, ArtSet? set)
        {
            View = view;
            ViewPointer = view.Pointer;
            Battle = battle;
            Set = set;
        }

        internal bool Alive => View;
        internal bool AttackShowing => attacking && attackTime < attackLength;

        private Clip? Get(string name) => Set != null && Set.Clips.TryGetValue(name, out var c) && c.Usable ? c : null;

        /// <summary>The idle, or a stand-in for it: another animation's first frame.</summary>
        private Clip? Idle() => Get("idle") ?? Get("attack") ?? Get("hurt") ?? Get("defeat");

        internal void SetUpRig()
        {
            animator = View.enemyAnimator;
            renderer = View.spriteRenderer;
            if (!animator || !renderer) throw new InvalidOperationException("the rig has no animator or renderer");
            RuntimeAnimatorController? baseController = null;
            try
            {
                var controller = animator!.runtimeAnimatorController;
                var current = controller != null ? controller.TryCast<AnimatorOverrideController>() : null;
                baseController = current != null ? current.runtimeAnimatorController : controller;
                if (baseController == null) throw new InvalidOperationException("the rig has no animator controller");
                AnimationClip? empty = null;
                foreach (var c in baseController.animationClips)
                    if (c != null && c.name == "Empty_Idle_F") empty = c;
                if (empty == null) throw new InvalidOperationException("the rig has no Empty_Idle_F clip");
                // The shared Mantis controller is left alone: a copy of its base without the Mantis's
                // clips, and an attack state that plays an empty one-second clip (no sprites, no sound,
                // no hit), so the Attack tag lasts as long as the animator's speed says.
                overrides = new AnimatorOverrideController(baseController) { name = CustomBattles.RuntimePrefix + "art/rig", hideFlags = HideFlags.HideAndDontSave };
                overrides["Empty_Attack"] = empty;
                animator.runtimeAnimatorController = overrides;
            }
            catch (Exception ex)
            {
                ModLog.Error($"Enemy art: the rig's animator couldn't be changed ({ex.Message}); the stock hit times the attack instead.");
                stockHit = true;
                if (baseController != null) animator!.runtimeAnimatorController = baseController;
            }
            var idle = Idle();
            if (idle != null)
            {
                Show(idle);
                // A video idle's sprite is clear until its first frame comes; the rig's own sprite never shows.
                if (idle.Video != null) renderer!.sprite = idle.Frames[0];
            }
            var shadow = View.shadowSpriteRenderer;
            if (shadow && idle != null)
            {
                if (!Set!.Spec.Shadow) shadow.enabled = false;
                else
                {
                    float f = Mathf.Clamp(Get("idle")?.VisibleWidth / ShadowBaseWidth ?? 1f, 0.4f, 4f);
                    var t = shadow.transform;
                    t.localScale = new Vector3(t.localScale.x * f, t.localScale.y * f, t.localScale.z);
                }
            }
        }

        /// <summary>Every frame; false once the fight's enemy is gone.</summary>
        internal bool Update()
        {
            if (!View) return false;
            if (PendingText != null && ++frames > 2)
            {
                View.ShowText(PendingText);
                PendingText = null;
            }
            if (Set == null || Set.Disposed || !renderer) return true;
            bool isAttacking = View.isAttacking;
            // A backstop for an attack that started without PlayAttack.
            if (isAttacking && !wasAttacking && !attacking) StartAttack(true);
            wasAttacking = isAttacking;
            float dt = Time.deltaTime;
            if (attacking) AdvanceAttack(dt);
            if (hurtTime >= 0)
            {
                hurtTime += dt;
                var hurt = Get("hurt");
                if (hurt == null || (hurtTime >= hurt.Length && !Stunned())) hurtTime = -1;
            }
            Pick();
            Advance(dt);
            return true;
        }

        internal void End(bool keepSet)
        {
            if (clip?.Video is VideoArt v) Stop(v);
            if (overrides) Object.Destroy(overrides);
            overrides = null;
            if (!keepSet && Set != null && Set == current) Release(Set);
        }

        // ---- what shows ----

        private void Pick()
        {
            var idle = Idle();
            Clip? next;
            bool freeze = false;
            if (View.murdered)
            {
                if (!defeated)
                {
                    defeated = true;
                    ModLog.Info("Enemy art: defeat.");
                }
                // Defeat: its own animation, else the hurt played once, else the idle held.
                next = Get("defeat") ?? Get("hurt");
                if (next == null) { next = clip ?? idle; freeze = true; }
            }
            else if (AttackShowing && attackArt) next = Get("attack");
            else if (hurtTime >= 0 && Get("hurt") is Clip hurt && (hurtTime < hurt.Length || Stunned())) next = hurt;
            else next = idle;
            // Without art for a state, the idle keeps playing where it is.
            next ??= idle;
            // Another animation standing in for a missing idle holds its first frame.
            frozen = freeze || (next != null && next == idle && Get("idle") == null);
            if (next != null && next != clip) Show(next);
        }

        private bool Stunned()
        {
            var model = View.EnemyModel;
            return model != null && model.IsStunned;
        }

        /// <summary>Starts a clip from its beginning, as the game's own animation states do.</summary>
        private void Show(Clip next)
        {
            if (clip?.Video is VideoArt old && next.Video != old) Stop(old);
            clip = next;
            clipTime = 0;
            shown = -1;
            renderer!.flipX = next.Flip;
            if (next.Video is VideoArt v) Restart(v);
            else Draw(0);
        }

        private void Advance(float dt)
        {
            var c = clip;
            if (c == null) return;
            if (c.Video is VideoArt v)
            {
                FollowVideo(c, v);
                return;
            }
            if (!frozen) clipTime += dt;
            Draw(ArtTimeline.FrameAt(c.Holds, c.Length, c.Loop, clipTime));
        }

        private void Draw(int index)
        {
            var c = clip!;
            if (index == shown || index < 0 || index >= c.Frames.Length) return;
            renderer!.sprite = c.Frames[index];
            shown = index;
        }

        // ---- videos ----

        private void Restart(VideoArt v)
        {
            v.Fresh = false;
            v.LastFrame = -1;
            if (!v.Prepared || v.Failed || !v.Player) return;
            v.Player.time = 0;
            v.Player.Play();
            v.Playing = true;
        }

        private static void Stop(VideoArt v)
        {
            if (!v.Playing || !v.Player) return;
            v.Player.Pause();
            v.Playing = false;
        }

        private void FollowVideo(Clip c, VideoArt v)
        {
            if (v.Failed)
            {
                // A video that fails after the switch keeps the last frame it showed; another state
                // falls back to the idle on the next pick.
                if (c.Name != "idle") clip = null;
                return;
            }
            if (!v.Prepared || !v.Player) return;
            bool paused = GamePaused();
            // A clip that played to its end stays "playing" here, so it isn't started again.
            if (!v.Playing && !paused && !frozen)
            {
                if (v.LastFrame < 0) v.Player.time = 0;
                v.Player.Play();
                v.Playing = true;
            }
            else if (v.Playing && (paused || frozen))
            {
                v.Player.Pause();
                v.Playing = false;
            }
            long frame = v.Player.frame;
            if (frame < 0 || frame == v.LastFrame) return;
            v.LastFrame = frame;
            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.RTToTexture) != 0)
                Graphics.CopyTexture(v.Target, 0, 0, v.Copy, 0, 0);
            else
                Graphics.ConvertTexture(v.Target, v.Copy);
            if (!v.Fresh)
            {
                v.Fresh = true;
                renderer!.sprite = c.Frames[0];
                shown = 0;
            }
        }

        private bool GamePaused()
        {
            if (AudioController.IsPausedCombat) return true;
            var m = Manager();
            var conductor = m != null ? m.conductor : null;
            return conductor != null && conductor && conductor.Paused;
        }

        // ---- hurt ----

        internal void Hurt()
        {
            // The stock hurt waits for an attack to end; a custom one is left out then.
            if (View.murdered || AttackShowing || !renderer) return;
            hurtTime = 0;
            var hurt = Get("hurt");
            if (hurt != null && clip == hurt) Show(hurt);
            ModLog.Info("Enemy art: hurt.");
        }

        // ---- the attack ----

        /// <summary>Starts the attack: its art, and the times its parry window opens and its hit lands.</summary>
        internal void StartAttack(bool backstop)
        {
            var art = Get("attack");
            attackArt = art != null;
            attackLength = art?.Length ?? ArtTimeline.NoArtLength;
            hitAt = art?.Hit ?? ArtTimeline.NoArtHit;
            parryAt = art?.Parry ?? Math.Max(0, ArtTimeline.NoArtHit - ArtTimeline.ParryLead);
            attacking = true;
            attackTime = 0;
            parryDone = hitDone = watchdogDone = false;
            tagOn = tagOff = -1;
            hurtTime = -1;
            if (animator && animator!.isActiveAndEnabled)
            {
                // The attack state's clip is one second long; at this speed its Attack tag (which
                // the game's energy pause and the attack fade read) lasts the whole custom attack.
                // With the stock clip instead, its own hit (at 0.3 s) lands at the art's hit time.
                animator.speed = (float)(stockHit ? 0.3 / hitAt : 1.0 / attackLength);
                speedChanged = true;
            }
            ModLog.Info($"Enemy art: attack started (length {ArtLoadResult.Sec(attackLength)} s, parry at {ArtLoadResult.Sec(parryAt)} s, hit at {ArtLoadResult.Sec(hitAt)} s" +
                $"{(attackArt ? "" : "; no attack art")}{(backstop ? "; seen without PlayAttack" : "")}).");
        }

        private void AdvanceAttack(float dt)
        {
            attackTime += dt;
            if (!parryDone && attackTime >= parryAt)
            {
                parryDone = true;
                if (View.isAttacking)
                {
                    var simulator = Manager(now: true)?.combatSimulator;
                    if (simulator != null)
                    {
                        simulator.OpenRiposteWindow();
                        ModLog.Info($"Enemy art: parry window opened at {attackTime:0.000} s.");
                    }
                }
            }
            if (!hitDone && attackTime >= hitAt)
            {
                if (!stockHit) Hit("hit");
                else if (View.attacks >= 1 || !View.isAttacking) hitDone = true;
                else if (attackTime >= Math.Min(attackLength, ArtTimeline.MaxHit) + 0.25) Hit("the stock hit didn't come, so the hit landed");
            }
            WatchAnimator();
            // Over once the art has shown, the hit has landed and the animator is back.
            if (attackTime >= attackLength + 0.5 && hitDone && !View.isAttacking)
            {
                attacking = false;
                RestoreSpeed();
            }
        }

        private void Hit(string what)
        {
            hitDone = true;
            // Only while the game waits for it (killed mid-attack too: the game's own rigs lose
            // their hit then and wait out the full 6 s).
            if (!View.isAttacking || View.attacks >= 1) return;
            var m = Manager(now: true);
            if (m == null)
            {
                ModLog.Error("Enemy art: the fight's combat manager wasn't found, so the game times the hit out.");
                return;
            }
            m.CombatAnimationHit(false, 0, null);
            ModLog.Info($"Enemy art: {what} at {attackTime:0.000} s (attacks {View.attacks}).");
        }

        // Logs how long the animator's Attack tag lasted, and sends it back to idle if it runs long.
        private void WatchAnimator()
        {
            if (!animator || !animator!.isActiveAndEnabled || !animator.isInitialized) return;
            bool tag = animator.GetCurrentAnimatorStateInfo(0).tagHash == AttackTag;
            if (tag && tagOn < 0) tagOn = attackTime;
            if (!tag && tagOn >= 0 && tagOff < 0)
            {
                tagOff = attackTime;
                RestoreSpeed();
                ModLog.Info($"Enemy art: attack state lasted {tagOff - tagOn + Time.deltaTime:0.00} s (expected {(stockHit ? hitAt : attackLength):0.00} s).");
            }
            if (tag && !watchdogDone && attackTime > (stockHit ? hitAt : attackLength) + 0.25)
            {
                watchdogDone = true;
                animator.Play(IdleState, 0, 0f);
                RestoreSpeed();
                ModLog.Error($"Enemy art: the attack state ran past {ArtLoadResult.Sec(attackTime)} s, so the enemy's animator was sent back to idle.");
            }
        }

        private void RestoreSpeed()
        {
            if (!speedChanged) return;
            speedChanged = false;
            if (animator) animator!.speed = 1f;
        }

        /// <summary>
        /// This fight's combat manager: the game's current one, checked to be this enemy's. Looked
        /// for at most once a second (the videos ask every frame), unless <paramref name="now"/>.
        /// </summary>
        private CombatManagerV3? Manager(bool now = false)
        {
            if (manager != null && manager) return manager;
            if (!now && Time.unscaledTime < nextLookup) return null;
            nextLookup = Time.unscaledTime + 1f;
            var candidate = CombatManager.Instance?.TryCast<CombatManagerV3>();
            if (candidate != null && Owns(candidate)) return manager = candidate;
            foreach (var m in Resources.FindObjectsOfTypeAll<CombatManagerV3>())
                if (m && m.gameObject.scene.handle != 0 && Owns(m)) return manager = m;
            return null;
        }

        private bool Owns(CombatManagerV3 m)
        {
            var field = m.characterFieldView;
            var enemy = field != null && field ? field.enemyView : null;
            return enemy != null && enemy.Pointer == ViewPointer;
        }
    }
}
