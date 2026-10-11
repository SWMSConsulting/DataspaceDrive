namespace DataspaceDrive.Core.Files;

/// <summary>Regeln für Datei- und Ordnernamen. Eindeutigkeit gilt je Ordner und ohne Groß-/Kleinschreibung.</summary>
public static class ItemName
{
    public const int MaxLength = 255;

    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    public static string Validate(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new InvalidInputException("Der Name darf nicht leer sein.");
        if (trimmed.Length > MaxLength)
            throw new InvalidInputException($"Der Name darf höchstens {MaxLength} Zeichen lang sein.");
        if (trimmed is "." or ".." || trimmed.EndsWith('.'))
            throw new InvalidInputException("Der Name darf nicht mit einem Punkt enden.");
        if (trimmed.IndexOfAny(Forbidden) >= 0 || trimmed.Any(char.IsControl))
            throw new InvalidInputException("Der Name enthält unzulässige Zeichen (/ \\ : * ? \" < > |).");
        return trimmed;
    }

    public static string Normalize(string validName) => validName.ToUpperInvariant();

    /// <summary>„Bericht.pdf“ → „Bericht (2).pdf“, „Ordner“ → „Ordner (2)“.</summary>
    public static string WithCounter(string name, int counter)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0
            ? $"{name[..dot]} ({counter}){name[dot..]}"
            : $"{name} ({counter})";
    }
}
