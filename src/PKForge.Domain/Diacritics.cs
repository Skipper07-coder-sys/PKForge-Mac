using System.Text;

namespace PKForge.Domain;

/// <summary>
/// Canonical decomposition that also works without ICU. The Mac and iPhone builds run in
/// invariant globalization mode, where <see cref="string.Normalize(NormalizationForm)"/> leaves
/// "é" whole, so every strip-the-accents loop kept it: "Poké Ball" slugged to "pok-ball" (no
/// sprite) and keyed "pokball" (no description). Real normalization runs first, so platforms
/// with ICU are unchanged; accented Latin letters it left whole become their base letter.
/// </summary>
public static class Diacritics
{
    // Every U+00C0-U+017F letter whose FormD is an ASCII letter plus combining marks (generated
    // with ICU), and that letter at the same index.
    private const string Accented =
        "ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöùúûüýÿĀāĂăĄąĆćĈĉĊċČčĎďĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥ" +
        "ĨĩĪīĬĭĮįİĴĵĶķĹĺĻļĽľŃńŅņŇňŌōŎŏŐőŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŨũŪūŬŭŮůŰűŲųŴŵŶŷŸŹźŻżŽž";
    private const string Bases =
        "AAAAAACEEEEIIIINOOOOOUUUUYaaaaaaceeeeiiiinooooouuuuyyAaAaAaCcCcCcCcDdEeEeEeEeEeGgGgGgGgHh" +
        "IiIiIiIiIJjKkLlLlLlNnNnNnOoOoOoRrRrRrSsSsSsSsTtTtUuUuUuUuUuUuWwYyYZzZzZz";

    /// <summary>
    /// <paramref name="text"/> in <paramref name="form"/> (FormD or FormKD), with any accented
    /// Latin letter left whole replaced by its base letter, as if its marks had been split off and dropped.
    /// </summary>
    public static string Decompose(string text, NormalizationForm form = NormalizationForm.FormD)
    {
        var decomposed = text.Normalize(form);
        if (!decomposed.AsSpan().ContainsAnyInRange('À', 'ſ')) return decomposed;
        var chars = decomposed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = BaseLetter(chars[i]);
        return new string(chars);
    }

    /// <summary>The ASCII letter under an accented Latin letter; any other character as it is.</summary>
    public static char BaseLetter(char c) => Accented.IndexOf(c) is >= 0 and var at ? Bases[at] : c;
}
