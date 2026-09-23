using UnityEngine;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Makes enemy attacks see-through, by the "Enemy attack opacity" setting. Most attacks are
/// frames of the enemy's own sprite (the firefly's beam is drawn in its attack frames), so the
/// whole enemy fades while an attack animation plays, along with its sidekicks. Effects that
/// only exist as attacks, the objects a fight spawns for them and the vines over the lanes,
/// fade whenever they show. Only alpha changes, so the game's own tints still show, and at
/// 100% nothing is written.
/// </summary>
internal static class EnemyAttackFade
{
    // Time to fade fully in or out, so an attack doesn't pop.
    private const float RampSeconds = 0.12f;
    private const float ScanInterval = 1f;
    // This enemy shader ignores the sprite's alpha, so those sprites borrow the enemy's other
    // material while they fade.
    private const string AlphaBlindShader = "Shader Graphs/DissolveSpriteV2";
    private static readonly int AttackTag = Animator.StringToHash("Attack");
    private static readonly Dictionary<int, Battle> Battles = new();
    private static readonly List<int> Dead = new();
    private static Material? fadeMaterial;
    private static float nextPrune;

    /// <param name="active">False once the fight's view is hidden: everything is put back.</param>
    internal static void LateUpdate(CombatNoteFieldView view, bool active)
    {
        Prune();
        int id = view.GetInstanceID();
        Battles.TryGetValue(id, out var battle);
        if (!active)
        {
            if (battle == null) return;
            battle.Restore();
            Battles.Remove(id);
            return;
        }
        float opacity = SettingsState.EnemyAttackOpacity.Factor;
        if (battle == null)
        {
            if (opacity >= 1f) return;
            battle = new Battle(view);
            Battles[id] = battle;
        }
        // A battle is dropped once it is back at 100% with everything put back.
        if (!battle.Update(opacity)) Battles.Remove(id);
    }

    private static void Prune()
    {
        if (Time.unscaledTime < nextPrune) return;
        nextPrune = Time.unscaledTime + ScanInterval;
        Dead.Clear();
        foreach (var pair in Battles)
            if (!pair.Value.IsAlive) Dead.Add(pair.Key);
        foreach (int id in Dead)
        {
            Battles[id].Restore();
            Battles.Remove(id);
        }
    }

    /// <summary>
    /// Whether the enemy is attacking: the game's own flag covers the start of an attack, and
    /// the "Attack" tag on its animation states covers the rest.
    /// </summary>
    private static bool Attacking(CombatEnemyView enemy)
    {
        if (enemy.isAttacking) return true;
        var animator = enemy.enemyAnimator;
        if (!animator || !animator.isActiveAndEnabled || !animator.isInitialized) return false;
        int layer = 0;
        var model = enemy.EnemyModel;
        var data = model != null ? model.Data : null;
        if (data && data!.overideAnimatorBaseLayerIndex) layer = data.animatorBaseLayerIndex;
        if (layer < 0 || layer >= animator.layerCount) return false;
        if (animator.GetCurrentAnimatorStateInfo(layer).tagHash == AttackTag) return true;
        return animator.IsInTransition(layer) && animator.GetNextAnimatorStateInfo(layer).tagHash == AttackTag;
    }

    private sealed class Battle
    {
        private readonly CombatNoteFieldView _view;
        private CombatCharacterFieldView? _characters;
        private CombatEnemyView? _enemy;
        // The enemy and its sidekicks fade during attacks; spawned attack objects always.
        private readonly Dictionary<int, FadedSprite> _enemySprites = new();
        private readonly Dictionary<int, FadedSprite> _attackSprites = new();
        private readonly HashSet<int> _seen = new();
        private Transform? _vines;
        private CanvasGroup? _vinesGroup;
        private float _ramp = 1f;
        private float _nextScan;
        // What the last scan saw, so a new attack object or enemy is picked up the same frame.
        private int _spawnCount = -1, _artCount = -1, _sidekickCount = -1;

        internal Battle(CombatNoteFieldView view) => _view = view;

        internal bool IsAlive => _view;

        /// <returns>Whether anything is still faded or fading.</returns>
        internal bool Update(float opacity)
        {
            float now = Time.unscaledTime;
            if (now >= _nextScan || Changed())
            {
                _nextScan = now + ScanInterval;
                Scan();
            }
            var enemy = _enemy;
            // A defeated enemy is left to the game's own death effect.
            bool alive = enemy && !enemy!.murdered;
            float target = alive && opacity < 1f && Attacking(enemy!) ? opacity : 1f;
            _ramp = alive ? Mathf.MoveTowards(_ramp, target, Time.unscaledDeltaTime / RampSeconds) : 1f;
            bool faded = Apply(_enemySprites, _ramp, dying: enemy && !alive);
            faded |= Apply(_attackSprites, opacity);
            faded |= ApplyVines(opacity);
            return faded || opacity < 1f || _ramp < 1f;
        }

