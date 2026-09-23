// Il2CppInterop names the game's proxy types differently under each loader.
// BepInEx keeps the original namespaces. MelonLoader adds "Il2Cpp" to every
// namespace that does not start with "Unity", whatever assembly it is in:
// Nocturne -> Il2CppNocturne, TMPro -> Il2CppTMPro, and the global namespace
// becomes Il2Cpp. Alias any other non-UnityEngine type here before using it.
#if MELONLOADER
global using Il2CppNocturne;
global using Localize = Il2CppI2.Loc.Localize;
global using NoteFieldBehaviour = Il2CppGameframe.SMRhythmPresenter.NoteFieldBehaviour;
global using TMP_Text = Il2CppTMPro.TMP_Text;
#else
global using Nocturne;
global using Localize = I2.Loc.Localize;
global using NoteFieldBehaviour = Gameframe.SMRhythmPresenter.NoteFieldBehaviour;
global using TMP_Text = TMPro.TMP_Text;
#endif
