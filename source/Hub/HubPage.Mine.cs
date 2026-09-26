using UnityEngine;
using static NocturneFlatScroll.EditorInput;
using static NocturneFlatScroll.EditorPageKit;
using static NocturneFlatScroll.EditorUi;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using Key = UnityEngine.InputSystem.Key;

namespace NocturneFlatScroll;

// My uploads (DESIGN-HUB 1.7): what this PC's hub key uploaded, whatever its state on the hub
// (removed entries say why), with "changed since your last upload" from the files' sizes and
// times (nothing is rebuilt or hashed to show it). An entry can get a new version or be deleted
// from the hub (two confirms). The hub key, which is the only way to own uploads (there are no
// accounts), can be backed up to a file, replaced by a saved one, or made new if it leaked.
internal static partial class HubPage
{
    private static List<HubMyCard>? mine;
    private static int mineIndex;
    private static bool mineLoading, mineAgain;
    private static string? mineProblem;
    private static HubMe? me;
    private static Dictionary<string, bool> changedSince = new(StringComparer.Ordinal);
    private static string? pendingMineSelect;

    private static void ResetMine()
    {
        mine = null;
        mineIndex = 0;
        mineLoading = mineAgain = false;
        mineProblem = null;
        me = null;
        changedSince = new Dictionary<string, bool>(StringComparer.Ordinal);
        pendingMineSelect = null;
    }