        internal void Restore()
        {
            _ramp = 1f;
            Apply(_enemySprites, 1f);
            Apply(_attackSprites, 1f);
            ApplyVines(1f);
        }

        /// <summary>Whether the enemy, its art, its sidekicks or the attack objects changed since the last scan.</summary>
        private bool Changed()
        {
            if (!_characters) return false;
            var enemy = _characters!.EnemyView;
            if (enemy != _enemy) return true;
            var spawned = _characters.spawnedPrefabParent;
            if ((spawned ? spawned!.childCount : 0) != _spawnCount) return true;
            if (!enemy) return false;
            var art = enemy!.enemyParent;
            if ((art ? art!.transform.childCount : 0) != _artCount) return true;
            var sidekicks = enemy.sidekicks;
            return (sidekicks != null ? sidekicks.Count : 0) != _sidekickCount;
        }

        /// <summary>
        /// Finds the enemy's sprites and the attack objects: once a second, and at once when
        /// something is added or removed.
        /// </summary>
        private void Scan()
        {
            if (!_characters)
            {
                var root = _view.transform.parent;
                var found = root ? root.Find("CharacterFieldView") : null;
                _characters = found ? found!.GetComponent<CombatCharacterFieldView>() : null;
                if (!_characters && root) _characters = root!.GetComponentInChildren<CombatCharacterFieldView>(true);
            }
            if (!_vines) _vines = _view.transform.Find("FieldPivot/Canvas/Vines");

            var enemy = _characters ? _characters!.EnemyView : null;
            if (enemy != _enemy)
            {
                // A new fight's enemy: the old one's sprites are gone or no longer ours to fade.
                Apply(_enemySprites, 1f);
                _enemySprites.Clear();
                _enemy = enemy;
                _ramp = 1f;
            }

            _seen.Clear();
            _artCount = _sidekickCount = 0;
            if (enemy)
            {
                // Only the enemy's art: hits landing on the enemy are children of the enemy
                // itself, next to EnemyParent, and are left alone.
                var art = enemy!.enemyParent;
                if (art)
                {
                    _artCount = art!.transform.childCount;
                    Track(_enemySprites, art.GetComponentsInChildren<SpriteRenderer>(true));
                }
                var sidekicks = enemy.sidekicks;
                if (sidekicks != null)
                {
                    _sidekickCount = sidekicks.Count;
                    foreach (var sidekick in sidekicks)
                    {
                        if (!sidekick) continue;
                        Track(_enemySprites, sidekick.SpriteRenderer);
                        Track(_enemySprites, sidekick.ShadowSpriteRenderer);
                    }
                }
            }
            Forget(_enemySprites);

            _seen.Clear();
            var spawned = _characters ? _characters!.spawnedPrefabParent : null;
            _spawnCount = spawned ? spawned!.childCount : 0;
            if (spawned)
            {
                for (int i = 0; i < spawned!.childCount; i++)
                {
                    var child = spawned.GetChild(i);
                    // Helpers are the player's allies; sidekicks fade with the enemy above.
                    if (child.GetComponentInChildren<CombatHelper>(true) ||
                        child.GetComponentInChildren<CombatSidekick>(true)) continue;
                    Track(_attackSprites, child.GetComponentsInChildren<SpriteRenderer>(true));
                }
            }
            Forget(_attackSprites);
        }

        private void Track(Dictionary<int, FadedSprite> sprites, IEnumerable<SpriteRenderer> renderers)
        {
            foreach (var renderer in renderers) Track(sprites, renderer);
        }

        private void Track(Dictionary<int, FadedSprite> sprites, SpriteRenderer? renderer)
        {
            if (!renderer) return;
            int id = renderer!.GetInstanceID();
            _seen.Add(id);
            if (!sprites.ContainsKey(id)) sprites[id] = new FadedSprite(renderer);
        }

        /// <summary>Puts back and drops sprites that are no longer part of the enemy or an attack.</summary>
        private void Forget(Dictionary<int, FadedSprite> sprites)
        {
            Dead.Clear();
            foreach (var pair in sprites)
                if (!_seen.Contains(pair.Key)) Dead.Add(pair.Key);
            foreach (int id in Dead)
            {
                sprites[id].Apply(1f, null, false);
                sprites.Remove(id);
            }
        }

