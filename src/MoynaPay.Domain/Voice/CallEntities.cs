using MoynaPay.Domain.Common;

namespace MoynaPay.Domain.Voice;

// Schema: calls  (Section 12 - "CallSession minimum fields", Section 8 - Voice & Telephony Architecture)

public class CallSession : BaseEntity
{
    // Field list copied verbatim from the proposal's "CallSession minimum fields" box (page 10)
    // so this stays a drop-in match for the spec, not a re-interpretation of it.
    public string? ExternalCallId { get; set; } // Asterisk Linkedid
    public CallDirection Direction { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? QueueId { get; set; }
    public string FromNumber { get; set; } = default!;
    public string ToNumber { get; set; } = default!;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RingingAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public int? RingDurationSeconds { get; set; }
    public int? TalkDurationSeconds { get; set; }
    public int? HoldDurationSeconds { get; set; }

    public CallStatus Status { get; set; } = CallStatus.Ringing;
    public string? HangupCause { get; set; }
    public Guid? DispositionId { get; set; }
    public Guid? RecordingId { get; set; }

    // ---- Outbound / AI additions -------------------------------------------------------
    // Direction and CampaignId already existed above; these are the fields an AI-placed,
    // campaign-driven call needs that a human-dialled one does not.

    /// <summary>
    /// The specific campaign attempt this call belongs to. CampaignId alone cannot answer
    /// "which attempt was this", which is what retry accounting and per-contact history need.
    /// </summary>
    public Guid? CampaignLeadId { get; set; }

    /// <summary>
    /// What answered. Outbound reality is that roughly two thirds of connected calls reach
    /// voicemail, and an agent that talks to an answering machine burns money while looking
    /// broken - so the detection result is first-class data, not a log line.
    /// </summary>
    public AnsweringMachineResult AmdResult { get; set; } = AnsweringMachineResult.NotChecked;

    /// <summary>
    /// Caller-supplied de-duplication key. A dialer retry, a redelivered queue message and a
    /// double-clicked button must not become three calls to the same person; a unique index on
    /// this column is what makes origination idempotent.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// When the "this call is being recorded" announcement finished playing. Null means it was
    /// not played, which for a recorded call is a compliance defect rather than a missing value.
    /// </summary>
    public DateTime? RecordingConsentAnnouncedAt { get; set; }

    /// <summary>Whether the disposition was chosen by the AI, a human agent, or system rules.</summary>
    public DispositionSource DispositionSetBy { get; set; } = DispositionSource.NotSet;

    /// <summary>Agent the call was handed to, when a transfer completed.</summary>
    public Guid? TransferredToAgentId { get; set; }

    public ICollection<CallLeg> Legs { get; set; } = new List<CallLeg>();
    public ICollection<CallEvent> Events { get; set; } = new List<CallEvent>();
    public ICollection<CallStageTiming> StageTimings { get; set; } = new List<CallStageTiming>();
}

public enum CallDirection { Inbound, Outbound }

/// <summary>Outcome of answering-machine detection on an outbound call.</summary>
public enum AnsweringMachineResult
{
    /// <summary>Detection did not run - inbound calls, or AMD disabled for the campaign.</summary>
    NotChecked,

    Human,
    Machine,

    /// <summary>Answered but nothing was said - common with call-screening services.</summary>
    Silence,

