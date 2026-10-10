using System.Text;
using PKForge.Domain;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace PKForge.Engine;

/// <summary>
/// Adapts the pinned Auto Legality Mod. Everything runs fully offline, in-process.
/// A generated/repaired mon is placed into the session's slot; callers then serialize
/// and write through the usual safe path (validate → backup → atomic write).
/// </summary>
public sealed class LegalizerService : ILegalizerService
{
    private static readonly object TrainerGenerationLock = new();
    private readonly GameStrings _strings = GameInfo.GetStrings("en");
    private readonly IGenerationOwnershipSettings? _ownershipSettings;

    public LegalizerService(IGenerationOwnershipSettings? ownershipSettings = null) =>
        _ownershipSettings = ownershipSettings;

    static LegalizerService()
    {
        // Our AutoMod is source-built against the exact same Core revision, so the
        // NuGet-version mismatch gate does not apply.
        APILegality.EnableDevMode = true;
        // Unfixable mons must fail, not become the ALM's joke Pokémon (a shiny Stunfisk named PANCAKE).
        Legalizer.EnableEasterEggs = false;
        // A save editor's "Level: 50" means Lv 50. The AutoMod's VGC shortcut (on by default) rebuilt
        // every Showdown set or create-form request at exactly Lv 50 as Lv 100, and still called it legal.
        APILegality.ForceLevel100for50 = false;
    }

    public GenerationOutcome Generate(ISaveEngineSession session, int box, int slot, GenerationRequest request)
    {
        if (session is Unbound.UnboundEngineSession unbound)
            return unbound.GenerateInto(box, slot, request);
        if (session is RadicalRed.CfruEngineSession radicalRed)
            return radicalRed.GenerateInto(box, slot, request);
        var text = BuildShowdownText(request, ((SaveEngineSession)session).SaveFile.Context);
        return GenerateFromShowdown(session, box, slot, text, request.AllowUnsupportedSpecies);
    }

    public GenerationOutcome GenerateFromShowdown(ISaveEngineSession session, int box, int slot, string showdownText,
        bool allowUnsupportedSpecies = false)
    {
        if (session is Unbound.UnboundEngineSession unbound)
            return unbound.GenerateFromShowdownText(box, slot, showdownText);
        if (session is RadicalRed.CfruEngineSession radicalRed)
            return radicalRed.GenerateFromShowdownText(box, slot, showdownText);
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var set = new ShowdownSet(showdownText);
        if (set.Species == 0)
            return new GenerationOutcome(false, "Could not read the set (no species).");
        // No ROM hack can extend a save format's species table; reject with the real
        // reason instead of a misleading legalizer failure.
        if (set.Species > save.MaxSpeciesID)
        {
            if (!allowUnsupportedSpecies)
                return new GenerationOutcome(false,
                    $"{GameName(save)} cannot store this Pokémon; its species does not exist in this generation.");

            var forced = BuildUnsupportedMon(save, set);
            var forcedPlaced = PlaceGenerated(save, forced, box, slot);
            return forcedPlaced is not null
                ? new GenerationOutcome(false, forcedPlaced)
                : new GenerationOutcome(true,
                    "Generated (HaX): this species is unsupported in this game; no guarantee it works.");
        }

        var result = GenerateLegal(engineSession, set);
        var legal = result.Status is LegalizationResult.Regenerated ? ConvertForSave(save, result.Created) : null;
        if (allowUnsupportedSpecies && (legal is null || !MatchesRequest(legal, set, showdownText)))
        {
            var asWritten = BuildAsWritten(engineSession, set, showdownText, legal);
            var hax = PlaceGenerated(save, asWritten, box, slot);
            return hax is not null
                ? new GenerationOutcome(false, hax)
                : new GenerationOutcome(true, new LegalityAnalysis(asWritten).Valid
                    ? "Generated - legal."
                    : "Generated (HaX): built exactly as written; not legal.");
        }
        if (result.Status is not LegalizationResult.Regenerated)
            return new GenerationOutcome(false,
                result.Status switch
                {
                    LegalizationResult.Timeout => "The legalizer timed out for this request.",
                    LegalizationResult.VersionMismatch => "Engine version mismatch.",
                    _ => "No legal combination found for this request in this game.",
                });

        var created = ConvertForSave(save, result.Created);
        var analysis = new LegalityAnalysis(created);
        var placed = PlaceGenerated(save, created, box, slot);
        if (placed is not null)
            return new GenerationOutcome(false, placed);
        return new GenerationOutcome(true, analysis.Valid
            ? save.IsFromTrainer(created) ? "Generated - legal." : "Generated - legal (event OT)."
            : "Generated (legality imperfect).");
    }

    /// <summary>Places a generated mon: boxes overwrite the slot; the party appends
    /// (capped at six, compact like the games). Null on success, else the failure.</summary>
    private static string? PlaceGenerated(SaveFile save, PKM created, int box, int slot)
    {
        if (box == -1)
        {
            if (save.PartyCount >= 6)
                return "The party is full.";
            // Surgical append: the PartyData setter would also dex-mark, bump records,
            // and rewrite handler data for every party member.
            save.SetPartySlotAtIndex(created, Math.Min(save.PartyCount, 5), EntityImportSettings.None);
        }
        else
        {
            save.SetBoxSlotAtIndex(created, box, slot, EntityImportSettings.None);
        }
        return null;
    }

