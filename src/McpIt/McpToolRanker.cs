using System;
using System.Collections.Generic;
using System.Text;

namespace McpIt;

/// <summary>
/// Ranks MCP tools against a free-text task description ("cancel my order", "list customers")
/// so an agent facing a large tool list can find the right tool. Pure, offline and
/// deterministic: no embeddings, no network, no reflection.
/// </summary>
/// <remarks>
/// <para>
/// Scoring is BM25 over the tool catalog with per-field weights (the tool name counts far more
/// than its description), plus bonuses for an exact or prefix name match, a small
/// <see cref="McpToolDescriptor.Priority"/> boost, and a nudge toward tools whose HTTP verb fits
/// the query's intent ("delete" favors DELETE, "list" favors GET, ...).
/// </para>
/// <para>
/// Matching is lexical. Plurals and common verb endings are folded ("orders", "ordered" and
/// "ordering" all match "order") and a query word also matches longer words it prefixes
/// ("cancel" matches "cancellation"), but typos and true synonyms are out of scope; declare
/// synonyms through <c>[McpTool(Keywords = ...)]</c> instead.
/// </para>
/// <para>
/// <see cref="Rank"/> builds a fresh index on every call. To answer many queries against the
/// same catalog, build one <see cref="McpToolIndex"/> and call <see cref="McpToolIndex.Search"/>.
/// </para>
/// </remarks>
public static class McpToolRanker
{
    /// <summary>
    /// Ranks <paramref name="tools"/> against <paramref name="query"/> and returns the best
    /// <paramref name="top"/> matches, best first.
    /// </summary>
    /// <param name="tools">The tool catalog, typically <c>McpIt.Generated.McpItToolCatalog.Tools</c>.</param>
    /// <param name="query">A plain-language description of the task. Empty or whitespace returns
    /// tools in priority order instead.</param>
    /// <param name="top">Maximum number of results; must be at least 1.</param>
    /// <returns>Matches ordered by score (desc), then priority (desc), then name (ordinal).
    /// Tools that do not match at all are left out.</returns>
    public static IReadOnlyList<McpToolMatch> Rank(
        IReadOnlyList<McpToolDescriptor> tools, string query, int top = 5)
        => new McpToolIndex(tools).Search(query, top);

