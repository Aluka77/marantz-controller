using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MarantzController;

/// <summary>bool → Visibility, fordítva: true → Collapsed, false → Visible.
/// A kompakt nézethez: ha IsCompact=true, a teljes tartalom eltűnik.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visibility: true → Visible, false → Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Igaz, ha az aktuális érték (1.) egyezik a gombéval (2.). Az aktív gomb kiemeléséhez
/// használjuk DataTriggerben: forrásnál a token, Quick Selectnél a sorszám alapján.
/// </summary>
public sealed class SourceEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return false;
        var current = values[0] as string ?? "";
        var token = values[1] as string ?? "";
        return token.Length > 0 && string.Equals(current, token, StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Forrás-token → Segoe MDL2 Assets glyph. A kódpontok LÉTEZÉSÉT és JELENTÉSÉT
/// képre rendereléssel ellenőriztük (E958=lemez, EB77=set-top-box, E7F8=laptop,
/// E786=lejátszó-képernyő, E704=antenna, E97B=mikrofonállvány, E965=torony).
/// A glyph-eket kódpontból állítjuk elő, hogy a forrás tiszta ASCII maradjon.
/// </summary>
public sealed class TokenToGlyphConverter : IValueConverter
{
    private static string G(int cp) => char.ConvertFromUtf32(cp);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "TV" => G(0xE7F4),      // TV-monitor
            "SAT/CBL" => G(0xEB77), // set-top-box antennával
            "DVD" => G(0xE958),     // lemez
            "BD" => G(0xE958),
            "CD" => G(0xE958),
            "GAME" => G(0xE7FC),    // kontroller
            "MPLAY" => G(0xE786),   // képernyő play-jellel
            "NET" => G(0xE189),     // dupla hangjegy
            "TUNER" => G(0xE704),   // antenna
            "AUX1" => G(0xE7F8),    // laptop (Szamitogep)
            "AUX2" => G(0xE7F6),    // fejhallgató
            "8K" => G(0xE965),      // magas készülék
            "PHONO" => G(0xE97B),   // mikrofonállvány
            _ => G(0xE767),         // hangszóró (általános)
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>IsPlaying → MDL2 play/pause glyph a transport-főgombhoz.</summary>
public sealed class BoolToPlayPauseGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? char.ConvertFromUtf32(0xE769) : char.ConvertFromUtf32(0xE768); // pause : play

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Arány (0..1) + sávszélesség → a kitöltött rész szélessége pixelben.
/// A pozíció-sávhoz: a sáv tényleges ActualWidth-ét kötjük be 2. értékként.
/// </summary>
public sealed class FractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double frac || values[1] is not double total)
            return 0d;
        if (double.IsNaN(total) || total <= 0)
            return 0d;
        return Math.Clamp(frac, 0, 1) * total;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → kapcsolat-állapot pötty szín (zöld / szürke).</summary>
public sealed class ConnectionDotConverter : IValueConverter
{
    private static readonly Brush Connected = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
    private static readonly Brush Disconnected = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
