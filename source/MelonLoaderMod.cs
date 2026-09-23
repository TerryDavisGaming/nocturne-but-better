#if MELONLOADER
using MelonLoader;
using NocturneFlatScroll;

[assembly: MelonInfo(typeof(FlatScrollMod), ModInfo.Name, ModInfo.Version, "TerryDavisGaming")]
[assembly: MelonGame("PracyStudios", "Nocturne")]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
// Built and tested against MelonLoader 0.7.3; the flag makes this a minimum, not an exact match.
[assembly: VerifyLoaderVersion(0, 7, 3, true)]
// Patches are applied explicitly in OptionsMenuIntegration.Install.
[assembly: HarmonyDontPatchAll]

namespace NocturneFlatScroll;

public sealed class FlatScrollMod : MelonMod
{
    public override void OnInitializeMelon()
    {
        ModLog.Initialize(message => LoggerInstance.Msg(message), message => LoggerInstance.Error(message));
        OptionsMenuIntegration.Install(HarmonyInstance);
    }

    public override void OnLateInitializeMelon()
    {
        // Unity has run its first Start messages, so scene objects can be searched.
        OptionsMenuIntegration.AttachToExistingMenus();
        ModLog.Info("Flat scrolling loaded; select a layout in Options > Gameplay > Note scrolling.");
    }

    // MelonLoader calls these from Unity's Update and LateUpdate; LateUpdate runs after
    // the Animator update, so absolute layout writes are not overwritten that frame.
    public override void OnUpdate() => LayoutDriver.Update();
    public override void OnLateUpdate() => LayoutDriver.LateUpdate();
}
#endif
