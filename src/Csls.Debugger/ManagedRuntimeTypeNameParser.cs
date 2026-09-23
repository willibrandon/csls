using Csls.Debugger.Contracts;
using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Parses bounded C#, Visual Basic, and F# type names into CLR runtime identities.
/// </summary>
internal sealed class ManagedRuntimeTypeNameParser
{
    private const int MaximumDepth = 32;
    private const int MaximumLength = 4096;
    private const int MaximumArrayRank = 32;
    private const int MaximumTypeArgumentCount = 64;
    private readonly DebugExpressionLanguage _language;
    private readonly string _text;
    private int _position;
    private int _typeArgumentCount;

    private ManagedRuntimeTypeNameParser(
        string text,
        DebugExpressionLanguage language)
    {
        _text = text;
        _language = language;
    }

    /// <summary>
    /// Parses one complete compiler-lowered managed runtime type name.
    /// </summary>
    /// <param name="text">The compiler-lowered type name.</param>
    /// <param name="language">The source language that produced the name.</param>
    /// <returns>The normalized recursive runtime type reference.</returns>
    internal static ManagedRuntimeTypeReference Parse(
        string text,
        DebugExpressionLanguage language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaximumLength)
        {
            throw new NotSupportedException(
                $"Managed runtime type names support at most {MaximumLength} characters.");
        }

