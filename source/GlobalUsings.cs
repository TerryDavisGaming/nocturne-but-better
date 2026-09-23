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
global using TextMeshProUGUI = Il2CppTMPro.TextMeshProUGUI;
global using TextAlignmentOptions = Il2CppTMPro.TextAlignmentOptions;
global using ShapeRenderer = Il2CppShapes.ShapeRenderer;
global using TapNote = Il2CppGameframe.SMReader.TapNote;
global using TapNoteType = Il2CppGameframe.SMReader.TapNoteType;
global using TapResult = Il2CppGameframe.SMRhythmEngine.TapResult;
global using TapResultSource = Il2CppGameframe.SMRhythmEngine.TapResultSource;
global using HoldResult = Il2CppGameframe.SMRhythmEngine.HoldResult;
#else
global using Nocturne;
global using Localize = I2.Loc.Localize;
global using NoteFieldBehaviour = Gameframe.SMRhythmPresenter.NoteFieldBehaviour;
global using TMP_Text = TMPro.TMP_Text;
global using TextMeshProUGUI = TMPro.TextMeshProUGUI;
global using TextAlignmentOptions = TMPro.TextAlignmentOptions;
global using ShapeRenderer = Shapes.ShapeRenderer;
global using TapNote = Gameframe.SMReader.TapNote;
global using TapNoteType = Gameframe.SMReader.TapNoteType;
global using TapResult = Gameframe.SMRhythmEngine.TapResult;
global using TapResultSource = Gameframe.SMRhythmEngine.TapResultSource;
global using HoldResult = Gameframe.SMRhythmEngine.HoldResult;
#endif