    /// <summary>
    /// Splits text into the normalized search terms the ranker indexes and matches: lowercase,
    /// split on camelCase/PascalCase/snake_case/kebab-case/route separators (route braces are
    /// dropped), stopwords and single characters removed, plurals and verb endings folded.
    /// For example <c>getOrderById</c> becomes <c>get, order, id</c> ("by" is a stopword) and
    /// <c>/api/orders/{id}</c> becomes <c>api, order, id</c>.
    /// </summary>
    /// <param name="text">Text to tokenize; null yields no terms.</param>
    /// <returns>Terms in input order; duplicates are kept.</returns>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var terms = new List<string>();
        AppendTerms(text, terms);
        return terms;
    }

    // Tokenizes into an existing list so the index can build field token lists without
    // allocating an intermediate list per field.
    internal static void AppendTerms(string? text, List<string> terms)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (!char.IsLetterOrDigit(c))
            {
                if (start >= 0)
                    AddWord(text, start, i, terms);
                start = -1;
                continue;
            }

            if (start < 0)
            {
                start = i;
                continue;
            }

            // camelCase boundary: "getOrder" -> get | Order.
            // Acronym boundary:   "HTTPServer" -> HTTP | Server (split before the last capital
            // of a run when it starts a lowercase word).
            var prev = text[i - 1];
            var boundary = char.IsUpper(c) &&
                (char.IsLower(prev) || char.IsDigit(prev) ||
                 (char.IsUpper(prev) && i + 1 < text.Length && char.IsLower(text[i + 1])));
            if (boundary)
            {
                AddWord(text, start, i, terms);
                start = i;
            }
        }
    }

    private static void AddWord(string text, int start, int end, List<string> terms)
    {
        if (end - start < 2)
            return; // single characters ("a", "v", "1") carry no signal

        var word = text.Substring(start, end - start).ToLowerInvariant();
        if (Stopwords.Contains(word))
            return;

        terms.Add(Stem(word));
    }

    /// <summary>
    /// Light, predictable suffix folding applied identically to tools and queries, so only
    /// consistency matters, not producing real English roots. Rules, in order:
    /// "-ies" to "-y"; "-es" after s/x/z/ch/sh; plural "-s" (not "-ss"/"-us"/"-is");
    /// "-ing"/"-ed" (undoubling a final consonant: "shipped" to "ship"); then a trailing "e"
    /// on words longer than four letters, so "create", "creates", "created" and "creating"
    /// all fold to the same term.
    /// </summary>
    internal static string Stem(string word)
    {
        var w = word;

        // Plurals.
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal))
            w = w.Substring(0, w.Length - 3) + "y";
        else if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal) &&
                 (EndsWithAny(w, w.Length - 2, "s", "x", "z", "ch", "sh")))
            w = w.Substring(0, w.Length - 2);
        else if (w.Length > 3 && w[w.Length - 1] == 's' &&
                 !w.EndsWith("ss", StringComparison.Ordinal) &&
                 !w.EndsWith("us", StringComparison.Ordinal) &&
                 !w.EndsWith("is", StringComparison.Ordinal))
            w = w.Substring(0, w.Length - 1);

        // Verb endings. Require a stem of at least three letters so short words survive.
        var stripped = false;
        if (w.Length >= 6 && w.EndsWith("ing", StringComparison.Ordinal))
        {
            w = w.Substring(0, w.Length - 3);
            stripped = true;
        }
        else if (w.Length >= 5 && w.EndsWith("ed", StringComparison.Ordinal))
        {
            w = w.Substring(0, w.Length - 2);
            stripped = true;
        }

        // "shipped"/"shipping" -> "shipp" -> "ship". l/s/z doubles are usually part of the
        // root ("billing" -> "bill", "processed" -> "process"), so they are kept.
        if (stripped && w.Length >= 4 && w[w.Length - 1] == w[w.Length - 2] &&
            IsConsonant(w[w.Length - 1]) && w[w.Length - 1] is not ('l' or 's' or 'z'))
            w = w.Substring(0, w.Length - 1);

        // ">= 4" so a 4-letter word folds with its "-es" plural ("case"/"cases" -> "cas").
        if (w.Length >= 4 && w[w.Length - 1] == 'e')
            w = w.Substring(0, w.Length - 1);

        return w;
    }

    private static bool EndsWithAny(string w, int end, params string[] suffixes)
    {
        foreach (var s in suffixes)
        {
            if (end >= s.Length && string.CompareOrdinal(w, end - s.Length, s, 0, s.Length) == 0)
                return true;
        }
        return false;
    }

    private static bool IsConsonant(char c) =>
        c is >= 'a' and <= 'z' && c is not ('a' or 'e' or 'i' or 'o' or 'u' or 'y');

    // Deliberately tiny: function words that never distinguish one tool from another. Verbs,
    // "all", "new" and question words ("who", "which") are kept because they carry intent.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "an", "the", "of", "to", "for", "in", "on", "at", "by", "with", "from", "into",
        "and", "or", "is", "are", "was", "be", "it", "its", "this", "that", "these", "those",
        "my", "me", "our", "your", "their", "please", "some", "can", "do", "does", "how",
    };
}

/// <summary>
/// A prebuilt, immutable search index over a tool catalog. Build it once (for example at
/// startup) and call <see cref="Search"/> per query; <see cref="Search"/> is thread-safe.
/// See <see cref="McpToolRanker"/> for how results are scored.
/// </summary>
public sealed class McpToolIndex
{
    // ---- Field weights (BM25F-style: a term's frequency counts once per weight unit). ----
    // The tool name is the strongest signal an agent has; keywords are explicit synonyms
    // declared by the API author; title often restates the name; category groups tools;
    // description is long and noisy; route and parameter names are weak supporting evidence.

    /// <summary>Weight of a term occurring in the tool name.</summary>
    internal const double NameWeight = 6.0;
    /// <summary>Weight of a term occurring in the tool's declared keywords.</summary>
    internal const double KeywordWeight = 3.0;
    /// <summary>Weight of a term occurring in the tool title.</summary>
    internal const double TitleWeight = 2.5;
    /// <summary>Weight of a term occurring in the tool category.</summary>
    internal const double CategoryWeight = 2.0;
    /// <summary>Weight of a term occurring in the tool description.</summary>
    internal const double DescriptionWeight = 1.0;
    /// <summary>Weight of a term occurring in the route template.</summary>
    internal const double RouteWeight = 1.0;
    /// <summary>Weight of a term occurring in a parameter name.</summary>
    internal const double ParameterWeight = 0.5;

