// -*- coding: utf-8 -*-
namespace KTools_App.Diagnostics;

public enum LogStatus
{
    None = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    Skipped = 5,
    PartiallySucceeded = 6,
    RetryScheduled = 7,
    Changed = 8
}
