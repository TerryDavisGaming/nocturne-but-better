using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// A QA aid for in-game tests: with the environment variable NFS_QA_ENEMYART=1, each custom-art
/// fight logs numbered "Enemy art: SHOT-n" lines at the moments worth a picture (the idle 1.5 s
/// in, just before the first hit, the hit, the first hurt, the defeat), for the screenshot helper
/// of qa/run-enemyart.ps1. A fight with the placeholder's look logs only its idle. Nothing else
/// changes.
/// </summary>
internal static partial class EnemyArt
{
    private static readonly bool QaShotsOn = QaBuild.Env("NFS_QA_ENEMYART") == "1";
    private static int qaShot;
    private const int QaIdle = 1, QaBeforeHit = 2, QaHit = 4, QaHurt = 8, QaDefeat = 16;
    /// <summary>"Just before the hit" is logged this long before it, so the helper's picture (about 0.1 to 0.2 s later) lands on it.</summary>
    private const double QaLead = 0.15;

    private sealed partial class Fight
    {
        private float qaStart = -1;
        private int qaTaken;

        // Every frame: the idle's picture 1.5 s into the fight.
        private void QaShots()
        {
            if (!QaShotsOn) return;
            if (qaStart < 0) qaStart = Time.unscaledTime;
            if (Time.unscaledTime - qaStart >= 1.5f) QaMoment(QaIdle, "idle");
        }

        /// <summary>Logs a moment's SHOT line, once a fight.</summary>
        private void QaMoment(int moment, string what)
        {
            if (!QaShotsOn || (qaTaken & moment) != 0) return;
            qaTaken |= moment;
            ModLog.Info($"Enemy art: SHOT-{++qaShot:00} {what} ({Battle.Title}{(Set == null ? ", the placeholder's look" : "")}).");
        }
    }
}
