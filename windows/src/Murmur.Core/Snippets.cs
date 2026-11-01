using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>When a snippet's trigger counts as said.</summary>
public enum SnippetMatch
{
    /// <summary>
    /// Anywhere in a dictation: "send it to my address please" types the address mid-sentence.
    /// What every snippet did before saved prompts existed, so it is what an entry without the
    /// field loads as.
    /// </summary>
    Inline,

    /// <summary>
    /// Only when the trigger is the whole of what was said. The stored text is then pasted
    /// exactly as written — a saved prompt — and no other pass touches it.
    /// </summary>
    WholeUtterance,
}

/// <summary>One voice-triggered block of text.</summary>
public sealed record SnippetEntry
{
    /// <summary>Longest body a snippet may hold.</summary>
    /// <remarks>
    /// Matches the ceiling the reference product documents. It exists so a runaway paste into
    /// the editor cannot turn one dictation into a megabyte of synthetic keystrokes.
    /// </remarks>
    public const int MaxLength = 4000;

    /// <summary>Stable identity, so an entry can be edited or deleted unambiguously.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The spoken phrase that triggers the expansion.</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary>What gets typed instead. May be several lines.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Disabled snippets stay in the file but stop expanding.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Whether the trigger fires mid-sentence or only said on its own.</summary>
    /// <remarks>
    /// Defaults to <see cref="SnippetMatch.Inline"/> so a <c>snippets.jsonl</c> written before
    /// this existed keeps behaving exactly as it did. A prompt created in the editor starts as
    /// <see cref="SnippetMatch.WholeUtterance"/> instead; that choice is the editor's.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<SnippetMatch>))]
    public SnippetMatch Match { get; init; } = SnippetMatch.Inline;

    /// <summary>A key combination that pastes the text without speaking, or null for none.</summary>
    [JsonConverter(typeof(HotkeyChordJsonConverter))]
    public HotkeyChord? Hotkey { get; init; }

    /// <summary>A one-line preview of the body, for list rows.</summary>
    /// <param name="maxLength">Longest preview to produce.</param>
    /// <returns>The body with line breaks flattened, elided if long.</returns>
    public string Preview(int maxLength = 80)
    {
        var flat = string.Join(
            " ", Text.Split(SpeechText.Whitespace, StringSplitOptions.RemoveEmptyEntries));

        return flat.Length <= maxLength ? flat : flat[..Math.Max(0, maxLength - 1)] + "…";
    }
}

/// <summary>
/// Snippets, persisted as JSON Lines.
/// </summary>
/// <remarks>
/// <para>
/// Structurally the dictionary's twin — stored entries, a management view, a matching pass over
/// the transcript — and built to the same shape on purpose. What differs is the storage format:
/// the dictionary is a hand-editable one-entry-per-line text file, and a snippet body is up to
/// <see cref="SnippetEntry.MaxLength"/> characters of arbitrary multi-line text, which that
/// format cannot hold. So this follows <see cref="TranscriptStore"/> instead: one JSON object
/// per line, source-generated so it survives trimming and single-file publishing, and tolerant
/// of a corrupt line costing one entry rather than the file.
/// </para>
/// </remarks>
public sealed class SnippetStore
{
    private readonly string _path;
    private readonly List<SnippetEntry> _entries = [];

    /// <summary>Opens (and creates if needed) the snippets at <paramref name="path"/>.</summary>
    /// <param name="path">Where the file lives.</param>
    public SnippetStore(string path)
    {
        _path = path;
        Reload();
    }

    /// <summary>The default location, alongside the dictionary and the history.</summary>
    public static string DefaultPath => MurmurPaths.In("snippets.jsonl");

    /// <summary>Where this store lives on disk.</summary>
    public string FilePath => _path;

    /// <summary>Every snippet, in file order.</summary>
    public IReadOnlyList<SnippetEntry> Entries => _entries;

