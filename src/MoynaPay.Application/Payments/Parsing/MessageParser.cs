using System.Text.RegularExpressions;

namespace MoynaPay.Application.Payments.Parsing;

public interface IMessageParser
{
    ParsedMessage Parse(string senderId, string body);
}

/// <summary>
/// Reads a provider message into fields, and refuses to guess.
///
/// Two refusals matter more than any successful parse:
///
/// 1. A sender id outside the whitelist is dropped without being read. Anyone can send a
///    text that looks exactly like a bKash confirmation, and the only thing separating a
///    real one from a forgery on the handset is who it came from.
///
/// 2. A message no template recognises comes back Unknown with zero confidence, which
///    routes it to a human. It never falls through to a looser pattern. When bKash
///    changes its wording - and it will - this product stops settling invoices it cannot
///    read, rather than settling them wrongly.
/// </summary>
public sealed class MessageParser(IReadOnlyList<MessageTemplate> templates) : IMessageParser
{
    private readonly IReadOnlyList<MessageTemplate> _templates = templates;

    public static MessageParser ForBkash() => new(BkashTemplates.All);

    public ParsedMessage Parse(string senderId, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ParsedMessage.Unknown("Empty body.");
        }

        if (!BkashTemplates.SenderIds.Contains(senderId))
        {
            return ParsedMessage.Unknown($"Sender '{senderId}' is not whitelisted.");
        }

        var normalised = Normalise(body);

        foreach (var template in _templates)
        {
            Match match;

            try
            {
                match = template.Match(normalised);
            }
            catch (RegexMatchTimeoutException)
            {
                // A template that cannot finish in 250ms on a 200 character message is
                // broken. Skip it, keep going, and let the ops queue surface it.
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            return Build(template, match);
        }

        return ParsedMessage.Unknown("No active template matched.");
    }

    /// <summary>
    /// Collapses the line breaks and repeated spaces that multi-line messages and
    /// notification rendering introduce, so one pattern covers both shapes of the same
    /// message.
    /// </summary>
    private static string Normalise(string body) =>
        Regex.Replace(body.Trim(), @"\s+", " ", RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));

    private static ParsedMessage Build(MessageTemplate template, Match match)
    {
        var trxId = BkashFieldReader.ReadTrxId(Group(match, "trxId"));
        var amount = BkashFieldReader.ReadAmount(Group(match, "amount"));
        var occurredAt = BkashFieldReader.ReadTimestamp(Group(match, "at"));

        // A credit needs all three of these to settle anything. If the pattern matched
        // but a field did not read - a corrupt timestamp, a truncated transaction id -
        // then what matched was not the message we think it was, and calling it a
        // payment with lower confidence would be dressing up a failed parse as a
        // judgement call. It is Unknown, and a human looks at it.
        if (template.Kind.IsCredit() &&
            (trxId is null || amount is null || occurredAt is null))
        {
            return ParsedMessage.Unknown(
                $"Template {template.Code} matched but a required field did not read.");
        }

        return new ParsedMessage
        {
            Kind = template.Kind,
            TemplateCode = template.Code,
            Confidence = template.BaseConfidence,
            Amount = amount,
            Fee = BkashFieldReader.ReadAmount(Group(match, "fee")),
            BalanceAfter = BkashFieldReader.ReadAmount(Group(match, "balance")),
            TrxId = trxId,
            CounterpartyMsisdn = Group(match, "counterparty"),
            Reference = Group(match, "reference")?.Trim(),
            OccurredAt = occurredAt,
        };
    }

    private static string? Group(Match match, string name)
    {
        var group = match.Groups[name];
        return group.Success && group.Value.Length > 0 ? group.Value : null;
    }
}
