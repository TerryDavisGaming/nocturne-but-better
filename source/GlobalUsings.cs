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
global using TMP_FontAsset = Il2CppTMPro.TMP_FontAsset;
global using TextMeshProUGUI = Il2CppTMPro.TextMeshProUGUI;
global using TextAlignmentOptions = Il2CppTMPro.TextAlignmentOptions;
global using TextOverflowModes = Il2CppTMPro.TextOverflowModes;
global using PanelStackPresenter = Il2CppCRL.Gui.PanelStackPresenter;
global using SaveLoadManager = Il2CppCRL.SaveLoad.SaveLoadManager;
global using ISongPosition = Il2CppGameframe.SMRhythmEngine.ISongPosition;
global using BaseNoteMover = Il2CppGameframe.SMRhythmPresenter.BaseNoteMover;
global using ShapeRenderer = Il2CppShapes.ShapeRenderer;
global using TapNote = Il2CppGameframe.SMReader.TapNote;
global using TapNoteType = Il2CppGameframe.SMReader.TapNoteType;
global using TapResult = Il2CppGameframe.SMRhythmEngine.TapResult;
global using TapResultSource = Il2CppGameframe.SMRhythmEngine.TapResultSource;
global using HoldResult = Il2CppGameframe.SMRhythmEngine.HoldResult;
global using AkSoundEngine = Il2Cpp.AkSoundEngine;
global using AkAudioListener = Il2Cpp.AkAudioListener;
global using AkBankManager = Il2Cpp.AkBankManager;
global using SmSongData = Il2CppGameframe.SMReader.SongData;
global using NotesLoaderSM = Il2CppGameframe.SMReader.NotesLoaderSM;
global using LocalizedString = Il2CppI2.Loc.LocalizedString;
global using WwiseEvent = Il2CppAK.Wwise.Event;
global using WwiseSwitch = Il2CppAK.Wwise.Switch;
global using WwiseBank = Il2CppAK.Wwise.Bank;
#else
global using Nocturne;
global using Localize = I2.Loc.Localize;
global using NoteFieldBehaviour = Gameframe.SMRhythmPresenter.NoteFieldBehaviour;
global using TMP_Text = TMPro.TMP_Text;
global using TMP_FontAsset = TMPro.TMP_FontAsset;
global using TextMeshProUGUI = TMPro.TextMeshProUGUI;
global using TextAlignmentOptions = TMPro.TextAlignmentOptions;
global using TextOverflowModes = TMPro.TextOverflowModes;
global using PanelStackPresenter = CRL.Gui.PanelStackPresenter;
global using SaveLoadManager = CRL.SaveLoad.SaveLoadManager;
global using ISongPosition = Gameframe.SMRhythmEngine.ISongPosition;
global using BaseNoteMover = Gameframe.SMRhythmPresenter.BaseNoteMover;
global using ShapeRenderer = Shapes.ShapeRenderer;
global using TapNote = Gameframe.SMReader.TapNote;
global using TapNoteType = Gameframe.SMReader.TapNoteType;
global using TapResult = Gameframe.SMRhythmEngine.TapResult;
global using TapResultSource = Gameframe.SMRhythmEngine.TapResultSource;
global using HoldResult = Gameframe.SMRhythmEngine.HoldResult;
global using SmSongData = Gameframe.SMReader.SongData;
global using NotesLoaderSM = Gameframe.SMReader.NotesLoaderSM;
global using LocalizedString = I2.Loc.LocalizedString;
global using WwiseEvent = AK.Wwise.Event;
global using WwiseSwitch = AK.Wwise.Switch;
global using WwiseBank = AK.Wwise.Bank;
// The Wwise types (AkSoundEngine, AkAudioListener, AkBankManager) are in the global namespace.
#endif