    /// <summary>Raised whenever the snippets change.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads the file, discarding in-memory state.</summary>
    public void Reload()
    {
        _entries.Clear();

        if (File.Exists(_path))
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // A single corrupt line must not destroy every snippet the user has written.
                try
                {
                    var entry = JsonSerializer.Deserialize(line, SnippetJsonContext.Default.SnippetEntry);
                    if (entry is not null) _entries.Add(entry);
                }
                catch (JsonException)
                {
                    // Skip.
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a snippet and saves.</summary>
    /// <param name="entry">The snippet to add.</param>
    public void Add(SnippetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Add(Clamp(entry));
        Save();
    }

    /// <summary>Replaces a snippet by id and saves.</summary>
    /// <param name="entry">The snippet to store in place of the existing one.</param>
    public void Update(SnippetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var index = _entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0) return;

        _entries[index] = Clamp(entry);
        Save();
    }

    /// <summary>Removes a snippet and saves.</summary>
    /// <param name="id">Which snippet to remove.</param>
    public void Remove(Guid id)
    {
        _entries.RemoveAll(e => e.Id == id);
        Save();
    }

    /// <summary>Case-insensitive search across trigger and body.</summary>
    /// <param name="query">What to look for.</param>
    /// <returns>Matching snippets, or all of them for an empty query.</returns>
    public IReadOnlyList<SnippetEntry> Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var trimmed = query.Trim();
        if (trimmed.Length == 0) return _entries;

        return _entries.Where(e =>
            e.Trigger.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            e.Text.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static SnippetEntry Clamp(SnippetEntry entry) =>
        entry.Text.Length <= SnippetEntry.MaxLength
            ? entry
            : entry with { Text = entry.Text[..SnippetEntry.MaxLength] };

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var lines = _entries.Select(e =>
            JsonSerializer.Serialize(e, SnippetJsonContext.Default.SnippetEntry));

        File.WriteAllLines(_path, lines);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Source-generated JSON for snippets.</summary>
/// <remarks>
/// Reflection-based serialization breaks under trimming and is flagged by the single-file
/// analyzer. Generating it keeps the published binary honest.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SnippetEntry))]
public sealed partial class SnippetJsonContext : JsonSerializerContext;

/// <summary>One expansion that actually fired.</summary>
/// <param name="Trigger">The phrase that matched.</param>
/// <param name="Count">How many times it fired.</param>
public sealed record AppliedSnippet(string Trigger, int Count);

/// <summary>The outcome of one expansion pass.</summary>
/// <param name="Text">The text to use — unchanged when nothing matched.</param>
/// <param name="Applied">One entry per snippet that fired.</param>
public readonly record struct SnippetExpansionResult(string Text, IReadOnlyList<AppliedSnippet> Applied)
{
    /// <summary>Whether anything expanded.</summary>
    public bool WasChanged => Applied.Count > 0;
}

/// <summary>
/// Replaces spoken trigger phrases with their stored text.
/// </summary>
/// <remarks>
/// <para>
/// The matching rules are lifted from <c>DictionaryCorrector</c> deliberately, because the
/// problem is the same one and its answers have already been argued out:
/// </para>
/// <list type="number">
/// <item><b>Longest trigger first</b>, so a trigger that contains another one is not pre-empted
/// by it.</item>
/// <item><b>Whole matches only</b>, fenced by letter-or-digit lookarounds rather than
/// <c>\b</c>, so a trigger can never bite into a longer word.</item>
/// <item><b>Glued words still match</b> — recognisers run words together, so the gap between
/// parts is matched as optional whitespace or hyphens.</item>
/// <item><b>Replacement is literal.</b> A <c>MatchEvaluator</c> rather than a replacement
/// string, so <c>$1</c> and <c>$&amp;</c> inside a user's snippet body are typed as written
/// instead of being treated as substitutions.</item>
/// </list>
/// <para>
/// One rule is stricter than the dictionary's: a trigger must be at least
/// <see cref="MinTriggerLength"/> characters. A one- or two-letter trigger fires constantly in
/// ordinary speech, and unlike a dictionary correction — which swaps one word for another — a
/// false snippet match drops a paragraph into the middle of a sentence.
/// </para>
/// </remarks>
public sealed class SnippetExpander
{
    /// <summary>Shortest trigger that will ever be compiled into a rule.</summary>
    public const int MinTriggerLength = 3;

    /// <summary>
    /// Guards against a pathological snippet list hanging a dictation. Matching is linear, so
    /// this should never trigger; it exists so a bug cannot wedge the app.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly char[] PhraseSeparators = [' ', '-', '\t'];

    private readonly List<Rule> _rules;

    private sealed record Rule(Regex Regex, string Replacement, string Trigger);

    /// <summary>Compiles the enabled snippets into an ordered rule set.</summary>
    /// <param name="entries">Every snippet. Disabled and whole-utterance ones are ignored.</param>
    public SnippetExpander(IEnumerable<SnippetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // A whole-utterance prompt is matched by WholeUtteranceMatcher before this pass runs,
        // and must never fire mid-sentence: "please review prompt" is not the prompt.
        _rules = entries
            .Where(e => e.IsEnabled)
            .Where(e => e.Match == SnippetMatch.Inline)
            .Where(e => e.Trigger.Trim().Length >= MinTriggerLength)
            .Where(e => e.Text.Length > 0)
            .OrderByDescending(e => e.Trigger.Length)
            .Select(e => MakeRule(e.Trigger, e.Text))
            .OfType<Rule>()
            .ToList();
    }

    /// <summary>True when no enabled snippet produced a usable rule.</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Expands every trigger found in <paramref name="text"/>.</summary>
    /// <param name="text">Transcribed text.</param>
    /// <returns>The rewritten text, plus one entry per snippet that fired.</returns>
    public SnippetExpansionResult Expand(string text)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(text))
        {
            return new SnippetExpansionResult(text, []);
        }

        // NFC before matching, exactly as the dictionary does: decomposed and composed forms of
        // the same accented word are different code-point sequences, so an accented trigger
        // would silently never fire unless both sides agree.
        var result = text.Normalize(NormalizationForm.FormC);
        var applied = new List<AppliedSnippet>();

        foreach (var rule in _rules)
        {
            var matches = rule.Regex.Matches(result);
            if (matches.Count == 0) continue;

            result = rule.Regex.Replace(result, _ => rule.Replacement);
            applied.Add(new AppliedSnippet(rule.Trigger, matches.Count));
        }

        return new SnippetExpansionResult(result, applied);
    }

    private static Rule? MakeRule(string trigger, string replacement)
    {
        var parts = trigger
            .Normalize(NormalizationForm.FormC)
            .Trim()
            .Split(PhraseSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape)
            .ToArray();

        if (parts.Length == 0) return null;

        var body = string.Join(@"[\s\-]*", parts);
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){body}(?![\p{{L}}\p{{N}}])";

        try
        {
            var regex = new Regex(
                pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            return new Rule(regex, replacement, trigger.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>A reason a snippet looks likely to fire when it wasn't meant to.</summary>
/// <param name="Message">What to show the user.</param>
public sealed record SnippetWarning(string Message)
{
    /// <summary>
    /// Words so common that using one as a trigger would expand constantly.
    /// </summary>
    private static readonly HashSet<string> TooCommon = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "you", "that", "this", "with", "have", "not", "but", "all",
        "can", "was", "are", "get", "one", "out", "him", "her", "them", "was", "were",
        "yes", "yeah", "okay", "sure", "hello", "thanks", "please", "sorry",
    };

    /// <summary>Checks a draft snippet.</summary>
    /// <param name="entry">The snippet as it currently stands.</param>
    /// <returns>Every warning that applies, in order of severity.</returns>
    public static IReadOnlyList<SnippetWarning> Check(SnippetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var warnings = new List<SnippetWarning>();
        var trigger = entry.Trigger.Trim();

        if (trigger.Length > 0 && trigger.Length < SnippetExpander.MinTriggerLength)
        {
            warnings.Add(new SnippetWarning(
                $"A trigger shorter than {SnippetExpander.MinTriggerLength} characters is "
                + "ignored — it would fire in ordinary speech."));
        }

        var words = trigger.Split(SpeechText.Whitespace, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 1 && TooCommon.Contains(SpeechText.BareWord(words[0])))
        {
            warnings.Add(new SnippetWarning(entry.Match == SnippetMatch.WholeUtterance
                ? $"\"{trigger}\" is a very common reply. Every time you say just that word, this "
                  + "prompt will be pasted instead. Two or three uncommon words make a safer trigger."
                : $"\"{trigger}\" is a very common word. Every time you say it, this snippet will "
                  + "replace it. Two or three uncommon words make a safer trigger."));
        }

        if (entry.Text.Length > SnippetEntry.MaxLength)
        {
            warnings.Add(new SnippetWarning(
                $"Snippets are limited to {SnippetEntry.MaxLength:N0} characters; the rest will "
                + "be cut off."));
        }

        return warnings;
    }
}