    // ---- BM25 parameters. ----

    /// <summary>BM25 term-frequency saturation. Higher than the textbook 1.2 because weighted
    /// frequencies are larger, and a name hit should stay clearly ahead of a description hit.</summary>
    internal const double K1 = 2.0;
    /// <summary>BM25 length normalization. Moderate, so a long description is only mildly
    /// penalized.</summary>
    internal const double B = 0.5;

    // ---- Bonuses and nudges. ----

    /// <summary>Added when the whole query is the tool's name ("getOrderById",
    /// "get order by id").</summary>
    internal const double ExactNameBonus = 10.0;
    /// <summary>Added when the normalized tool name starts with the whole query ("getOrder"
    /// for "getOrderById"). Queries shorter than <see cref="MinPrefixLength"/> never qualify.</summary>
    internal const double NamePrefixBonus = 2.0;
    /// <summary>Fraction of a full match credited when a query term is only a prefix of an
    /// indexed term ("cancel" in "cancellation").</summary>
    internal const double PrefixMatchFactor = 0.5;
    /// <summary>Minimum query-term length that may match by prefix.</summary>
    internal const int MinPrefixLength = 3;
    /// <summary>Multiplier bonus (1 + this) for tools whose HTTP verb fits the query's intent.
    /// Large enough to separate getOrder from deleteOrder for "remove an order", too small to
    /// override a clearly better text match.</summary>
    internal const double VerbIntentBoost = 0.25;
    /// <summary>Multiplier bonus per point of <see cref="McpToolDescriptor.Priority"/>
    /// (clamped to +/- <see cref="MaxPriorityEffect"/> points), so priority mostly breaks
    /// near-ties.</summary>
    internal const double PriorityBoostPerPoint = 0.02;
    /// <summary>Priority values beyond this magnitude have no additional effect.</summary>
    internal const int MaxPriorityEffect = 10;

    private readonly McpToolDescriptor[] _tools;
    private readonly double[] _docLength;
    private readonly double _avgDocLength;
    private readonly string[] _normalizedNames;
    private readonly string[] _nameTermKeys;
    private readonly Dictionary<string, Posting[]> _postings;
    private readonly string[] _vocabulary; // sorted ordinal, for prefix range lookups

    private readonly struct Posting(int doc, double weightedTf)
    {
        public readonly int Doc = doc;
        public readonly double WeightedTf = weightedTf;
    }

    /// <summary>Builds the index. Cost is linear in the total size of the catalog.</summary>
    /// <param name="tools">The tool catalog to index; the list is copied.</param>
    public McpToolIndex(IReadOnlyList<McpToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _tools = new McpToolDescriptor[tools.Count];
        _docLength = new double[tools.Count];
        _normalizedNames = new string[tools.Count];
        _nameTermKeys = new string[tools.Count];

        var postings = new Dictionary<string, List<Posting>>(StringComparer.Ordinal);
        var weighted = new Dictionary<string, double>(StringComparer.Ordinal);
        var scratch = new List<string>();
        double totalLength = 0;

        for (var d = 0; d < tools.Count; d++)
        {
            var tool = tools[d] ?? throw new ArgumentException("Tool catalog contains a null entry.", nameof(tools));
            _tools[d] = tool;
            _normalizedNames[d] = Normalize(tool.Name);

            weighted.Clear();
            double length = 0;

            scratch.Clear();
            McpToolRanker.AppendTerms(tool.Name, scratch);
            _nameTermKeys[d] = string.Join(" ", scratch);
            length += AddField(weighted, scratch, NameWeight);

            scratch.Clear();
            foreach (var k in tool.Keywords ?? [])
                McpToolRanker.AppendTerms(k, scratch);
            length += AddField(weighted, scratch, KeywordWeight);

            length += AddText(weighted, scratch, tool.Title, TitleWeight);
            length += AddText(weighted, scratch, tool.Category, CategoryWeight);
            length += AddText(weighted, scratch, tool.Description, DescriptionWeight);
            length += AddText(weighted, scratch, tool.Route, RouteWeight);

            scratch.Clear();
            foreach (var p in tool.Parameters ?? [])
                McpToolRanker.AppendTerms(p, scratch);
            length += AddField(weighted, scratch, ParameterWeight);

            _docLength[d] = length;
            totalLength += length;

            foreach (var pair in weighted)
            {
                if (!postings.TryGetValue(pair.Key, out var list))
                    postings[pair.Key] = list = [];
                list.Add(new Posting(d, pair.Value));
            }
        }

        _avgDocLength = tools.Count == 0 ? 1 : Math.Max(totalLength / tools.Count, 1e-9);
        _postings = new Dictionary<string, Posting[]>(postings.Count, StringComparer.Ordinal);
        foreach (var pair in postings)
            _postings[pair.Key] = pair.Value.ToArray();

        _vocabulary = new string[_postings.Count];
        _postings.Keys.CopyTo(_vocabulary, 0);
        Array.Sort(_vocabulary, StringComparer.Ordinal);
    }

