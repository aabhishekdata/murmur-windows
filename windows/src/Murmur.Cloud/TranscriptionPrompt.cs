using System.Text;

namespace Murmur.Cloud;

/// <summary>Builds the <c>prompt</c> field from the user's dictionary terms.</summary>
/// <remarks>
/// <para>
/// The prompt is the cloud model's equivalent of Parakeet's hotword biasing: a list of the
/// spellings the user cares about, which makes the model far likelier to write "Kubernetes"
/// than "Cooper Netties". The correction pass still runs afterwards and is what guarantees the
/// spelling; this only makes it need to do less.
/// </para>
/// <para>
/// Capped at <see cref="MaxLength"/> characters. The model reads only the last couple of
/// hundred tokens of a prompt, and a whole phrase is dropped rather than cut in half — half a
/// term teaches the model a spelling that does not exist.
/// </para>
/// </remarks>
public static class TranscriptionPrompt
{
    /// <summary>The longest prompt sent.</summary>
    public const int MaxLength = 800;

    private const string Lead = "Vocabulary: ";

    /// <summary>Builds the prompt.</summary>
    /// <param name="biasPhrases">Dictionary terms, in priority order.</param>
    /// <param name="languageHint">A sentence about the language, or null.</param>
    /// <returns>The prompt, or null when there is nothing worth sending.</returns>
    public static string? Build(IReadOnlyList<string>? biasPhrases, string? languageHint = null)
    {
        var hint = languageHint?.Trim();
        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(hint))
        {
            builder.Append(hint.Length > MaxLength ? hint[..MaxLength] : hint);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        var budget = MaxLength - builder.Length - (builder.Length > 0 ? 1 : 0) - Lead.Length - 1;

        foreach (var phrase in biasPhrases ?? [])
        {
            var term = phrase?.Trim();
            if (string.IsNullOrEmpty(term) || !seen.Add(term)) continue;

            var cost = term.Length + (terms.Count > 0 ? 2 : 0);
            if (cost > budget) break;

            terms.Add(term);
            budget -= cost;
        }

        if (terms.Count > 0)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(Lead).AppendJoin(", ", terms).Append('.');
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