    /// <summary>The key's uploads and limits from the hub; each one's "changed since" is worked out on the worker.</summary>
    private static void LoadMine(bool force = false)
    {
        if (mineLoading)
        {
            // Asked again while a load is going (after an upload): loaded once more after it.
            mineAgain |= force;
            return;
        }
        if (store == null || api == null || link is Link.Opening or Link.Connecting or Link.Down) return;
        var who = identity;
        if (who?.UploaderId == null)
        {
            mine = null;
            return;
        }
        if (mine != null && !force) return;
        mineLoading = true;
        mineProblem = null;
        var a = api;
        var s = store;
        var ct = Ct;
        jobs.Run(Task.Run(async () =>
        {
            var list = await a.MyPackagesAsync(who.Key, ct).ConfigureAwait(false);
            HubMe? limits = null;
            try { limits = await a.MeAsync(who.Key, ct).ConfigureAwait(false); }
            catch (HubException) { }
            var changed = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var card in list) changed[card.Id] = s.UploadFor(card.Id) is { } record && s.SourceChanged(record);
            return (list, limits, changed);
        }, ct), result =>
        {
            mineLoading = false;
            if (mineAgain)
            {
                mineAgain = false;
                LoadMine(force: true);
                return;
            }
            // The key changed while this loaded: this list isn't its.
            if (identity?.Key != who.Key) return;
            mine = result.list.OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
            me = result.limits;
            changedSince = result.changed;
            SelectMine();
        }, ex =>
        {
            mineLoading = false;
            if (mineAgain)
            {
                mineAgain = false;
                LoadMine(force: true);
                return;
            }
            mineProblem = Words(ex);
            LogFailure("loading your uploads", ex);
        });
    }

    private static void SelectMine()
    {
        if (pendingMineSelect == null || mine == null) return;
        int at = mine.FindIndex(c => c.Id == pendingMineSelect);
        if (at >= 0) mineIndex = at;
        pendingMineSelect = null;
    }

    private static HubMyCard? SelectedMine => mine != null && mine.Count > 0 ? mine[Math.Clamp(mineIndex, 0, mine.Count - 1)] : null;

    private static (string Tag, Color Color) MineTag(HubMyCard card) => card.Status switch
    {
        "live" => ("ON THE HUB", Green),
        "hidden" => ("UNDER REVIEW", Amber),
        "removed" => ("REMOVED", Red),
        "deleted" => ("DELETED", DimText),
        _ => (card.Status.ToUpperInvariant(), DimText),
    };

    private static Texture? MinePicture(HubMyCard card) =>
        thumbs.Want("m:" + card.Id + ":" + card.Version, () => HubThumbs.Checked(card.Thumb));

    private static ThumbRow MineRow(int i)
    {
        var card = mine![i];
        var row = new ThumbRow { Picture = MinePicture(card) };
        (row.Tile, row.TileColor) = HubThumbs.Tile(card.Id, card.Title);
        row.Title = TitleLine(card.Title, card.Artist);
        row.Sub = card.StatusWords;
        AddChips(row, card);
        bool changed = changedSince.TryGetValue(card.Id, out bool c) && c && card.Status == "live";
        row.Note = changed ? "<color=#F2B02E>Changed since your last upload</color>" : card.PictureState == "waiting" ? "(its picture shows after a check)" : "";
        (row.Tag, row.TagColor) = MineTag(card);
        row.Meta = KindBadge(card.Kind, card.Lanes) + "\n" + $"v{card.Version}" + (card.Downloads is long n ? $"   {Count(n)} {(n == 1 ? "download" : "downloads")}" : "");
        return row;
    }

    private static string MineMessage()
    {
        if (identityProblem != null) return "Your hub key can't be read: " + identityProblem + ".\n\nUse a saved key, or make a new one (the old file is kept).";
        if (identity == null) return "You haven't uploaded anything from this PC yet.\n\nYour uploads are tied to this PC's hub key, which is made at your first upload. Only that key can change or delete them.";
        if (identity.UploaderId == null) return "Nothing is uploaded with this PC's hub key yet. It's registered at your first upload.";
        if (link == Link.Down) return linkProblem;
        if (link is Link.Opening or Link.Connecting || mineLoading || (mine == null && mineProblem == null)) return "Loading your uploads...";
        if (mineProblem != null) return mineProblem + $"\n\n{(PadNames ? "A" : "F5")} tries again.";
        return "You have nothing on the hub right now. The Upload tab shares something you made.";
    }

    private static void MinePanel(PanelView v)
    {
        var card = SelectedMine;
        if (card == null)
        {
            v.NoPicture = true;
            v.Title = "Your hub key";
            v.Body = identity == null
                ? "There are no accounts: the hub knows your uploads by this PC's hub key, made at your first upload or report. " +
                  "Back it up after your first upload, so you can still manage your uploads after reinstalling Windows.\n\n" +
                  "Use a saved key brings back a key you backed up."
                : "The hub knows your uploads by this PC's hub key. It's kept in the Hub folder, encrypted to your Windows account.\n\n" +
                  "Back up key saves it to a file, so you can still manage your uploads after reinstalling Windows. " +
                  "Make a new key replaces it if it was shared by mistake; your uploads stay yours.";
            v.Facts = LimitsText();
            return;
        }
        v.Picture = MinePicture(card);
        (v.Tile, v.TileColor) = HubThumbs.Tile(card.Id, card.Title);
        v.Title = card.Title;
        v.Line1 = card.Artist;
        v.Line2 = card.StatusWords;
        v.Line3 = "uploaded " + Date(card.CreatedAt) + (card.UpdatedAt > card.CreatedAt ? ", updated " + Date(card.UpdatedAt) : "");
        var (tag, color) = MineTag(card);
        v.Badge = KindBadge(card.Kind, card.Lanes) + $"   <color=#{HexOf(color)}>{tag}</color>";
        var facts = new List<string> { $"v{card.Version}  -  " + SizeAndCount(card.Size, card.Downloads) };
        facts.Add(card.PictureState switch
        {
            "waiting" => "Its picture shows after the hub's owner checks it (or after 24 hours).",
            "refused" => "The hub's owner didn't take its picture, so it shows a title tile.",
            _ => "",
        });
        string limits = LimitsText();
        if (limits.Length > 0) facts.Add(limits);
        v.Facts = string.Join("\n", facts.Where(f => f.Length > 0));
        v.Table = DifficultyTable(card);
        v.Body = card.Description.Length > 0 ? card.Description : "(no description)";
        bool changed = changedSince.TryGetValue(card.Id, out bool c) && c;
        v.Note = card.Status switch
        {
            "live" when changed => "Changed since your last upload. Upload a new version?",
            "removed" => "Players who downloaded it keep their copy.",
            "deleted" => "You deleted it from the hub. Players who downloaded it keep their copy.",
            _ => "",
        };
    }

    // Today's uploads and the key's first days, from GET /v1/me.
    private static string LimitsText()
    {
        var limits = me?.Limits;
        if (limits == null || limits.UploadsPerDay <= 0) return "";
        string text = $"Today: {limits.UploadsToday} of {limits.UploadsPerDay} uploads.";
        if (limits.Probation) text += " A new key's first 2 days have lower limits.";
        return text;
    }

    private static List<PanelAction> MineActions()
    {
        var actions = new List<PanelAction>();
        var card = SelectedMine;
        string enter = PadNames ? "A" : "Enter";
        if (card != null)
        {
            if (card.Status == "live" && link == Link.Online)
                actions.Add(new PanelAction { Text = "Upload a new version...", Key = enter, Do = () => NewVersion(card) });
            if (card.Status is "live" or "hidden")
                actions.Add(new PanelAction { Text = "Delete from the hub...", Key = PadNames ? "" : "Del", Do = () => AskDeleteFromHub(card) });
            if (LongDescription(card.Description)) actions.Add(ReadDescription(card.Title, card.Description));
        }
        if (identity != null) actions.Add(new PanelAction { Text = "Back up key...", Do = BackUpKey });
        actions.Add(new PanelAction { Text = "Use a saved key...", Do = UseSavedKey });
        if (identity != null || identityProblem != null) actions.Add(new PanelAction { Text = "Make a new key...", Do = AskNewKey });
        return actions;
    }

    private static void MinePrimary()
    {
        var card = SelectedMine;
        if (card == null) return;
        if (card.Status == "live") NewVersion(card);
        else Say(card.StatusWords + ".", 5f);
    }

    private static bool MineKeys(InputKeyboard k)
    {
        var card = SelectedMine;
        if (card == null || Ctrl(k) || Alt(k)) return false;
        if (Pressed(k, Key.Delete))
        {
            if (card.Status is "live" or "hidden") AskDeleteFromHub(card);
            return true;
        }
        return false;
    }

    // A new version goes through the upload steps with this entry's battle or difficulties.
    private static void NewVersion(HubMyCard card)
    {
        if (!UploadAllowed(out string why))
        {
            Say(why, 6f);
            return;
        }
        var record = store!.UploadFor(card.Id);
        if (card.IsBattle)
        {
            string? folder = record != null && record.Source.Length > 0 ? store.Paths.Full(record.Source) : null;
            if (folder != null && Directory.Exists(folder)) StartBattleUpload(folder, card);
            else PickBattle(card);
        }
        else PickPack(card);
    }

    // ---- delete from the hub ------------------------------------------------------------------------

    private static void AskDeleteFromHub(HubMyCard card)
    {
        if (link is not (Link.Online or Link.TooOld))
        {
            Say("Deleting from the hub needs the hub, which can't be used now. " + linkProblem, 6f);
            return;
        }
        var first = Confirm($"Delete {card.Title} from the hub?", "No, keep it on the hub", "Yes, delete it from the hub",
            "This removes it from the hub for everyone. Players who downloaded it keep their copy. Your own battle on this PC isn't touched.",
            () =>
            {
                // The second prompt has its Yes on the other row, so one spot clicked twice can't answer both.
                var second = Confirm($"Really delete {card.Title} from the hub?", "No, keep it", "Yes, delete it for everyone",
                    "This can't be undone. You can upload it again as a new entry later.", () => DeleteFromHub(card), BackToMain, yesFirst: true);
                second.Plain = true;
                ShowPicker(second);
            }, BackToMain);
        first.Plain = true;
        ShowPicker(first);
    }

    private static void DeleteFromHub(HubMyCard card)
    {
        var who = identity;
        if (who == null || api == null || store == null) return;
        var a = api;
        var s = store;
        var ct = Ct;
        Work($"Deleting {card.Title} from the hub...", Task.Run(async () =>
        {
            try { await a.DeletePackageAsync(card.Id, who.Key, ct).ConfigureAwait(false); }
            // Already gone from the hub (deleted, or removed by its owner): nothing left to delete.
            catch (HubException ex) when (ex.Code is "deleted" or "not_found") { }
            s.ForgetUpload(card.Id);
            return true;
        }, ct), _ =>
        {
            ModLog.Info($"Hub: deleted {card.Id} from the hub.");
            BackToMain();
            Say($"{card.Title} is deleted from the hub. Players who downloaded it keep their copy.", 8f);
            LoadMine(force: true);
        }, ex =>
        {
            BackToMain();
            Say(Words(ex), 8f);
        });
    }

    // ---- the hub key (DESIGN-HUB 1.7, 2.8) --------------------------------------------------------------

    /// <summary>Saves the key to a text file the player picks (plain on purpose, so it works on another PC).</summary>
    private static void BackUpKey()
    {
        var who = identity;
        if (who == null)
        {
            Say("There's no hub key yet: it's made at your first upload or report.", 5f);
            return;
        }
        Work("Choose where to save your hub key, in the window that opened...",
            FileDialogs.Save(FileDialogs.Purpose.HubKey, "Back up your hub key", "Nocturne hub key.txt", ".txt"), path =>
        {
            if (path == null)
            {
                Say("The key wasn't saved.", 3f);
                return;
            }
            Work("Saving your hub key...", Task.Run(() =>
            {
                who.WriteBackup(path);
                return Path.GetFileName(path);
            }), name =>
            {
                KeyBackupOffered();
                ModLog.Info("Hub: the hub key was backed up to a file.");
                Say($"Your hub key is saved in {name}. Keep it private: it works like a password for your uploads. Don't keep it in a synced folder such as OneDrive.", 12f);
            });
        });
    }

    /// <summary>Reads a backed-up key, asks the hub whose it is, and after a warning makes it this PC's key.</summary>
    private static void UseSavedKey()
    {
        Work("Choose your saved hub key, in the window that opened...", FileDialogs.Open(FileDialogs.Purpose.HubKey, "Use a saved hub key"), path =>
        {
            if (path == null) return;
            if (api == null || link is not (Link.Online or Link.TooOld))
            {
                Say("Checking a saved key needs the hub, which can't be used now. " + linkProblem, 6f);
                return;
            }
            var a = api;
            var ct = Ct;
            Work("Asking the hub about the saved key...", Task.Run(async () =>
            {
                string key = HubIdentity.ReadBackup(path);
                var who = await a.MeAsync(key, ct).ConfigureAwait(false);
                return (key, who.Uploader);
            }, ct), result =>
            {
                if (identity != null && identity.Key == result.key)
                {
                    Say("That's already this PC's hub key.", 4f);
                    return;
                }
                var confirm = Confirm($"Use the saved key of {UploaderName(new HubUploaderRef { Name = result.Uploader.Name, Tag = result.Uploader.Tag })}?",
                    "No, keep this PC's key", "Yes, use the saved key",
                    "Whoever made this file can also control these uploads. Only use a key you backed up yourself. This PC's key is kept in the Hub folder.",
                    () => ReplaceKey(result.key, result.Uploader), BackToMain);
                confirm.Plain = true;
                ShowPicker(confirm);
            });
        });
    }

    private static void ReplaceKey(string key, HubUploader uploader)
    {
        var p = paths!;
        Work("Switching to the saved key...", Task.Run(() =>
        {
            var next = HubIdentity.FromKey(key, uploader.Name, uploader.Id);
            HubIdentity.Replace(p.Identity, next);
            return next;
        }), next =>
        {
            identity = next;
            identityProblem = null;
            mine = null;
            me = null;
            ModLog.Info("Hub: this PC uses a saved hub key now (the one it had is kept).");
            BackToMain();
            Say($"This PC uses the saved key now ({UploaderName(new HubUploaderRef { Name = uploader.Name, Tag = uploader.Tag })}). The key it had is kept in the Hub folder.", 9f);
            LoadMine(force: true);
        }, ex =>
        {
            BackToMain();
            Say(Words(ex), 8f);
        });
    }

    private static void AskNewKey()
    {
        var confirm = Confirm("Make a new hub key?", "No, keep my key", "Yes, make a new key",
            "Use this if your key was shared or posted by mistake. Your uploads stay yours; the old key stops working at once. Back up the new key afterwards.",
            MakeNewKey, BackToMain);
        ShowPicker(confirm);
    }

    private static void MakeNewKey()
    {
        var who = identity;
        var p = paths!;
        string name = who != null && who.Name.Length > 0 ? who.Name : defaultName;
        // A registered key changes on the hub (only the new key's SHA-256 travels); the new key is
        // saved before the hub is asked, so a lost answer is sorted out the next time the page opens.
        if (who?.UploaderId != null)
        {
            if (api == null || link is not (Link.Online or Link.TooOld))
            {
                Say("Changing a key needs the hub, which can't be used now. " + linkProblem, 6f);
                return;
            }
            var a = api;
            var ct = Ct;
            Work("Making a new key...", Task.Run(() => HubIdentity.RotateAsync(a, p.Identity, who, ct), ct), next =>
            {
                identity = next;
                identityProblem = null;
                ModLog.Info("Hub: the hub key was changed.");
                BackToMain();
                Say("Your new key is in use, and the old one no longer works. Back it up now (Back up key).", 10f);
            }, ex =>
            {
                BackToMain();
                Say(Words(ex), 8f);
                // The hub may have changed the key although its answer was lost: it's asked which key works now.
                if (ex is not OperationCanceledException) SettleKeyChange();
            });
            return;
        }
        // Not registered yet (or unreadable): a new key on this PC; the old file is kept.
        Work("Making a new key...", Task.Run(() =>
        {
            var next = HubIdentity.Create(name);
            HubIdentity.Replace(p.Identity, next);
            return next;
        }), next =>
        {
            identity = next;
            identityProblem = null;
            mine = null;
            ModLog.Info("Hub: this PC has a new hub key (not registered yet).");
            BackToMain();
            Say("This PC has a new hub key. It's registered at your first upload; back it up then.", 8f);
        }, ex =>
        {
            BackToMain();
            Say(Words(ex), 8f);
        });
    }

    /// <summary>
    /// After a key change that failed: one that stopped before its answer came is sorted out now
    /// (the hub is asked which key works), and the page takes that key, so a second try starts
    /// from the key the hub has. Nothing is asked when no key change is waiting.
    /// </summary>
    private static void SettleKeyChange()
    {
        if (api == null || paths == null) return;
        var a = api;
        var p = paths;
        var ct = Ct;
        jobs.Run(Task.Run(() => HubIdentity.RecoverAsync(a, p.Identity, ct), ct), settled =>
        {
            if (settled == null || settled.Key == identity?.Key) return;
            identity = settled;
            identityProblem = null;
            mine = null;
            me = null;
            ModLog.Info("Hub: a key change that was cut short was finished.");
            Say("Your new key is in use after all, and the old one no longer works. Back it up now (Back up key).", 10f);
            LoadMine(force: true);
        }, ex => LogFailure("finishing the key change", ex));
    }
}