        var parser = new ManagedRuntimeTypeNameParser(text, language);
        ManagedRuntimeTypeReference result = parser.ParseType(depth: 0);
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw parser.InvalidTypeName("unexpected trailing syntax");
        }

        return result;
    }

    private bool AtEnd => _position >= _text.Length;

    private ManagedRuntimeTypeReference ParseType(int depth)
    {
        if (depth >= MaximumDepth)
        {
            throw new NotSupportedException(
                $"Managed runtime type names support at most {MaximumDepth} nested levels.");
        }

        SkipWhitespace();
        ConsumeGlobalQualifier();
        var metadataSegments = new List<string>();
        var displaySegments = new List<string>();
        var typeArguments = new List<ManagedRuntimeTypeReference>();
        var separators = new List<char>();
        while (true)
        {
            string identifier = ParseIdentifier();
            IReadOnlyList<ManagedRuntimeTypeReference> segmentArguments =
                ParseGenericArguments(depth);
            metadataSegments.Add(segmentArguments.Count == 0
                ? identifier
                : $"{identifier}`{segmentArguments.Count.ToString(CultureInfo.InvariantCulture)}");
            displaySegments.Add(segmentArguments.Count == 0
                ? identifier
                : $"{identifier}<{string.Join(',', segmentArguments.Select(
                    static argument => argument.DebuggerTypeName))}>");
            typeArguments.AddRange(segmentArguments);

            SkipWhitespace();
            if (AtEnd || (_text[_position] != '.' && _text[_position] != '+'))
            {
                break;
            }

            separators.Add(_text[_position++]);
            SkipWhitespace();
        }

        string metadataName = JoinSegments(metadataSegments, separators);
        string debuggerTypeName = JoinSegments(displaySegments, separators);
        if (typeArguments.Count == 0 &&
            ManagedRuntimeTypeAliases.TryNormalize(
                metadataName,
                _language,
                out string aliasMetadataName,
                out string alias))
        {
            metadataName = aliasMetadataName;
            debuggerTypeName = alias;
        }

        var result = new ManagedRuntimeTypeReference(
            metadataName,
            typeArguments.ToArray(),
            Array.Empty<int>(),
            debuggerTypeName);
        return ParseSuffixes(result, depth);
    }

    private ManagedRuntimeTypeReference ParseSuffixes(
        ManagedRuntimeTypeReference result,
        int depth)
    {
        while (true)
        {
            SkipWhitespace();
            if (TryConsume('?'))
            {
                result = new ManagedRuntimeTypeReference(
                    "System.Nullable`1",
                    [result],
                    Array.Empty<int>(),
                    $"{result.DebuggerTypeName}?");
                continue;
            }

            if (!TryParseArrayRank(out int rank))
            {
                return result;
            }

            if (depth + result.ArrayRanks.Count >= MaximumDepth)
            {
                throw new NotSupportedException(
                    $"Managed runtime type names support at most {MaximumDepth} nested levels.");
            }

            result = result with
            {
                ArrayRanks = [.. result.ArrayRanks, rank],
                DebuggerTypeName =
                    $"{result.DebuggerTypeName}[{new string(',', rank - 1)}]"
            };
        }
    }

    private IReadOnlyList<ManagedRuntimeTypeReference> ParseGenericArguments(int depth)
    {
        SkipWhitespace();
        char closing;
        if (TryConsume('<'))
        {
            closing = '>';
        }
        else if (_language == DebugExpressionLanguage.VisualBasic &&
            TryConsumeVisualBasicGenericStart())
        {
            closing = ')';
        }
        else
        {
            return Array.Empty<ManagedRuntimeTypeReference>();
        }

        var arguments = new List<ManagedRuntimeTypeReference>();
        while (true)
        {
            if (++_typeArgumentCount > MaximumTypeArgumentCount)
            {
                throw new NotSupportedException(
                    $"Managed runtime type names support at most " +
                    $"{MaximumTypeArgumentCount} generic arguments.");
            }

            arguments.Add(ParseType(depth + 1));
            SkipWhitespace();
            if (TryConsume(closing))
            {
                return arguments;
            }

            if (!TryConsume(','))
            {
                throw InvalidTypeName($"expected ',' or '{closing}'");
            }
        }
    }

    private bool TryParseArrayRank(out int rank)
    {
        rank = 0;
        if (AtEnd)
        {
            return false;
        }

        char opening = _text[_position];
        char closing = opening switch
        {
            '[' => ']',
            '(' when _language == DebugExpressionLanguage.VisualBasic => ')',
            _ => '\0'
        };
        if (closing == '\0')
        {
            return false;
        }

        int savedPosition = _position++;
        SkipWhitespace();
        if (opening == '(' && StartsWithWord("Of"))
        {
            _position = savedPosition;
            return false;
        }

        rank = 1;
        while (TryConsume(','))
        {
            if (++rank > MaximumArrayRank)
            {
                throw new NotSupportedException(
                    $"Managed runtime arrays support at most {MaximumArrayRank} dimensions.");
            }

            SkipWhitespace();
        }

        if (!TryConsume(closing))
        {
            throw InvalidTypeName($"expected '{closing}' in an array suffix");
        }

        return true;
    }

    private string ParseIdentifier()
    {
        SkipWhitespace();
        if (AtEnd)
        {
            throw InvalidTypeName("expected an identifier");
        }

        bool visualBasicEscaped = _language == DebugExpressionLanguage.VisualBasic &&
            _text[_position] == '[';
        if (visualBasicEscaped)
        {
            _position++;
        }

        int start = _position;
        if (!visualBasicEscaped && _text[_position] == '@')
        {
            start = ++_position;
        }

        if (AtEnd || !char.IsLetter(_text[_position]) && _text[_position] != '_')
        {
            throw InvalidTypeName("expected an identifier");
        }

        while (!AtEnd && (char.IsLetterOrDigit(_text[_position]) ||
            _text[_position] == '_'))
        {
            _position++;
        }

        if (_position == start)
        {
            throw InvalidTypeName("expected an identifier");
        }

        string identifier = _text[start.._position];
        if (visualBasicEscaped && !TryConsume(']'))
        {
            throw InvalidTypeName("expected ']' after an escaped identifier");
        }

        return identifier;
    }

    private void ConsumeGlobalQualifier()
    {
        const string qualifier = "global::";
        if (_text.AsSpan(_position).StartsWith(qualifier, StringComparison.Ordinal))
        {
            _position += qualifier.Length;
            return;
        }

        const string visualBasicQualifier = "Global.";
        if (_language == DebugExpressionLanguage.VisualBasic &&
            _text.AsSpan(_position).StartsWith(
                visualBasicQualifier,
                StringComparison.OrdinalIgnoreCase))
        {
            _position += visualBasicQualifier.Length;
        }
    }

    private bool TryConsumeVisualBasicGenericStart()
    {
        int savedPosition = _position;
        if (!TryConsume('('))
        {
            return false;
        }

        SkipWhitespace();
        if (!StartsWithWord("Of"))
        {
            _position = savedPosition;
            return false;
        }

        _position += 2;
        SkipWhitespace();
        return true;
    }

    private bool StartsWithWord(string word)
    {
        StringComparison comparison = _language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        ReadOnlySpan<char> remaining = _text.AsSpan(_position);
        return remaining.StartsWith(word, comparison) &&
            (remaining.Length == word.Length ||
                !char.IsLetterOrDigit(remaining[word.Length]) &&
                remaining[word.Length] != '_');
    }

    private bool TryConsume(char value)
    {
        if (AtEnd || _text[_position] != value)
        {
            return false;
        }

        _position++;
        return true;
    }

    private void SkipWhitespace()
    {
        while (!AtEnd && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }

    private InvalidDataException InvalidTypeName(string reason) => new(
        $"Managed runtime type name '{_text}' is invalid at character {_position}: {reason}.");

    private static string JoinSegments(
        List<string> segments,
        List<char> separators)
    {
        var result = new System.Text.StringBuilder(segments[0]);
        for (int index = 1; index < segments.Count; index++)
        {
            _ = result.Append(separators[index - 1]);
            _ = result.Append(segments[index]);
        }

        return result.ToString();
    }
}
