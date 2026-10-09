using System.Globalization;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// What a TM, HM or TR teaches in one game. Item tables name them "TM01", "HM05", "TR12" or
/// "TM001", and each game numbers its own: Emerald's TM01 is Focus Punch, Black's is Hone Claws.
/// The shared item text mixes every generation and PokeAPI draws machines per type, never per
/// number, so both the effect line and the disc come from here.
/// </summary>
internal static class MachineItems
{
    /// <summary>The move <paramref name="itemName"/> teaches in this game; null for any other item,
    /// or a game whose machine table PKHeX does not carry (Gen 6, Let's Go, Z-A).</summary>
    public static ushort? Move(string itemName, EntityContext context, GameVersion version)
    {
        if (itemName.Length < 4 || itemName[..2] is not ("TM" or "HM" or "TR")) return null;
        var digits = itemName.AsSpan(2);
        foreach (var ch in digits)
            if (!char.IsAsciiDigit(ch)) return null;
        var number = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (Tables(context, version) is not { } tables) return null;
        var (list, first) = itemName[..2] switch
        {
            "TM" => (tables.Technical, tables.FirstTechnical),
            "HM" => (tables.Hidden, 1),
            _ => (tables.Records, 0),
        };
        var index = number - first;
        return (uint)index < (uint)list.Length ? list[index] : null;
    }

    /// <summary>
    /// The art name of a machine: "TM Fire" slugs to PokeAPI's tm-fire disc. PokeAPI draws HMs
    /// in four types only (every HM move is one of them); TRs have no art there, so they keep their name.
    /// </summary>
    public static string? ArtName(string itemName, EntityContext context, GameVersion version)
    {
        if (itemName.StartsWith("TR", StringComparison.Ordinal) || Move(itemName, context, version) is not { } move) return null;
        var type = MoveInfo.GetType(move, context);
        if (type >= TypeFacts.Count) return null;
        var hidden = itemName.StartsWith("HM", StringComparison.Ordinal) && type is 0 or 1 or 2 or 10; // Normal, Fighting, Flying, Water
        return (hidden ? "HM " : "TM ") + TypeFacts.Names[type];
    }

    private sealed record MachineTables(ushort[] Technical, int FirstTechnical, ushort[] Hidden, ushort[] Records);

    private static MachineTables? Tables(EntityContext context, GameVersion version) => context switch
    {
        // Gen 1-2 and 5 list the HMs straight after the TMs.
        EntityContext.Gen1 => Split(Widen(PersonalInfo1.MachineMoves), 50),
        EntityContext.Gen2 => Split(Widen(PersonalInfo2.MachineMoves), 50),
        EntityContext.Gen3 => new(PersonalInfo3.MachineMovesTechnical.ToArray(), 1, PersonalInfo3.MachineMovesHidden.ToArray(), []),
        EntityContext.Gen4 => new(PersonalInfo4.MachineMovesTechnical.ToArray(), 1,
            (version is GameVersion.HG or GameVersion.SS ? PersonalInfo4.MachineMovesHiddenHGSS : PersonalInfo4.MachineMovesHiddenDPPt).ToArray(), []),
        EntityContext.Gen5 => Split(PersonalInfo5BW.MachineMoves.ToArray(), 95),
        EntityContext.Gen7 => new(PersonalInfo7.MachineMoves.ToArray(), 1, [], []),
        // Sword/Shield start at TM00 and TR00; Scarlet/Violet's list is indexed by TM number too.
        EntityContext.Gen8 => new(PersonalInfo8SWSH.MachineMovesTechnical.ToArray(), 0, [], PersonalInfo8SWSH.MachineMovesRecord.ToArray()),
        EntityContext.Gen8b => new(PersonalInfo8BDSP.MachineMoves.ToArray(), 1, [], []),
        EntityContext.Gen9 => new(PersonalInfo9SV.MachineMoves.ToArray(), 0, [], []),
        _ => null,
    };

    private static MachineTables Split(ushort[] moves, int technical) => new(moves[..technical], 1, moves[technical..], []);

    private static ushort[] Widen(ReadOnlySpan<byte> moves)
    {
        var wide = new ushort[moves.Length];
        for (var i = 0; i < moves.Length; i++) wide[i] = moves[i];
        return wide;
    }
}
