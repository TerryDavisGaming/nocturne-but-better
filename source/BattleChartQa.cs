using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// QA ONLY, until the battle creator (track F) opens the chart editor for battles: remove this
/// file, and its call in ChartEditor.Update, when the creator is merged.
/// With the environment variable NFS_QA_BATTLECHART set to a custom battle's folder (or, when the
/// game is started through Steam and doesn't see the variable, the folder written on the first
/// line of NocturneButBetter\qa-battlechart.txt in the game's data folder), the chart editor
/// opens on that battle's chart once, when the title screen shows. NFS_QA_BATTLECHART_SLOT
/// (0 Beginner to 5 Zen) picks the difficulty it opens on. The mod only reads these.
/// </summary>
internal static class BattleChartQa
{
    internal const string Variable = "NFS_QA_BATTLECHART";
    internal const string SlotVariable = "NFS_QA_BATTLECHART_SLOT";
    private const string FileName = "qa-battlechart.txt";

    private static bool done;
    private static float nextCheck;

    internal static void Update()
    {
        if (done || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 1f;
        try
        {
            string? folder = Folder();
            if (folder == null) { done = true; return; }
            if (!TitleShowing() || EditorOverlay.IsOpen) return;
            done = true;
            int slot = int.TryParse(Environment.GetEnvironmentVariable(SlotVariable), out int s) ? s : -1;
            ModLog.Info($"QA: opening the chart editor on the custom battle in {folder} (slot {slot}).");
            var target = BattleChartTarget.FromFolder(folder, () => ModLog.Info("QA: the battle chart editor closed; its Closed callback ran."));
            ChartEditor.OpenBattle(target, slot);
        }
        catch (Exception ex)
        {
            done = true;
            ModLog.Error("QA: opening the chart editor on a custom battle failed: " + ex);
        }
    }

    private static string? Folder()
    {
        string? folder = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            string file = Path.Combine(Application.persistentDataPath, "NocturneButBetter", FileName);
            if (File.Exists(file)) folder = File.ReadLines(file).FirstOrDefault();
        }
        folder = folder?.Trim().Trim('"');
        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    private static bool TitleShowing()
    {
        foreach (var menu in Resources.FindObjectsOfTypeAll<MainMenu>())
            if (menu && menu.gameObject.activeInHierarchy) return true;
        return false;
    }
}