        /// <param name="dying">The enemy was just defeated: its material is left as it is.</param>
        /// <returns>Whether any of the sprites is faded.</returns>
        private bool Apply(Dictionary<int, FadedSprite> sprites, float factor, bool dying = false)
        {
            bool faded = false;
            List<int>? dead = null;
            foreach (var pair in sprites)
            {
                if (!pair.Value.IsAlive)
                {
                    (dead ??= new List<int>()).Add(pair.Key);
                    continue;
                }
                faded |= pair.Value.Apply(factor, FadeMaterial, dying);
            }
            if (dead != null) foreach (int id in dead) sprites.Remove(id);
            return faded;
        }

        /// <summary>The enemy's alpha-aware material, copied once, for sprites whose own shader ignores alpha.</summary>
        private Material? FadeMaterial
        {
            get
            {
                if (fadeMaterial) return fadeMaterial;
                var source = _enemy ? _enemy!.enemySpriteMaterialWithOcclusion : null;
                if (!source) return null;
                fadeMaterial = new Material(source) { name = "NocturneFlatScroll.AttackFade", hideFlags = HideFlags.DontUnloadUnusedAsset };
                return fadeMaterial;
            }
        }

        /// <summary>Vines are UI images; a group of the mod's own fades them together.</summary>
        private bool ApplyVines(float opacity)
        {
            if (opacity >= 0.999f)
            {
                if (_vinesGroup) Object.Destroy(_vinesGroup);
                _vinesGroup = null;
                return false;
            }
            if (!_vines) return false;
            if (!_vinesGroup)
            {
                // Leave a group the game added itself alone.
                if (_vines!.GetComponent<CanvasGroup>()) return false;
                _vinesGroup = _vines.gameObject.AddComponent<CanvasGroup>();
            }
            if (!Mathf.Approximately(_vinesGroup!.alpha, opacity)) _vinesGroup.alpha = opacity;
            return true;
        }
    }

    /// <summary>One sprite's alpha before the fade, kept up to date with the game's own changes.</summary>
    private sealed class FadedSprite
    {
        private readonly SpriteRenderer _renderer;
        private float _alpha;
        private Color _written;
        private bool _faded;
        private Material? _checked;
        private bool _alphaBlind;
        private Material? _original;

        internal FadedSprite(SpriteRenderer renderer) => _renderer = renderer;

        internal bool IsAlive => _renderer;

        /// <param name="swapMaterial">The material for sprites whose own shader ignores alpha.</param>
        /// <param name="dying">
        /// The enemy was just defeated. Its death effect may already be playing on the material the
        /// sprite has now, so a borrowed material is left in place.
        /// </param>
        /// <returns>Whether the sprite is faded after this call.</returns>
        internal bool Apply(float factor, Material? swapMaterial, bool dying)
        {
            if (!_renderer) return false;
            var color = _renderer.color;
            if (factor >= 0.999f)
            {
                if (!_faded) return false;
                // If the game set its own colour since, that colour stays as it is.
                if (color == _written)
                {
                    color.a = _alpha;
                    _renderer.color = color;
                }
                RestoreMaterial(dying);
                _faded = false;
                return false;
            }
            // The game sets colours for stuns, hits and deaths; follow its alpha from then on.
            if (!_faded || color != _written) _alpha = color.a;
            _faded = true;
            color.a = _alpha * factor;
            if (color != _renderer.color) _renderer.color = color;
            _written = color;
            SwapMaterial(swapMaterial);
            return true;
        }

        private void SwapMaterial(Material? swapMaterial)
        {
            var current = _renderer.sharedMaterial;
            if (_original)
            {
                // A hit flash gives the sprite its own copy of the material it has, which can be
                // the borrowed one; that copy is still borrowed.
                if (IsFadeMaterial(current)) return;
                // The game changed the material itself; that one is what gets put back.
                _original = null;
            }
            if (!current || !swapMaterial) return;
            if (current != _checked)
            {
                _checked = current;
                var shader = current!.shader;
                _alphaBlind = shader && shader.name == AlphaBlindShader;
            }
            if (!_alphaBlind) return;
            _original = current;
            _renderer.sharedMaterial = swapMaterial;
        }

        private void RestoreMaterial(bool dying)
        {
            if (!_original) return;
            if (!dying && IsFadeMaterial(_renderer.sharedMaterial)) _renderer.sharedMaterial = _original;
            _original = null;
        }

        /// <summary>The borrowed material, or a copy the game made of it for a hit flash.</summary>
        private static bool IsFadeMaterial(Material? material)
        {
            if (!material || !fadeMaterial) return false;
            if (material == fadeMaterial) return true;
            return material!.shader == fadeMaterial!.shader && material.name.StartsWith(fadeMaterial.name, StringComparison.Ordinal);
        }
    }
}