    /// <summary>HaX generation for species beyond the save's table: a plain mon of the
    /// save's own format carrying the set details, no encounter, no legality. The games
    /// have no data for the species, so behavior is explicitly not guaranteed.</summary>
    /// <summary>
    /// HaX: the set exactly as its text asks, legal or not. The base is the closest legal mon
    /// (the request's own legal version, else the species alone at the level, else at any level)
    /// so OT, met data and ball stay plausible; with none, a blank of the save's format. Every
    /// field the text asks for is then written over it. IVs and friendship are forced only when
    /// the text states them: Showdown defaults (31s, 255) are not a request.
    /// </summary>
    private PKM BuildAsWritten(SaveEngineSession session, ShowdownSet set, string text, PKM? closest)
    {
        var save = session.SaveFile;
        var pk = closest?.Clone() ?? ClosestLegalBase(session, set) ?? BuildUnsupportedMon(save, set);
        Span<int> ivs = stackalloc int[6];
        pk.GetIVs(ivs);
        var friendship = pk.CurrentFriendship;
        pk.ApplySetDetails(set);
        if (!States(text, "IVs:")) pk.SetIVs(ivs);
        if (!States(text, "Friendship:")) pk.CurrentFriendship = friendship;
        pk.ResetPartyStats();
        pk.RefreshChecksum();
        return pk;
    }

    private PKM? ClosestLegalBase(SaveEngineSession session, ShowdownSet set)
    {
        var species = _strings.specieslist[set.Species] + (set.FormName is { Length: > 0 } form ? $"-{form}" : "");
        foreach (var minimal in new[] { $"{species}\nLevel: {set.Level}", species })
        {
            var result = GenerateLegal(session, new ShowdownSet(minimal));
            if (result.Status is LegalizationResult.Regenerated)
                return ConvertForSave(session.SaveFile, result.Created);
        }
        return null;
    }

    /// <summary>Whether a legal candidate already is what the text asks for, so HaX keeps it legal.</summary>
    private static bool MatchesRequest(PKM pk, ShowdownSet set, string text)
    {
        if (pk.Species != set.Species || pk.Form != set.Form || pk.CurrentLevel != set.Level) return false;
        ReadOnlySpan<ushort> moves = [pk.Move1, pk.Move2, pk.Move3, pk.Move4];
        foreach (var move in set.Moves)
            if (move != 0 && !moves.Contains(move)) return false;
        if (set.HeldItem != 0)
        {
            // Compare with what the format can hold: an item the game lacks can't be forced either.
            var probe = pk.Clone();
            probe.ApplyHeldItem(set.HeldItem, set.Context);
            if (probe.HeldItem != pk.HeldItem) return false;
        }
        if (set.Nature < Nature.Random && pk.Nature != set.Nature) return false;
        if (set.Ability >= 0 && pk.Ability != set.Ability) return false;
        if (set.Shiny && !pk.IsShiny) return false;
        Span<int> have = stackalloc int[6];
        pk.GetEVs(have);
        if (pk is not GBPKM && !have.SequenceEqual(set.EVs)) return false;
        pk.GetIVs(have);
        return !States(text, "IVs:") || have.SequenceEqual(set.IVs);
    }

    private static bool States(string text, string field) =>
        text.Split('\n').Any(line => line.TrimStart().StartsWith(field, StringComparison.OrdinalIgnoreCase));

    private static PKM BuildUnsupportedMon(SaveFile save, ShowdownSet set)
    {
        var template = EntityBlank.GetBlank(save);
        if (template.Version == 0)
            template.Version = save.Version;
        // ApplySetDetails clamps the species to the format maximum; it still fills
        // level, moves, IVs, EVs, nature (PID-aware on Gen 3/4), shiny and EC.
        template.ApplySetDetails(set);
        template.Species = (ushort)set.Species; // the actual override
        if (template.Format >= 3)
            template.Ball = (byte)Ball.Poke;
        template.OriginalTrainerName = save.OT;
        template.TID16 = save.TID16;
        if (template is not GBPKM)
            template.SID16 = save.SID16;
        template.OriginalTrainerGender = (byte)Math.Clamp((int)save.Gender, 0, 1);
        template.RefreshChecksum();
        return template;
    }

    public GenerationOutcome LegalizeSlot(ISaveEngineSession session, int box, int slot)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        // Box -1 is the party: it has its own accessors, and the box path would go out
        // of range and abort the whole mutation behind the loading overlay.
        var current = box == -1 ? save.GetPartySlotAtIndex(slot) : save.GetBoxSlotAtIndex(box, slot);
        if (current.Species == 0)
            return new GenerationOutcome(false, "Empty slot.");
        if (new LegalityAnalysis(current).Valid)
            return new GenerationOutcome(true, "Already legal.");

        var repaired = LegalizeKeepingOrigin(save, current);
        if (repaired.Species != current.Species || !new LegalityAnalysis(repaired).Valid)
            return new GenerationOutcome(false, "Could not find a legal repair for this mon.");

