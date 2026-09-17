using System.Globalization;
using System.Text;

namespace McpGateway.Connectors.Fhir.Indexing;

/// <summary>
/// Turns a caller's keyword string into a safe FTS5 <c>MATCH</c> expression.
///
/// FTS5 has its own query language: bare input can carry operators (<c>AND</c>, <c>NOT</c>,
/// <c>NEAR</c>), column filters (<c>title:</c>), prefix stars and quotes. Passing a caller's
/// string straight through is the FTS equivalent of SQL injection — at best a syntax error that
/// fails the search, at worst a query that reads columns the caller did not intend. Every term is
/// therefore quoted and the operators are ours, not the caller's.
/// </summary>
public static class FtsQueryBuilder
{
    /// <summary>Longest single term kept; longer runs are truncated rather than rejected.</summary>
    public const int MaxTermLength = 64;

    /// <summary>Most terms honoured from one query.</summary>
    public const int MaxTerms = 32;

    /// <summary>
    /// Builds a MATCH expression requiring every term (AND). Quoted phrases in the caller's input
    /// are preserved as phrases.
    /// </summary>
    /// <returns>The MATCH expression, or null when the input holds no usable term.</returns>
    public static string? Build(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var terms = Tokenize(query);
        if (terms.Count == 0)
            return null;

        var builder = new StringBuilder();
        foreach (var term in terms)
        {
            if (builder.Length > 0)
                builder.Append(" AND ");

            // Doubling embedded quotes is FTS5's own escape for a quoted string.
            builder.Append('"').Append(term.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits input into searchable terms: quoted runs stay together as phrases, everything else
    /// splits on non-alphanumeric characters so punctuation and FTS operators cannot leak through.
    /// </summary>
    internal static List<string> Tokenize(string query)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in query)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                if (!inQuotes)
                    Flush(terms, current);

                continue;
            }

            var keep = inQuotes
                ? !char.IsControl(character)
                : char.IsLetterOrDigit(character) || character == '-' || character == '_';

            if (keep)
            {
                if (current.Length < MaxTermLength)
                    current.Append(character);
            }
            else
            {
                Flush(terms, current);
            }

            if (terms.Count >= MaxTerms)
                return terms;
        }

        Flush(terms, current);
        return terms;
    }

    private static void Flush(List<string> terms, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        var term = current.ToString().Trim();
        current.Clear();

        // A term of only separators carries no meaning and would produce an empty FTS phrase.
        if (term.Length > 0 && term.Any(char.IsLetterOrDigit))
            terms.Add(term.ToLower(CultureInfo.InvariantCulture));
    }
}
