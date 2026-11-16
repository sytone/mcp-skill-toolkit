using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace McpCli;

// AJV compiles schema patterns with JavaScript's Unicode flag. .NET regex consumes
// UTF-16 code units, so character atoms must be expanded before applying quantifiers.
internal static class SchemaRegex
{
    private readonly record struct Atom(Predicate<int> Matches, int? Literal = null);

    public static Regex Compile(string pattern)
    {
        var result = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            if (pattern[i] == '[')
            {
                i++;
                var negate = i < pattern.Length && pattern[i] == '^';
                if (negate) i++;
                var atoms = new List<Atom>();
                while (i < pattern.Length && pattern[i] != ']')
                {
                    var atom = ReadAtom(pattern, ref i);
                    if (i + 1 < pattern.Length && pattern[i] == '-' && pattern[i + 1] != ']')
                    {
                        i++;
                        var end = ReadAtom(pattern, ref i);
                        if (atom.Literal is not { } from || end.Literal is not { } to || from > to)
                            throw new InvalidDataException("Invalid output schema pattern character range");
                        atom = new(point => point >= from && point <= to);
                    }
                    atoms.Add(atom);
                }
                if (i == pattern.Length) throw new InvalidDataException("Unterminated output schema pattern character class");
                i++;
                result.Append(ScalarSet(point => atoms.Any(atom => atom.Matches(point)) != negate));
            }
            else if (pattern[i] == '.')
            {
                result.Append(ScalarSet(point => point is not ('\r' or '\n' or 0x2028 or 0x2029)));
                i++;
            }
            else if (pattern[i] == '$')
            {
                result.Append(@"\z");
                i++;
            }
            else if (pattern[i] == '\\' && i + 1 < pattern.Length && "pPuDsSwWd".Contains(pattern[i + 1]))
            {
                var atom = ReadAtom(pattern, ref i);
                result.Append(ScalarSet(atom.Matches));
            }
            else if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                result.Append(pattern[i++]).Append(pattern[i++]);
            }
            else if (char.IsSurrogate(pattern[i]))
            {
                var atom = ReadAtom(pattern, ref i);
                result.Append(ScalarSet(atom.Matches));
            }
            else result.Append(pattern[i++]);
        }
        try { return new Regex(result.ToString(), RegexOptions.ECMAScript | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException error) { throw new InvalidDataException($"Invalid output schema pattern: {error.Message}", error); }
    }

    private static Atom ReadAtom(string pattern, ref int position)
    {
        var value = (int)pattern[position++];
        if (value == '\\')
        {
            if (position == pattern.Length) throw new InvalidDataException("Invalid output schema pattern escape");
            var escape = pattern[position++];
            switch (escape)
            {
                case 'p' or 'P':
                    if (position == pattern.Length || pattern[position++] != '{')
                        throw new InvalidDataException("Invalid output schema Unicode property escape");
                    var end = pattern.IndexOf('}', position);
                    if (end < 0) throw new InvalidDataException("Unterminated output schema Unicode property escape");
                    var property = Category(pattern[position..end]);
                    position = end + 1;
                    return new(point => property(point) == (escape == 'p'));
                case 'd' or 'D': return new(point => (point is >= '0' and <= '9') == (escape == 'd'));
                case 'w' or 'W':
                    return new(point => (point is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') == (escape == 'w'));
                case 's' or 'S':
                    return new(point => (point is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or 0xA0 or 0x1680 or
                        >= 0x2000 and <= 0x200A or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF) == (escape == 's'));
                case 'u':
                    if (position < pattern.Length && pattern[position] == '{')
                    {
                        var close = pattern.IndexOf('}', ++position);
                        if (close < 0) throw new InvalidDataException("Unterminated output schema code point escape");
                        value = Hex(pattern[position..close]);
                        position = close + 1;
                        if (value > 0x10FFFF) throw new InvalidDataException("Invalid output schema code point");
                    }
                    else value = ReadHex(pattern, ref position, 4);
                    break;
                case 'x': value = ReadHex(pattern, ref position, 2); break;
                case 'n': value = '\n'; break;
                case 'r': value = '\r'; break;
                case 't': value = '\t'; break;
                case 'v': value = '\v'; break;
                case 'f': value = '\f'; break;
                case 'b': value = '\b'; break;
                case '0': value = 0; break;
                case 'c':
                    if (position == pattern.Length || !char.IsAsciiLetter(pattern[position]))
                        throw new InvalidDataException("Invalid output schema control escape");
                    value = char.ToUpperInvariant(pattern[position++]) % 32;
                    break;
                default: value = escape; break;
            }
        }
        if (value is >= 0xD800 and <= 0xDBFF && position < pattern.Length)
        {
            var next = position;
            int low;
            if (pattern[next] == '\\' && next + 5 < pattern.Length && pattern[next + 1] == 'u' && pattern[next + 2] != '{')
            {
                next += 2;
                low = ReadHex(pattern, ref next, 4);
            }
            else low = pattern[next++];
            if (low is >= 0xDC00 and <= 0xDFFF)
            {
                value = 0x10000 + (value - 0xD800) * 1024 + low - 0xDC00;
                position = next;
            }
        }
        return new(point => point == value, value);
    }

    private static int ReadHex(string pattern, ref int position, int length)
    {
        if (position + length > pattern.Length) throw new InvalidDataException("Invalid output schema hexadecimal escape");
        var result = Hex(pattern.Substring(position, length));
        position += length;
        return result;
    }

    private static int Hex(string value) =>
        value.Length > 0 && int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number) && number >= 0 ?
            number : throw new InvalidDataException("Invalid output schema hexadecimal escape");

    private static Predicate<int> Category(string name)
    {
        if (name.StartsWith("General_Category=", StringComparison.Ordinal)) name = name["General_Category=".Length..];
        else if (name.StartsWith("gc=", StringComparison.Ordinal)) name = name[3..];
        UnicodeCategory[] categories = name switch
        {
            "L" or "Letter" => [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter,
                UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter],
            "LC" or "Cased_Letter" => [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter],
            "M" or "Mark" => [UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark],
            "N" or "Number" => [UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber, UnicodeCategory.OtherNumber],
            "P" or "Punctuation" => [UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation,
                UnicodeCategory.ClosePunctuation, UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation],
            "S" or "Symbol" => [UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol, UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol],
            "Z" or "Separator" => [UnicodeCategory.SpaceSeparator, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator],
            "C" or "Other" => [UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned],
            _ => [SingleCategory(name)]
        };
        return point => categories.Contains(point is >= 0xD800 and <= 0xDFFF ?
            UnicodeCategory.Surrogate : Rune.GetUnicodeCategory(new Rune(point)));
    }

    private static UnicodeCategory SingleCategory(string name) => name switch
    {
        "Lu" or "Uppercase_Letter" => UnicodeCategory.UppercaseLetter,
        "Ll" or "Lowercase_Letter" => UnicodeCategory.LowercaseLetter,
        "Lt" or "Titlecase_Letter" => UnicodeCategory.TitlecaseLetter,
        "Lm" or "Modifier_Letter" => UnicodeCategory.ModifierLetter,
        "Lo" or "Other_Letter" => UnicodeCategory.OtherLetter,
        "Mn" or "Nonspacing_Mark" => UnicodeCategory.NonSpacingMark,
        "Mc" or "Spacing_Mark" => UnicodeCategory.SpacingCombiningMark,
        "Me" or "Enclosing_Mark" => UnicodeCategory.EnclosingMark,
        "Nd" or "Decimal_Number" => UnicodeCategory.DecimalDigitNumber,
        "Nl" or "Letter_Number" => UnicodeCategory.LetterNumber,
        "No" or "Other_Number" => UnicodeCategory.OtherNumber,
        "Pc" or "Connector_Punctuation" => UnicodeCategory.ConnectorPunctuation,
        "Pd" or "Dash_Punctuation" => UnicodeCategory.DashPunctuation,
        "Ps" or "Open_Punctuation" => UnicodeCategory.OpenPunctuation,
        "Pe" or "Close_Punctuation" => UnicodeCategory.ClosePunctuation,
        "Pi" or "Initial_Punctuation" => UnicodeCategory.InitialQuotePunctuation,
        "Pf" or "Final_Punctuation" => UnicodeCategory.FinalQuotePunctuation,
        "Po" or "Other_Punctuation" => UnicodeCategory.OtherPunctuation,
        "Sm" or "Math_Symbol" => UnicodeCategory.MathSymbol,
        "Sc" or "Currency_Symbol" => UnicodeCategory.CurrencySymbol,
        "Sk" or "Modifier_Symbol" => UnicodeCategory.ModifierSymbol,
        "So" or "Other_Symbol" => UnicodeCategory.OtherSymbol,
        "Zs" or "Space_Separator" => UnicodeCategory.SpaceSeparator,
        "Zl" or "Line_Separator" => UnicodeCategory.LineSeparator,
        "Zp" or "Paragraph_Separator" => UnicodeCategory.ParagraphSeparator,
        "Cc" or "Control" => UnicodeCategory.Control,
        "Cf" or "Format" => UnicodeCategory.Format,
        "Cs" or "Surrogate" => UnicodeCategory.Surrogate,
        "Co" or "Private_Use" => UnicodeCategory.PrivateUse,
        "Cn" or "Unassigned" => UnicodeCategory.OtherNotAssigned,
        _ => throw new InvalidDataException($"Unsupported output schema Unicode property: {name}")
    };

    private static string ScalarSet(Predicate<int> includes)
    {
        var alternatives = new List<string>();
        var bmp = CharacterClass(0, 0xFFFF, point => point is not (>= 0xD800 and <= 0xDFFF) && includes(point));
        if (bmp.Length != 0) alternatives.Add(bmp);
        var high = CharacterClass(0xD800, 0xDBFF, includes);
        var low = CharacterClass(0xDC00, 0xDFFF, includes);
        if (high.Length != 0) alternatives.Add(high + @"(?![\uDC00-\uDFFF])");
        if (low.Length != 0) alternatives.Add(@"(?<![\uD800-\uDBFF])" + low);
        var groupStart = 0xD800;
        var previous = "";
        for (var surrogate = 0xD800; surrogate <= 0xDC00; surrogate++)
        {
            var next = surrogate == 0xDC00 ? "" :
                CharacterClass(0xDC00, 0xDFFF, value => includes(0x10000 + (surrogate - 0xD800) * 1024 + value - 0xDC00));
            if (next == previous) continue;
            if (previous.Length != 0)
                alternatives.Add(CharacterClass(groupStart, surrogate - 1, _ => true) + previous);
            groupStart = surrogate;
            previous = next;
        }
        return alternatives.Count == 0 ? "(?!)" : "(?:" + string.Join('|', alternatives) + ")";
    }

    private static string CharacterClass(int first, int last, Predicate<int> includes)
    {
        var ranges = new StringBuilder();
        for (var point = first; point <= last; point++)
        {
            if (!includes(point)) continue;
            var start = point;
            while (point < last && includes(point + 1)) point++;
            ranges.Append(@"\u").Append(start.ToString("X4", CultureInfo.InvariantCulture));
            if (point != start) ranges.Append(@"-\u").Append(point.ToString("X4", CultureInfo.InvariantCulture));
        }
        return ranges.Length == 0 ? "" : "[" + ranges + "]";
    }
}