    /// <summary>Detection ran and could not decide. Treated as Human, but counted separately.</summary>
    Unknown
}

public enum DispositionSource
{
    NotSet,
    Ai,
    Agent,
    System
}

public enum CallStatus { Ringing, Answered, OnHold, Transferring, Ended, Failed, Abandoned }

public class CallLeg : BaseEntity
{
    // "Agent, trunk, transfer and conference legs"
    public Guid CallSessionId { get; set; }
    public CallSession? CallSession { get; set; }
    public string LegType { get; set; } = default!; // "agent" | "trunk" | "transfer" | "conference"
    public string Channel { get; set; } = default!;  // Asterisk channel identifier
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}

public class CallEvent : BaseEntity
{
    // "Ringing, answered, hold, transfer, ended, recording events" - normalized from ARI/AMI.
    public Guid CallSessionId { get; set; }
    public CallSession? CallSession { get; set; }
    public string EventType { get; set; } = default!;
    public string? PayloadJson { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public class Recording : BaseEntity
{
    // "Object storage metadata, retention and access"
    public Guid CallSessionId { get; set; }
    public string ObjectStorageKey { get; set; } = default!; // MinIO/S3 key; SQL never stores audio bytes
    public int DurationSeconds { get; set; }
    public long SizeBytes { get; set; }
    public DateTime RetainUntil { get; set; }
}

public class Disposition : BaseEntity
{
    public string Code { get; set; } = default!; // e.g. "sale", "callback", "not-interested"
    public string Description { get; set; } = default!;
    public bool RequiresFollowUp { get; set; }
}

/// <summary>
/// One measured stage of one conversational turn.
///
/// WHY THIS IS A TABLE AND NOT JUST A LOG LINE
/// -------------------------------------------
/// Latency is the product's acceptance criterion, so it has to be queryable: p50/p95 per
/// stage, per concurrency level, before and after a change. Grepping container logs cannot
/// answer "did last week's release make the answer slower", and a metric alone cannot answer
/// "which stage, on which call".
///
/// The field names deliberately mirror the VOICE_TIMING JSON the existing Python worker
/// already emits. That is not cosmetic: the old batch pipeline and the new realtime gateway
/// must write the SAME schema, otherwise the before/after comparison that justifies the
/// cutover cannot be computed.
///
/// Stages overlap and nest (a "decision" stage contains an "llm_http" stage), so durations
/// must never be summed to produce a turn total - read <see cref="TurnProcessingTotalStage"/>
/// for that instead.
///
/// This table is high-volume and short-lived: it is rolled up into daily metrics and trimmed
/// by the retention job, unlike the compliance tables which are kept permanently.
/// </summary>
public class CallStageTiming : BaseEntity
{
    /// <summary>The canonical stage name for an end-to-end turn measurement.</summary>
    public const string TurnProcessingTotalStage = "turn_processing_total";

    /// <summary>
    /// The stage that answers the acceptance question - end of caller speech to the first
    /// audio sample actually leaving for the caller. An ARI acknowledgement is NOT this:
    /// the channel accepting a playback request is not the caller hearing sound.
    /// </summary>
    public const string TimeToFirstAudioStage = "time_to_first_audio";

    public Guid CallSessionId { get; set; }
    public CallSession? CallSession { get; set; }

    /// <summary>Conversational turn this stage belongs to. Null for call-level stages.</summary>
    public Guid? TurnId { get; set; }

    /// <summary>1-based turn ordinal, for reading a call in sequence without joining.</summary>
    public int? TurnNo { get; set; }

    /// <summary>e.g. "stt_inference", "llm_http", "tts_synthesis", "time_to_first_audio".</summary>
    public string Stage { get; set; } = default!;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null while the stage is still running, or if the process died mid-stage.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Measured by the emitting process rather than derived from the timestamps, so it stays
    /// accurate even when the two clocks differ or the row is written late.
    /// </summary>
    public double? DurationMs { get; set; }

    /// <summary>"started" | "success" | "failed" | "timeout" | "cancelled".</summary>
    public string Outcome { get; set; } = "started";

    /// <summary>
    /// Number of calls in flight when this stage ran. Without it a p95 is meaningless,
    /// because a single-call measurement and a ten-call measurement are different systems.
    /// </summary>
    public int? Concurrency { get; set; }

    /// <summary>Length of audio handed to the stage. Required to compute a real-time factor.</summary>
    public int? AudioDurationMs { get; set; }

    /// <summary>Audio length after voice-activity trimming; the difference is silence not worth processing.</summary>
    public int? VadAudioDurationMs { get; set; }

    /// <summary>Provider that served the stage, e.g. "local-whisper", "piper", "ollama".</summary>
    public string? Provider { get; set; }

    /// <summary>Model identifier, e.g. "base.en". Comparing releases requires knowing this.</summary>
    public string? Model { get; set; }

    /// <summary>Correlation id shared with the emitting process's own logs.</summary>
    public string? RequestId { get; set; }

    /// <summary>Timing-record schema version, so readers can tolerate older rows.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Which pipeline produced the row, e.g. "batch-v1", "gateway-v1". This is the before/after axis.</summary>
    public string? PipelineVersion { get; set; }

    /// <summary>
    /// Provider-specific knobs that are useful for diagnosis but not worth a column each -
    /// beam size, thread count, compute type, sample rate. Diagnostic only; never aggregated.
    /// </summary>
    public string? MetadataJson { get; set; }
}
