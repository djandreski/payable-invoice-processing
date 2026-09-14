using System.Text;

namespace InvoiceReviewAssistant.Infrastructure.Pdf;

/// <summary>
/// Applies the shared document-text normalization policy without changing letters,
/// digits, punctuation, or casing. This type is also used by the whole-document OCR
/// path so both sources reach extraction in the same canonical form.
/// </summary>
public static class DocumentTextNormalizer
{
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return string.Empty;
        }

        var whitespaceNormalized = new StringBuilder(text.Length);
        var previousWasCarriageReturn = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\r')
            {
                whitespaceNormalized.Append('\n');
                previousWasCarriageReturn = true;
                continue;
            }

            if (rune.Value == '\n')
            {
                if (!previousWasCarriageReturn)
                {
                    whitespaceNormalized.Append('\n');
                }

                previousWasCarriageReturn = false;
                continue;
            }

            previousWasCarriageReturn = false;
            if (IsUnicodeLineBreak(rune))
            {
                whitespaceNormalized.Append('\n');
            }
            else if (Rune.IsWhiteSpace(rune))
            {
                whitespaceNormalized.Append(' ');
            }
            else
            {
                whitespaceNormalized.Append(rune.ToString());
            }
        }

        return CollapseBlankLines(whitespaceNormalized.ToString());
    }

    public static string NormalizePages(IEnumerable<string> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return Normalize(string.Join("\n\n", pages.Select(Normalize)));
    }

    private static bool IsUnicodeLineBreak(Rune rune) => rune.Value is 0x0085 or 0x2028 or 0x2029;

    private static string CollapseBlankLines(string text)
    {
        var lines = text.Split('\n');
        var firstContentLine = 0;
        while (firstContentLine < lines.Length && string.IsNullOrWhiteSpace(lines[firstContentLine]))
        {
            firstContentLine++;
        }

        if (firstContentLine == lines.Length)
        {
            return string.Empty;
        }

        var lastContentLine = lines.Length - 1;
        while (lastContentLine >= firstContentLine && string.IsNullOrWhiteSpace(lines[lastContentLine]))
        {
            lastContentLine--;
        }

        var normalized = new StringBuilder(text.Length);
        var previousLineWasBlank = false;
        for (var index = firstContentLine; index <= lastContentLine; index++)
        {
            var currentLineIsBlank = string.IsNullOrWhiteSpace(lines[index]);
            if (currentLineIsBlank && previousLineWasBlank)
            {
                continue;
            }

            if (normalized.Length > 0)
            {
                normalized.Append('\n');
            }

            if (!currentLineIsBlank)
            {
                normalized.Append(lines[index]);
            }

            previousLineWasBlank = currentLineIsBlank;
        }

        return normalized.ToString();
    }
}