        if (box == -1)
            save.SetPartySlotAtIndex(repaired, slot, EntityImportSettings.None);
        else
            save.SetBoxSlotAtIndex(repaired, box, slot, EntityImportSettings.None);
        return new GenerationOutcome(true, OriginNote(current, repaired) is { } note ? $"Legalized. {note}" : "Legalized.");
    }

    /// <summary>
    /// Encounter order for making or repairing a Pokémon: a catch (wild or static) first,
    /// then an egg, and only then events and in-game trades, which carry another trainer.
    /// Auto-Legality's default puts eggs first, and an egg fits almost any request.
    /// </summary>
    private static readonly EncounterTypeGroup[] CatchesFirst =
        [EncounterTypeGroup.Slot, EncounterTypeGroup.Static, EncounterTypeGroup.Egg, EncounterTypeGroup.Mystery, EncounterTypeGroup.Trade];

    /// <summary>The same for repairing a hatched Pokémon: it stays hatched whenever it can.</summary>
    private static readonly EncounterTypeGroup[] EggsFirst =
        [EncounterTypeGroup.Egg, EncounterTypeGroup.Slot, EncounterTypeGroup.Static, EncounterTypeGroup.Mystery, EncounterTypeGroup.Trade];

    /// <summary>
    /// Repairs a Pokémon while keeping as much of it as stays legal. First the smallest repair: EVs
    /// cut to a legal spread and moves it can never know dropped, nothing else touched. Then a caught
    /// Pokémon is rebuilt at its own met location; failing that (or for a hatched one) Auto-Legality
    /// makes it, searching its own game and encounters of its own kind first (eggs allow any PID, so a
    /// caught shiny used to come back hatched). Either way its game, trainer, ball, level, EXP, moves,
    /// name, item, PID and IVs, ability and EVs go back on wherever the result stays legal.
    /// </summary>
    internal static PKM LegalizeKeepingOrigin(SaveFile save, PKM current, Shiny shinyKind = Shiny.Always)
    {
        var analysis = new LegalityAnalysis(current);
        var spreads = LegalEVSpreads(current);
        var moves = KnowableMoves(current, analysis);
        // The smallest repair first: when cutting its EVs to a legal spread and dropping moves it can
        // never know is enough, everything else stays as it was.
        foreach (var small in SmallRepairs(current, spreads, moves))
            if (new LegalityAnalysis(small).Valid)
                return small;

        // Its own EVs, or when they are not legal as they are, the closest spreads that may be.
        IReadOnlyList<int[]> evs = spreads.Count > 0 ? spreads : [EVsOf(current)];
        var hatched = WasHatched(current);
        if (!hatched && RegenerateAtOrigin(save, current, analysis, moves, evs, shinyKind) is { } atOrigin)
            return atOrigin;

        // Auto-Legality reads the set back from the Pokémon, EVs included: hand it the most cautious
        // spread. Whatever of the Pokémon stays legal goes back on what it makes.
        var start = spreads.Count > 0 ? WithEVs(current, spreads[^1]) : current;
        // The analysis's best match for a broken Pokémon is not always its kind of origin: eggs fit
        // any PID, a wrong ball fits a catch. Only a match of its own kind leads the search, and
        // encounters of that kind go first. A gift egg (Riley's Riolu) is an egg too, not only a
        // bred one; without the lead a hatched gift came back from the Day-Care. (Auto-Legality
        // finds the lead by reference and makes bred eggs afresh on every search, so a bred egg
        // never leads: only a catch, gift or event match can.)
        var lead = analysis.EncounterOriginal.IsEgg == hatched ? analysis : null;
        PKM repaired;
        lock (TrainerGenerationLock)
        {
            var previous = EncounterMovesetGenerator.PriorityList;
            var previousPriority = APILegality.GameVersionPriority;
            var previousOrder = APILegality.PriorityOrder;
            try
            {
                EncounterMovesetGenerator.PriorityList = hatched ? EggsFirst : CatchesFirst;
                // Its own game first. Auto-Legality searches the newest game first, so an Emerald
                // Pokémon was made in Colosseum/XD or hatched in LeafGreen before Emerald was tried.
                APILegality.GameVersionPriority = GameVersionPriorityType.PriorityOrder;
                APILegality.PriorityOrder = [current.Version, .. GameUtil.GameVersions.Where(z => z != current.Version)];
                repaired = AutoLegalize(save, start, moves, lead);
            }
            finally
            {
                EncounterMovesetGenerator.PriorityList = previous;
                APILegality.GameVersionPriority = previousPriority;
                APILegality.PriorityOrder = previousOrder;
            }
        }
        return KeepWhatItCan(repaired, current, moves, evs, shinyKind);
    }

    /// <summary>Hatched from an egg. Gen 3 keeps no egg date: a hatched Pokémon is met at level 0.</summary>
    private static bool WasHatched(PKM pk) => pk.IsEgg || pk.WasEgg || (pk.Format == 3 && pk.MetLevel == 0);

    /// <summary>The moves it can really know: an unlearnable one would rule out every encounter.</summary>
    private static ushort[] KnowableMoves(PKM pk, LegalityAnalysis analysis) =>
        new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }.Where((m, i) => m != 0 && analysis.Info.Moves[i].Valid).ToArray();

    private static int MoveCount(PKM pk) => new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }.Count(m => m != 0);

    /// <summary>
    /// The Pokémon itself with only its EVs cut to a legal spread; then with only the moves it can
    /// never know dropped (PP refilled); then with both.
    /// </summary>
    private static IEnumerable<PKM> SmallRepairs(PKM current, List<int[]> spreads, ushort[] moves)
    {
        foreach (var spread in spreads)
            yield return WithEVs(current, spread);
        if (moves.Length == 0 || moves.Length == MoveCount(current))
            yield break;
        var fewer = current.Clone();
        fewer.SetMoves(moves);
        fewer.RefreshChecksum();
        yield return fewer;
        foreach (var spread in spreads)
            yield return WithEVs(fewer, spread);
    }

    /// <summary>
    /// Auto-Legality on <paramref name="start"/>; when that finds nothing, again with only the moves
    /// the Pokémon can really know, since one it can never learn rules out every encounter.
    /// </summary>
    private static PKM AutoLegalize(SaveFile save, PKM start, ushort[] moves, LegalityAnalysis? lead)
    {
        var made = save.Legalize(start.Clone(), lead); // Legalize rewrites the mon it is given
        if (new LegalityAnalysis(made).Valid || moves.Length == MoveCount(start)) return made;
        var fewer = start.Clone();
        fewer.SetMoves(moves);
        fewer.RefreshChecksum();
        var retry = save.Legalize(fewer, lead);
        return new LegalityAnalysis(retry).Valid ? retry : made;
    }

    /// <summary>
    /// Rebuilds the Pokémon from an encounter at its own met location and game, keeping what
    /// the player asked for (species, shiny, gender, nature) and, where still legal, everything
    /// <see cref="KeepWhatItCan"/> puts back. Auto-Legality walks every encounter of the species
    /// first and ran out of time before reaching the right place; this goes straight there. Null
    /// when no encounter there yields a legal Pokémon.
    /// </summary>
    private static PKM? RegenerateAtOrigin(SaveFile save, PKM current, LegalityAnalysis analysis,
        ushort[] moves, IReadOnlyList<int[]> evs, Shiny shinyKind)
    {
        if (current.MetLocation == 0) return null;
        // The encounter the analysis matched is where it really came from (a Ralts met at Lv 7);
        // other encounters at the same place can be a later stage met higher (a Lv 60 Kirlia).
        var matched = analysis.EncounterOriginal is EncounterInvalid ? null : analysis.EncounterOriginal;
        return RegenerateAtOrigin(save, current, matched, moves, evs, shinyKind)
            ?? (moves.Length > 0 ? RegenerateAtOrigin(save, current, matched, [], evs, shinyKind) : null);
    }

    private static PKM? RegenerateAtOrigin(SaveFile save, PKM current, IEncounterable? matched, ushort[] moves,
        IReadOnlyList<int[]> evs, Shiny shinyKind)
    {
        var template = current.Clone();
        var generated = EncounterMovesetGenerator.GenerateEncounters(template, save, moves, current.Version);
        var here = (matched is null ? generated : generated.Prepend(matched)).Distinct()
            .Where(e => e is not IEncounterEgg && e.Location == current.MetLocation)
            .Take(32);
        var criteria = new EncounterCriteria
        {
            Shiny = current.IsShiny ? shinyKind : Shiny.Never,
            Gender = current.Gender is 0 or 1 ? (Gender)current.Gender : Gender.Random,
            Nature = current.Nature,
        };
        // An encounter that comes out above the Pokémon's level (Sword/Shield's Wild Area statics
        // are made at Lv 60) is kept only when no encounter there fits the level it has now.
        PKM? higher = null;
        foreach (var encounter in here)
        {
            PKM made;
            try { made = encounter.ConvertToPKM(save, criteria); }
            catch (ArgumentException) { continue; }
            // Sword/Shield overworld catches derive the PID from a seed and the generator
            // ignores the shiny request: search for a seed that gives the asked shininess.
            if (!IsShinyAsAsked(made, current.IsShiny, shinyKind) && made is PK8 pk8
                && encounter is EncounterSlot8 slot8 && slot8.GetRequirement(pk8) == OverworldCorrelation8Requirement.MustHave)
                Overworld8RNG.ApplyDetails(pk8, criteria, current.IsShiny ? shinyKind : Shiny.Never);

            // Some generators (Gen 5 wild slots) ignore the shiny request; set it afterwards.
            // Each try rolls a new PID, and only some meet the origin's PID rules.
            for (var tries = 0; tries < 64 && !IsShinyAsAsked(made, current.IsShiny, shinyKind); tries++)
                made = TryKeep(made, pk => { if (current.IsShiny) pk.SetShiny(shinyKind); else pk.SetUnshiny(); });
            if (!IsShinyAsAsked(made, current.IsShiny, shinyKind) || !new LegalityAnalysis(made).Valid) continue;
            // The encounter is often an earlier stage (a gift Mudkip for a Swampert).
            if (EvolveInto(made, current) is not { } evolved) continue;

            made = KeepWhatItCan(evolved, current, moves, evs, shinyKind);
            if (made.CurrentLevel <= current.CurrentLevel) return made;
            higher ??= made;
        }
        return higher;
    }

    /// <summary>
    /// Puts back on a rebuilt Pokémon what <paramref name="current"/> had, each part only where the
    /// result stays legal: its game and met location, its trainer, ball, level, met level and EXP,
    /// moves, name, held item, PID and IVs (as the pair they were, else the IVs alone), ability, EVs.
    /// </summary>
    private static PKM KeepWhatItCan(PKM made, PKM current, ushort[] moves, IReadOnlyList<int[]> evs, Shiny shinyKind)
    {
        made = TryKeep(made, pk => { pk.Version = current.Version; pk.MetLocation = current.MetLocation; });
        made = TryKeep(made, pk => TakeTrainer(pk, current));
        made = TryKeep(made, pk => pk.Ball = current.Ball);
        // Its level, the level it was met at (a wild slot spans several) and its EXP into the level.
        if (made.MetLevel != current.MetLevel || made.CurrentLevel != current.CurrentLevel)
            made = TryKeep(made, pk => { pk.MetLevel = current.MetLevel; pk.CurrentLevel = current.CurrentLevel; pk.ResetPartyStats(); });
        if (current.CurrentLevel > made.CurrentLevel) made = TryKeep(made, pk => { pk.CurrentLevel = current.CurrentLevel; pk.ResetPartyStats(); });
        if (made.CurrentLevel == current.CurrentLevel && made.EXP != current.EXP) made = TryKeep(made, pk => pk.EXP = current.EXP);
        if (moves.Length > 0) made = TryKeep(made, pk => { pk.SetMoves(moves); pk.HealPP(); });
        if (current.IsNicknamed) made = TryKeep(made, pk => pk.SetNickname(current.Nickname));
        // And what it was trained with. Gen 3/4 tie the IVs to the PID, so its own PID and IVs
        // go back together when that pair was a legal one, else the IVs alone.
        if (current.HeldItem != 0) made = TryKeep(made, pk => pk.HeldItem = current.HeldItem);
        var own = TryKeep(made, pk => TakeIdentity(pk, current));
        if (IsShinyAsAsked(own, current.IsShiny, shinyKind)) made = own;
        var ivs = current.GetIVs();
        if (made.GetIVs() != ivs) made = TryKeep(made, pk => pk.SetIVs(ivs));
        if (made.Ability != current.Ability) made = TryKeep(made, pk => TakeAbility(pk, current));
        return KeepEVs(made, evs);
    }

    /// <summary>The trainer of <paramref name="from"/>: name, ID, gender and language.</summary>
    private static void TakeTrainer(PKM pk, PKM from)
    {
        var nicknamed = pk.IsNicknamed; // Gen 3 infers it from the name, which the language changes
        pk.OriginalTrainerName = from.OriginalTrainerName;
        pk.ID32 = from.ID32;
        pk.OriginalTrainerGender = from.OriginalTrainerGender;
        pk.Language = from.Language;
        if (!nicknamed) pk.ClearNickname();
    }

    /// <summary>
    /// Evolves a Pokémon made from an encounter into <paramref name="current"/>'s species and
    /// form at its level, as the games do: same PID and ability slot, the new species' ability,
    /// size and default name. Unchanged when it already is that species; null when the
    /// evolved Pokémon is not legal.
    /// </summary>
    private static PKM? EvolveInto(PKM made, PKM current)
    {
        if (made.Species == current.Species && made.Form == current.Form) return made;
        // Read before the species changes: Gen 3 has no nickname flag and compares the name with
        // the species, so a Swampert still called "MUDKIP" would count as nicknamed.
        var nicknamed = made.IsNicknamed;
        var evolved = made.Clone();
        evolved.Species = current.Species;
        evolved.Form = current.Form;
        evolved.CurrentLevel = Math.Max(made.CurrentLevel, current.CurrentLevel);
        // Gen 3 keeps an ability bit and reads the ability off the species; later formats store it.
        if (evolved.Format >= 4)
            evolved.RefreshAbility(evolved.AbilityNumber >> 1);
        if (evolved is IScaledSizeValue size)
        {
            size.HeightAbsolute = size.CalcHeightAbsolute;
            size.WeightAbsolute = size.CalcWeightAbsolute;
        }
        if (!nicknamed) evolved.ClearNickname();
        evolved.ResetPartyStats();
        evolved.RefreshChecksum();
        return new LegalityAnalysis(evolved).Valid ? evolved : null;
    }

    private static int[] EVsOf(PKM pk)
    {
        var evs = new int[6];
        pk.GetEVs(evs);
        return evs;
    }

    /// <summary>
    /// EV spreads to try instead of the Pokémon's own, closest first: its EVs cut to the format's
    /// caps (per stat, 510 in all) in proportion, so the spread keeps its shape (six 252s become
    /// six 85s); then, in Gen 3/4, the same rounded down to what vitamins give (multiples of 10, at
    /// most 100 each), the only EVs a Pokémon can have when it has gained no EXP since it was met.
    /// </summary>
    private static List<int[]> LegalEVSpreads(PKM pk)
    {
        var spreads = new List<int[]>();
        if (pk.Format < 3) return spreads; // Gen 1/2 stat experience has no total cap
        var evs = EVsOf(pk);
        var capped = evs.Select(ev => Math.Min(ev, pk.MaxEV)).ToArray();
        var total = capped.Sum();
        if (total > EffortValues.Max510)
        {
            capped = capped.Select(ev => ev * EffortValues.Max510 / total).ToArray();
            // Rounding down leaves a few points unspent: hand them back in stat order.
            for (var i = 0; i < 6 && capped.Sum() < EffortValues.Max510; i++)
                if (capped[i] > 0 && capped[i] < pk.MaxEV) capped[i]++;
        }
        if (!capped.SequenceEqual(evs)) spreads.Add(capped);
        if (pk.Format <= 4)
        {
            var vitamins = capped.Select(ev => Math.Min(ev, (int)EffortValues.MaxVitamins34) / 10 * 10).ToArray();
            if (!vitamins.SequenceEqual(evs) && !vitamins.SequenceEqual(capped)) spreads.Add(vitamins);
        }
        return spreads;
    }

    /// <summary>The closest of <paramref name="spreads"/> that keeps <paramref name="made"/> legal.</summary>
    private static PKM KeepEVs(PKM made, IReadOnlyList<int[]> spreads)
    {
        foreach (var spread in spreads)
        {
            if (EVsOf(made).SequenceEqual(spread)) return made;
            var kept = TryKeep(made, pk => { pk.SetEVs(spread); pk.ResetPartyStats(); });
            if (EVsOf(kept).SequenceEqual(spread)) return kept;
        }
        return made;
    }

    private static PKM WithEVs(PKM pk, int[] spread)
    {
        var trimmed = pk.Clone();
        trimmed.SetEVs(spread);
        // The stats follow the EVs, the HP it has left does not (Gen 8+ keep it in the box too).
        trimmed.ResetPartyStats();
        trimmed.Stat_HPCurrent = Math.Min(pk.Stat_HPCurrent, trimmed.Stat_HPMax);
        trimmed.RefreshChecksum();
        return trimmed;
    }

    /// <summary>
    /// Gives <paramref name="pk"/> the PID, encryption constant, IVs, ability and size of
    /// <paramref name="from"/>: what a seed or PID decides together, so they go back as a set.
    /// </summary>
    private static void TakeIdentity(PKM pk, PKM from)
    {
        pk.PID = from.PID;
        pk.EncryptionConstant = from.EncryptionConstant;
        pk.SetIVs(from.GetIVs());
        TakeAbility(pk, from);
        if (pk is IScaledSize size && from is IScaledSize fromSize)
        {
            size.HeightScalar = fromSize.HeightScalar;
            size.WeightScalar = fromSize.WeightScalar;
        }
        if (pk is IScaledSize3 scale && from is IScaledSize3 fromScale)
            scale.Scale = fromScale.Scale;
        if (pk is IScaledSizeValue absolute)
        {
            absolute.HeightAbsolute = absolute.CalcHeightAbsolute;
            absolute.WeightAbsolute = absolute.CalcWeightAbsolute;
        }
    }

    /// <summary>The ability slot of <paramref name="from"/> (Gen 3 stores only the slot bit).</summary>
    private static void TakeAbility(PKM pk, PKM from)
    {
        if (pk.Format >= 4) pk.RefreshAbility(from.AbilityNumber >> 1);
        else pk.AbilityNumber = from.AbilityNumber;
    }

    private static bool IsShinyAsAsked(PKM pk, bool shiny, Shiny kind) => !shiny ? !pk.IsShiny : kind switch
    {
        Shiny.AlwaysSquare => pk.ShinyXor == 0,
        Shiny.AlwaysStar => pk.IsShiny && pk.ShinyXor != 0,
        _ => pk.IsShiny,
    };

    /// <summary>
    /// <paramref name="change"/> applied to a copy, returned only if it stays legal; otherwise
    /// the Pokémon as it was. The copy itself is kept, so a random change (a new PID) is the
    /// one that was checked.
    /// </summary>
    private static PKM TryKeep(PKM pk, Action<PKM> change)
    {
        var trial = pk.Clone();
        change(trial);
        trial.RefreshChecksum();
        return new LegalityAnalysis(trial).Valid ? trial : pk;
    }

    /// <summary>Says so when the only legal repair had to change the origin, so it is never a surprise.</summary>
    private static string? OriginNote(PKM before, PKM after)
    {
        if (!WasHatched(before) && WasHatched(after))
            return "No legal version keeps its catch origin, so it is now hatched from an egg.";
        if (before.MetLocation != after.MetLocation && !WasHatched(after))
            return "Its met location changed to one where it can legally appear.";
        return null;
    }

    public GenerationOutcome LegalizeSlots(ISaveEngineSession session, IReadOnlyList<(int Box, int Slot)> slots,
        Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var repaired = 0;
        var stuck = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (box, slot) = slots[i];
            // Party (-1) and boxes have different accessors; the box path would go out
            // of range and abort the whole batch behind the loading overlay.
            var current = box == -1 ? save.GetPartySlotAtIndex(slot) : save.GetBoxSlotAtIndex(box, slot);
            if (current.Species != 0 && !new LegalityAnalysis(current).Valid)
            {
                var candidate = LegalizeKeepingOrigin(save, current);
                if (candidate.Species == current.Species && new LegalityAnalysis(candidate).Valid)
                {
                    if (box == -1)
                        save.SetPartySlotAtIndex(candidate, slot, EntityImportSettings.None);
                    else
                        save.SetBoxSlotAtIndex(candidate, box, slot, EntityImportSettings.None);
                    repaired++;
                }
                else
                {
                    stuck++;
                }
            }
            onProgress?.Invoke(i + 1, slots.Count);
        }

        return repaired switch
        {
            > 0 => new GenerationOutcome(true, $"Legalized {repaired} Pokémon." +
                (stuck > 0 ? $" {stuck} had no legal repair and were left untouched." : string.Empty)),
            0 when stuck > 0 => new GenerationOutcome(false,
                $"None of the {stuck} flagged Pokémon had a legal repair; nothing was written."),
            _ => new GenerationOutcome(false, "Nothing to legalize."),
        };
    }

    public GeneratedEntity? GenerateData(ISaveEngineSession session, GenerationRequest request) =>
        GenerateDataFromShowdown(session, BuildShowdownText(request, ((SaveEngineSession)session).SaveFile.Context),
            request.AllowUnsupportedSpecies);

    public GeneratedEntity? GenerateDataFromShowdown(ISaveEngineSession session, string showdownText,
        bool allowUnsupportedSpecies = false)
    {
        if (session is not SaveEngineSession engineSession) return null;
        var save = engineSession.SaveFile;

        var set = new ShowdownSet(showdownText);
        if (set.Species == 0) return null;
        if (set.Species > save.MaxSpeciesID)
        {
            if (!allowUnsupportedSpecies) return null;
            return BuildGeneratedEntity(BuildUnsupportedMon(save, set));
        }
        var result = GenerateLegal(engineSession, set);
        var created = result.Status is LegalizationResult.Regenerated ? ConvertForSave(save, result.Created) : null;
        if (allowUnsupportedSpecies && (created is null || !MatchesRequest(created, set, showdownText)))
            return BuildGeneratedEntity(BuildAsWritten(engineSession, set, showdownText, created));
        return created is null ? null : BuildGeneratedEntity(created);
    }

    private GeneratedEntity BuildGeneratedEntity(PKM created)
    {
        var data = new byte[created.SIZE_PARTY];
        created.WriteDecryptedDataParty(data);
        var info = new BankEntryInfo(
            created.Species, created.Form, created.IsShiny,
            created.IsNicknamed ? created.Nickname : _strings.specieslist[created.Species],
            created.CurrentLevel, created.Format, "Generated", EntityBytes.FormatOf(created), created.HeldItem, EntitySprite.Traits(created));
        return new GeneratedEntity(data, info);
    }

    public GenerationOutcome FillSpecies(ISaveEngineSession session, IReadOnlyList<int> species, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var placed = 0;
        foreach (var id in species)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = FindEmptySlot(save);
            if (slot is null)
                return placed > 0
                    ? new GenerationOutcome(true, $"Generated {placed}; storage is now full.")
                    : new GenerationOutcome(false, "No empty PC slots.");

            var name = _strings.specieslist[Math.Clamp(id, 1, _strings.specieslist.Length - 1)];
            var outcome = GenerateFromShowdown(session, slot.Value.Box, slot.Value.Slot, name);
            if (outcome.Success) placed++;
            onProgress?.Invoke(placed, species.Count);
        }
        return placed > 0
            ? new GenerationOutcome(true, $"Generated {placed} legal Pokémon into empty slots.")
            : new GenerationOutcome(false, "The legalizer could not generate any of those species in this game.");
    }

    /// <summary>Mass egg factory in the spirit of CDNRae's PKHeX bulk egg generator:
    /// a legal template per species, converted to an authentic egg state for the
    /// target generation, then placed into the first empty PC slots.</summary>
    public GenerationOutcome GenerateEggs(ISaveEngineSession session, IReadOnlyList<int> species, EggOptions options, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;
        if (save.Generation is < 3)
            return new GenerationOutcome(false, "Eggs are only supported from Gen 3 onward here.");

        var placed = 0;
        foreach (var id in species)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = FindEmptySlot(save);
            if (slot is null)
                return placed > 0
                    ? new GenerationOutcome(true, $"Generated {placed} eggs; storage is now full.")
                    : new GenerationOutcome(false, "No empty PC slots.");

            var name = _strings.specieslist[Math.Clamp(id, 1, _strings.specieslist.Length - 1)];
            var result = GenerateLegal(engineSession, new ShowdownSet(name));
            if (result.Status is not LegalizationResult.Regenerated)
                continue;
            var egg = result.Created;
            if (options.MaxIv)
            {
                egg.SetIVs(0x7FFF_FFFF); // six 31s in the 30-bit packed representation
            }
            if (options.Shiny && !egg.IsShiny)
                egg.SetShiny();

            egg.Nickname = "Egg";
            egg.IsNicknamed = true;
            egg.OriginalTrainerFriendship = (byte)EggStateLegality.GetMinimumEggHatchCycles(egg);
            egg.MetLocation = 0;
            if (save.Generation == 4)
            {
                egg.IsNicknamed = false;
                egg.Version = save.Context.GetSingleGameVersion();
                egg.EggLocation = 2000; // Daycare
            }
            egg.IsEgg = true;
            egg.RefreshChecksum();
            save.SetBoxSlotAtIndex(egg, slot.Value.Box, slot.Value.Slot, EntityImportSettings.None);
            placed++;
            onProgress?.Invoke(placed, species.Count);
        }
        return placed > 0
            ? new GenerationOutcome(true, $"Generated {placed} eggs into empty slots.")
            : new GenerationOutcome(false, "The legalizer could not generate any of those species in this game.");
    }

    private static (int Box, int Slot)? FindEmptySlot(SaveFile save)
    {
        for (var box = 0; box < save.BoxCount; box++)
        for (var slot = 0; slot < save.BoxSlotCount; slot++)
            if (save.GetBoxSlotAtIndex(box, slot).Species == 0)
                return (box, slot);
        return null;
    }

    private APILegality.AsyncLegalizationResult GenerateLegal(SaveEngineSession session, ShowdownSet set)
    {
        lock (TrainerGenerationLock)
        {
            var save = session.SaveFile;
            // Auto-Legality has no encounter/evolution tables for Luminescent's
            // distinct context yet. Its save layout and trainer identity are BDSP,
            // so generate through an isolated retail-BDSP view and place the result
            // back into the real Luminescent session. This keeps the generator usable
            // without teaching AutoMod that mod-specific encounters are official.
            var generationSave = save is SAV8BSLuminescent
                ? new SAV8BS(save.Data.ToArray())
                : save;
            var previousPriority = APILegality.GameVersionPriority;
            var previousOrder = APILegality.PriorityOrder;
            var previousGroups = EncounterMovesetGenerator.PriorityList;
            var useOwner = _ownershipSettings?.UseCurrentTrainerForGeneration ?? true;
            try
            {
                // Auto-Legality tries eggs first, and an egg fits any request (any PID, any
                // shiny), so every generated Pokémon came out hatched. A created Pokémon is
                // looked for as a catch, a gift or a trade first; eggs only when nothing else fits.
                EncounterMovesetGenerator.PriorityList =
                    CatchesFirst;

                if (save is SAV8BSLuminescent)
                    return generationSave.GetLegalFromSet(set);

                if (!useOwner)
                    return save.GetLegalFromSet(set);

                APILegality.GameVersionPriority = GameVersionPriorityType.PriorityOrder;

                var eligible = GameUtil.GameVersions
                    .Where(z => generationSave.Generation < 3 || z.Generation >= 3)
                    .ToList();

                // Try the open game first: common species get a native encounter and an
                // exact trainer/version match. Some species are transfer-only, so keep a
                // legal fallback that still carries the full modern trainer identity.
                APILegality.PriorityOrder = [generationSave.Version, .. eligible.Where(z => z != generationSave.Version)];
                var native = generationSave.GetLegalFromSet(set);
                var nativeOwned = TryOwn(session, native, out var ownedNative);
                if (nativeOwned && save.IsFromTrainer(ownedNative.Created))
                    return ownedNative;

                // A Gen 1/2 origin cannot carry a modern SID, and oldest-first search
                // steers transfer species toward ordinary catchable encounters instead
                // of fixed-OT distributions.
                APILegality.PriorityOrder = [.. eligible.OrderBy(z => z)];
                var transfer = generationSave.GetLegalFromSet(set);
                if (TryOwn(session, transfer, out var ownedTransfer))
                    return ownedTransfer;

                // Event-only species (Marshadow, Zeraora, Diancie...) have no
                // player-OT origin in any version: their only legal form is the
                // distribution itself. Keep the authentic event OT rather than
                // failing a request the legalizer actually satisfied.
                if (nativeOwned)
                    return ownedNative;
                if (native.Status is LegalizationResult.Regenerated)
                    return native;
                if (transfer.Status is LegalizationResult.Regenerated)
                    return transfer;
                return native with { Status = LegalizationResult.Failed };
            }
            finally
            {
                APILegality.GameVersionPriority = previousPriority;
                APILegality.PriorityOrder = previousOrder;
                EncounterMovesetGenerator.PriorityList = previousGroups;
            }
        }
    }

    private static bool TryOwn(SaveEngineSession session, APILegality.AsyncLegalizationResult result,
        out APILegality.AsyncLegalizationResult owned)
    {
        // The stamp mutates in place; work on a clone so a rejected stamp (fixed-OT
        // event mon, or a rewrite that turns out illegal) leaves the pristine
        // legal result usable for the event-OT fallback above.
        owned = result with { Created = result.Created.Clone() };
        return owned.Status is LegalizationResult.Regenerated && session.MakeOwned(owned.Created, null, out _);
    }

    private static string GameName(SaveFile save)
    {
        var index = (int)save.Version;
        var names = GameInfo.GetStrings("en").gamelist;
        return index > 0 && index < names.Length && names[index].Length > 0 ? names[index] : save.Version.ToString();
    }

    private static PKM ConvertForSave(SaveFile save, PKM created)
    {
        if (save is not SAV8BSLuminescent || created is PB8LUMI)
            return created;

        var data = new byte[created.SIZE_PARTY];
        created.WriteDecryptedDataParty(data);
        return new PB8LUMI(data);
    }


    public GenerationOutcome FillLivingDex(ISaveEngineSession session, byte[] compressedBundle, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        if (compressedBundle is not { Length: > 0 })
            return new GenerationOutcome(false, "No living dex bundle for this game - nothing written.");

        var save = engineSession.SaveFile;
        var capacity = save.BoxCount * save.BoxSlotCount;
        var placed = engineSession.PlaceLivingDex(compressedBundle);
        onProgress?.Invoke(placed, capacity);
        return placed == 0
            ? new GenerationOutcome(false, "The bundle held no compatible Pokémon; nothing was written.")
            : new GenerationOutcome(true, $"Living dex: {placed} Pokémon placed.");
    }

    /// <summary>Builds standard Showdown-format text from the wizard's structured request.</summary>
    private string BuildShowdownText(GenerationRequest request, EntityContext context)
    {
        var text = new StringBuilder();
        var speciesName = _strings.specieslist[request.Species];
        // Showdown names spell the Nidoran pair with a suffix; the gender sign alone
        // misparses as the wrong sibling.
        if (request.Species is (int)PKHeX.Core.Species.NidoranM) speciesName = "Nidoran-M";
        else if (request.Species is (int)PKHeX.Core.Species.NidoranF) speciesName = "Nidoran-F";
        if (request.Form > 0)
        {
            // "Rotom" + "-" + "Wash" => "Rotom-Wash"; the showdown parser matches form
            // names ignoring case and dash/space differences, so the display name works.
            var forms = FormConverter.GetFormList((ushort)request.Species, _strings.Types, _strings.forms, context);
            if (request.Form < forms.Length && forms[request.Form].Length > 0)
                speciesName = $"{speciesName}-{ShowdownParsing.GetShowdownFormName((ushort)request.Species, forms[request.Form])}";
        }
        text.AppendLine(speciesName);
        if (request.Level is { } level)
            text.AppendLine($"Level: {Math.Clamp(level, 1, 100)}");
        if (request.Shiny)
            text.AppendLine("Shiny: Yes");
        if (request.Nature is { } nature && nature < _strings.natures.Length)
            text.AppendLine($"{_strings.natures[nature]} Nature");
        if (request.Ability is { } ability && ability < _strings.abilitylist.Length)
            text.AppendLine($"Ability: {_strings.abilitylist[ability]}");
        if (request.Ball is { } ball && ball < _strings.balllist.Length)
            text.AppendLine($"Ball: {_strings.balllist[ball]}");
        foreach (var move in request.Moves ?? [])
        {
            if (move > 0 && move < _strings.movelist.Length)
                text.AppendLine($"- {_strings.movelist[move]}");
        }
        return text.ToString();
    }
}
