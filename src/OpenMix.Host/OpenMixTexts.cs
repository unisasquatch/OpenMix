/// <summary>OpenMix's own about, privacy and licence pages (Settings). The original game's EULA, privacy policy and
/// credits are separate: they come from the user's copy and are labelled as historical documents of the original game.</summary>
static class OpenMixTexts
{
    public static string? Get(string name) => name switch
    {
        "openmix-about" => About,
        "openmix-privacy" => Privacy,
        "openmix-licenses" => Licences(),
        _ => null,
    };

    const string About = """
OPENMIX

OpenMix is an unofficial, fan-made Windows player for DropMix boards and cards.

OpenMix is not affiliated with, endorsed by, or connected to Hasbro or Harmonix. "DropMix" is a trademark of its
owners and is used only to say what OpenMix works with.

OpenMix ships with none of the original game's content. All of the game's art, sounds, music, text and settings that
OpenMix uses were imported on this PC from your own copy of DropMix during setup, or downloaded for the cards in your
collection when you asked for it. OpenMix's own screens, such as setup and these pages, are part of OpenMix.

The DropMix EULA, privacy policy and credits listed in Settings are the original game's documents, imported from
your copy for reference. They were written for the original app and its online services, not for OpenMix.
""";

    const string Privacy = """
OPENMIX PRIVACY

On this PC: everything OpenMix imports, downloads or saves (game content, your card collection, saved mixes,
settings) stays in %LOCALAPPDATA%\OpenMix.

On your network: phones on the same Wi-Fi can open the game page to play. Only the OpenMix window on this PC can
change settings, saved mixes, your collection or card content.

On the internet: OpenMix contacts one service, the original game's card server, and only after you choose
Settings > Card Content > Download My Collection. It first asks for the download size and shows it to you, then
downloads only after you confirm. Both requests send the card ID numbers of the cards in your collection, as the
original app did, along with your internet address as with any web request. OpenMix has no accounts, analytics,
advertising or telemetry.
""";

    static string Licences()
    {
        var dir = AppContext.BaseDirectory;
        var parts = new List<string> { "OPENMIX LICENCES\n\nOpenMix is free software under the GNU General Public License, version 3 (GPL-3.0)." };
        foreach (var f in new[] { "THIRD_PARTY.md", "LICENSE" })
        {
            var p = Path.Combine(dir, f);
            if (File.Exists(p)) parts.Add(File.ReadAllText(p));
        }
        if (parts.Count == 1) parts.Add("The licence texts are installed with OpenMix (LICENSE, THIRD_PARTY.md and the licenses folder).");
        return string.Join("\n\n", parts);
    }
}
