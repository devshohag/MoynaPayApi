using System.Text.RegularExpressions;

namespace MoynaPay.Application.Payments.Parsing;

/// <summary>
/// One pattern and what a match means.
///
/// Templates live in the database in production - operators change their wording without
/// warning, and a fix that ships as a row beats one that ships as a deployment. The set
/// in BkashTemplates is the seed for that table and the fixture baseline for tests.
/// </summary>
public sealed class MessageTemplate
{
    /// <summary>
    /// A regex from the database is effectively input, and a carelessly written one can
    /// be made to run for a very long time on a short string. Two defences: prefer the
    /// non-backtracking engine, which has no catastrophic case, and cap every match with
    /// a timeout for the patterns it cannot compile.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex _regex;

    public MessageTemplate(
        string code,
        MessageKind kind,
        string pattern,
        decimal baseConfidence = 1.00m)
    {
        Code = code;
        Kind = kind;
        Pattern = pattern;
        BaseConfidence = baseConfidence;

        _regex = Compile(pattern);
    }

    public string Code { get; }
    public MessageKind Kind { get; }
    public string Pattern { get; }
    public decimal BaseConfidence { get; }

    public Match Match(string body) => _regex.Match(body);

    private static Regex Compile(string pattern)
    {
        try
        {
            return new Regex(
                pattern,
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // The non-backtracking engine rejects lookarounds and backreferences. A
            // template that needs them still runs, but only behind a hard timeout.
            return new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                MatchTimeout);
        }
    }
}
