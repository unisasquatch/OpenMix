using OpenMix.Board;

namespace OpenMix.Cards;

/// <summary>Card type, which is also its colour.</summary>
public enum CardType { Beat = 0, Bass = 1, Loop = 2, Lead = 3, Fx = 4, Wild = 5, Invalid = 6 }

public enum CardRarity { Common, Rare, Legendary }

/// <summary>Musical key: C=0 … B=11, None=12.</summary>
public enum MusicKey { C, Cs, D, Ds, E, F, Fs, G, Gs, A, As, B, None }

public enum MusicMode { Major, Minor, None }

/// <summary>A DropMix card, assembled from the bundled card/content/set CSVs. Fields
/// mirror the recovered <c>CardDatabase</c> columns; not every column is populated for
/// every card.</summary>
public sealed record Card
{
    public required string Cid { get; init; }            // e.g. s01_c001_i01_24kmagic_wild
    public string SourceCid { get; init; } = "";         // e.g. LIC_0058_Wild
    public ushort Cti { get; init; }                     // card type index (UID >> 48)
    public string Name { get; init; } = "";
    public string Artist { get; init; } = "";
    public CardType Type { get; init; } = CardType.Invalid;
    public CardRarity Rarity { get; init; } = CardRarity.Common;
    public MusicKey Key { get; init; } = MusicKey.None;
    public MusicMode Mode { get; init; } = MusicMode.None;
    public float Tempo { get; init; }
    public int Power { get; init; }
    public string[] Instruments { get; init; } = Array.Empty<string>();
    public string AudioId { get; init; } = "";           // card_audio song id
    // content_db extras (display): genres ("genre_pop genre_rnb"), release year, card illustrator, art-crop centre, song credits
    public string[] Genres { get; init; } = Array.Empty<string>();
    public string Year { get; init; } = "";
    public string Illustrator { get; init; } = "";
    public string ArtCrop { get; init; } = "";
    public string Credits { get; init; } = "";
    public string ImageName { get; init; } = "";

    /// <summary>Playlist (deck) icon, e.g. "sweets"; each icon is a 15-card playlist.</summary>
    public string Icon { get; init; } = "";

    // ---- from the content DB ----
    /// <summary>FX ability script, e.g. "instant:remove(Opponent,type{Bass})" (FX cards only).</summary>
    public string Ability { get; init; } = "";
    /// <summary>Localisation id of the card's rule text, e.g. "fx_clear_bass".</summary>
    public string ScreenText { get; init; } = "";
    /// <summary>Loop length in bars (32, 16 or 8).</summary>
    public int NumBars { get; init; }
    public string MusicEffect { get; init; } = "";
    public string Transition { get; init; } = "";
    public bool WildBeatHasKey { get; init; }
    /// <summary>The game's transposition table: semitones to shift this card when the mix is in
    /// key C..B (index 0..11); empty if the card has no table.</summary>
    public int?[] KeyShifts { get; init; } = Array.Empty<int?>();

    /// <summary>Semitones to move this card into <paramref name="mixKey"/> (0..11), or null if the
    /// card has no entry for that key.</summary>
    public int? ShiftFor(int mixKey) => KeyShifts.Length == 12 ? KeyShifts[((mixKey % 12) + 12) % 12] : null;

    public Cti CtiTyped => new(Cti);
    public override string ToString() => $"{Cid} \"{Name}\" [{Type}] CTI {Cti}";
}