    /// <summary>The number of indexed tools.</summary>
    public int Count => _tools.Length;

    /// <summary>
    /// Returns the best <paramref name="top"/> tools for <paramref name="query"/>, best first.
    /// A query that is empty, whitespace or only stopwords returns tools by
    /// <see cref="McpToolDescriptor.Priority"/> (desc) then name, all with score 0. Otherwise
    /// tools that do not match at all are left out, so fewer than <paramref name="top"/>
    /// results (or none) may come back.
    /// </summary>
    /// <param name="query">A plain-language description of the task.</param>
    /// <param name="top">Maximum number of results; must be at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is less than 1.</exception>
    public IReadOnlyList<McpToolMatch> Search(string? query, int top = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);

        var terms = McpToolRanker.Tokenize(query);
        if (terms.Count == 0)
            return ByPriority(top); // nothing searchable: empty, whitespace or only stopwords
        var normalizedQuery = Normalize(query);

        var n = _tools.Length;
        var scores = new double[n];
        var best = new double[n]; // per-term best prefix-expansion credit, reset after each term
        var touched = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var term in terms)
        {
            if (!seen.Add(term))
                continue; // repeated query words add no information

            if (_postings.TryGetValue(term, out var exact))
            {
                var idf = Idf(exact.Length);
                foreach (var p in exact)
                    scores[p.Doc] += idf * Saturate(p.WeightedTf, p.Doc);
            }

            if (term.Length < MinPrefixLength)
                continue;

            // Prefix expansion: credit the best longer term this query term starts with, once
            // per tool, so "cancel" matches "cancellation" without double counting a tool that
            // has several such terms.
            touched.Clear();
            var i = LowerBound(term);
            for (; i < _vocabulary.Length && _vocabulary[i].StartsWith(term, StringComparison.Ordinal); i++)
            {
                var candidate = _vocabulary[i];
                if (candidate.Length == term.Length)
                    continue; // the exact term, already scored
                var postings = _postings[candidate];
                var idf = Idf(postings.Length);
                foreach (var p in postings)
                {
                    var credit = PrefixMatchFactor * idf * Saturate(p.WeightedTf, p.Doc);
                    if (best[p.Doc] == 0)
                        touched.Add(p.Doc);
                    if (credit > best[p.Doc])
                        best[p.Doc] = credit;
                }
            }
            foreach (var d in touched)
            {
                scores[d] += best[d];
                best[d] = 0;
            }
        }

        var queryNameKey = string.Join(" ", terms);
        var intent = DetectIntent(terms);
        var results = new List<McpToolMatch>();

        for (var d = 0; d < n; d++)
        {
            var score = scores[d];

            if (string.Equals(normalizedQuery, _normalizedNames[d], StringComparison.Ordinal))
                score += ExactNameBonus;
            else if (string.Equals(queryNameKey, _nameTermKeys[d], StringComparison.Ordinal))
                score += ExactNameBonus;
            else if (normalizedQuery.Length >= MinPrefixLength &&
                     _normalizedNames[d].StartsWith(normalizedQuery, StringComparison.Ordinal))
                score += NamePrefixBonus;

            if (score <= 0)
                continue;

            var tool = _tools[d];
            if (MatchesIntent(intent, tool))
                score *= 1 + VerbIntentBoost;

            var priority = Math.Clamp(tool.Priority, -MaxPriorityEffect, MaxPriorityEffect);
            score *= 1 + PriorityBoostPerPoint * priority;

            results.Add(new McpToolMatch(tool, score));
        }

        results.Sort(CompareMatches);
        if (results.Count > top)
            results.RemoveRange(top, results.Count - top);
        return results;
    }

    private IReadOnlyList<McpToolMatch> ByPriority(int top)
    {
        var results = new List<McpToolMatch>(_tools.Length);
        foreach (var tool in _tools)
            results.Add(new McpToolMatch(tool, 0));
        results.Sort(CompareMatches);
        if (results.Count > top)
            results.RemoveRange(top, results.Count - top);
        return results;
    }

    // Score desc, then priority desc, then name ordinal: fully deterministic.
    private static int CompareMatches(McpToolMatch x, McpToolMatch y)
    {
        var c = y.Score.CompareTo(x.Score);
        if (c != 0) return c;
        c = y.Tool.Priority.CompareTo(x.Tool.Priority);
        if (c != 0) return c;
        return string.CompareOrdinal(x.Tool.Name, y.Tool.Name);
    }

    // BM25 IDF with the +1 inside the log so it is always positive, even for a term that
    // occurs in every tool (otherwise a one-tool catalog could never match anything).
    private double Idf(int documentFrequency) =>
        Math.Log(1 + (_tools.Length - documentFrequency + 0.5) / (documentFrequency + 0.5));

    private double Saturate(double weightedTf, int doc)
    {
        var norm = 1 - B + B * (_docLength[doc] / _avgDocLength);
        return weightedTf * (K1 + 1) / (weightedTf + K1 * norm);
    }

    private int LowerBound(string term)
    {
        int lo = 0, hi = _vocabulary.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(_vocabulary[mid], term) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static double AddText(Dictionary<string, double> weighted, List<string> scratch, string? text, double weight)
    {
        scratch.Clear();
        McpToolRanker.AppendTerms(text, scratch);
        return AddField(weighted, scratch, weight);
    }

    private static double AddField(Dictionary<string, double> weighted, List<string> terms, double weight)
    {
        foreach (var t in terms)
            weighted[t] = (weighted.TryGetValue(t, out var w) ? w : 0) + weight;
        return terms.Count * weight;
    }

    // Lowercase letters and digits only: "get_order-by Id" and "getOrderById" compare equal.
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // ---- Verb intent. ----

    [Flags]
    private enum Intent
    {
        None = 0,
        Delete = 1,
        Create = 2,
        Update = 4,
        Read = 8,
    }

    private static readonly Dictionary<string, Intent> IntentWords = BuildIntentWords();

    private static Dictionary<string, Intent> BuildIntentWords()
    {
        var map = new Dictionary<string, Intent>(StringComparer.Ordinal);
        void Add(Intent intent, params string[] words)
        {
            // Stored as stems so "deleting", "removed" etc. resolve through the same path.
            foreach (var w in words)
                map[McpToolRanker.Stem(w)] = intent;
        }

        Add(Intent.Delete, "delete", "remove", "cancel", "destroy", "erase", "purge", "drop", "revoke", "discard", "void");
        Add(Intent.Create, "create", "add", "new", "register", "submit", "place", "make", "insert", "post");
        Add(Intent.Update, "update", "edit", "change", "modify", "set", "rename", "patch", "replace", "adjust", "move");
        Add(Intent.Read, "get", "list", "find", "show", "search", "fetch", "view", "lookup", "look", "retrieve",
            "read", "display", "browse", "query", "count", "check", "who", "what", "which", "where", "when", "status");
        return map;
    }

    private static Intent DetectIntent(IReadOnlyList<string> terms)
    {
        var intent = Intent.None;
        foreach (var t in terms)
        {
            if (IntentWords.TryGetValue(t, out var i))
                intent |= i;
        }
        return intent;
    }

    private static bool MatchesIntent(Intent intent, McpToolDescriptor tool)
    {
        if (intent == Intent.None)
            return false;
        var method = tool.HttpMethod ?? string.Empty;
        return
            // Not tool.Destructive: the generator marks every POST/PUT/PATCH destructive too.
            ((intent & Intent.Delete) != 0 && Is(method, "DELETE")) ||
            ((intent & Intent.Create) != 0 && Is(method, "POST")) ||
            ((intent & Intent.Update) != 0 && (Is(method, "PUT") || Is(method, "PATCH"))) ||
            ((intent & Intent.Read) != 0 && (Is(method, "GET") || tool.ReadOnly));

        static bool Is(string actual, string expected) =>
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
