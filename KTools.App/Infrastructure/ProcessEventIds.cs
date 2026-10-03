// -*- coding: utf-8 -*-
namespace KTools_App.Infrastructure;

public static class ProcessEventIds
{
    public const string Started = "process.started";
    public const string Launched = "process.launched";
    public const string StartFailed = "process.start_failed";
    public const string Exit = "process.exit";
    public const string Completed = "process.completed";
    public const string Cancelled = "process.cancelled";
    public const string TerminationUnverified = "process.termination_unverified";
    public const string OutputSampled = "process.output";
    public const string OutputTruncated = "process.output_truncated";
    public const string ArtifactMissing = "process.artifact_missing";
    public const string PostconditionFailed = "process.postcondition_failed";
    public const string ReadFailed = "process.output_read_failed";
}
