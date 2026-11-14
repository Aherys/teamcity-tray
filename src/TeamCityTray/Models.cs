using System;
using System.Collections.Generic;

namespace TeamCityTray;

public sealed record BuildInfo(
    long Id,
    string BuildTypeName,
    string ProjectId,
    string ProjectName,
    string Number,
    bool IsQueued,
    string? WebUrl,
    DateTime? Queued,
    DateTime? Started,
    string? TriggerUser,
    long? TriggerBuildId,
    IReadOnlyList<long> Dependencies,
    int Percent,
    int? ElapsedSeconds);

public sealed record FinalInfo(string State, string Status, string StatusText);

public enum RowState
{
    Queued,
    Running,
    Success,
    Failure,
}

public sealed record RowData(
    long Id,
    string Name,
    string Number,
    RowState State,
    int Percent,
    int ElapsedSeconds,
    string? WebUrl,
    int Depth,
    string ToolTip);
