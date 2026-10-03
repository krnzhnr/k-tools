// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Diagnostics;

public enum LogFileFormat
{
    Text = 0,
    Jsonl = 1
}

public sealed class LogServiceOptions
{
    public const int DefaultQueueCapacity = 4096;
    public const int DefaultUiQueueCapacity = 4096;
    public const int DefaultSubscriberQueueCapacity = 1024;
    public const int DefaultMaxQueuedBytes = 8 * 1024 * 1024;
    public const int DefaultMaxMessageLength = 4 * 1024;
    public const int DefaultMaxPropertiesLength = 8 * 1024;
    public const int DefaultMaxDetailLength = 16 * 1024;
    public const long DefaultMaxFileBytes = 16L * 1024 * 1024;
    public const double DefaultRotationTriggerRatio = 0.8d;
    public const int DefaultMaxFiles = 20;
    public const long DefaultMaxDirectoryBytes = 256L * 1024 * 1024;
    public const int DefaultRetentionDays = 10;
    public const int DefaultFlushIntervalMilliseconds = 1000;
    public const int DefaultMaxExceptionDepth = 8;
    public const int DefaultMaxEmergencyFileBytes = 64 * 1024;
    public const int DefaultMaxEmergencyFiles = 5;
    public const int DefaultEmergencyRetentionDays = 30;
    public const int DefaultReadRecentEventsLimit = 1000;
    public const int DefaultMaxExportLines = 20000;

    public int QueueCapacity { get; set; } = DefaultQueueCapacity;

    public int SubscriberQueueCapacity { get; set; } = DefaultSubscriberQueueCapacity;

    public int UiQueueCapacity { get; set; } = DefaultUiQueueCapacity;

    public int MaxQueuedBytes { get; set; } = DefaultMaxQueuedBytes;

    public int MaxMessageLength { get; set; } = DefaultMaxMessageLength;

    public int MaxPropertiesLength { get; set; } = DefaultMaxPropertiesLength;

    public int MaxDetailLength { get; set; } = DefaultMaxDetailLength;

    public long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    public double RotationTriggerRatio { get; set; } = DefaultRotationTriggerRatio;

    public int MaxFiles { get; set; } = DefaultMaxFiles;

    public long MaxDirectoryBytes { get; set; } = DefaultMaxDirectoryBytes;

    public int RetentionDays { get; set; } = DefaultRetentionDays;

    public int FlushIntervalMilliseconds { get; set; } = DefaultFlushIntervalMilliseconds;

    public int MaxExceptionDepth { get; set; } = DefaultMaxExceptionDepth;

    public int MaxEmergencyFileBytes { get; set; } = DefaultMaxEmergencyFileBytes;

    public int MaxEmergencyFiles { get; set; } = DefaultMaxEmergencyFiles;

    public int EmergencyRetentionDays { get; set; } = DefaultEmergencyRetentionDays;

    public int ReadRecentEventsLimit { get; set; } = DefaultReadRecentEventsLimit;

    public int MaxExportLines { get; set; } = DefaultMaxExportLines;

    public int MaxRecentReadBytes { get; set; } = 4 * 1024 * 1024;

    public int EmergencyMaxInnerDepth { get; set; } = 3;

    public int EmergencyReasonLength { get; set; } = 1024;

    public int EmergencyStackLength { get; set; } = 4096;

    public string? CustomLogDirectory { get; set; }

    public string? EmergencyDirectory { get; set; }

    public bool WriteFileEvents { get; set; } = true;

    public LogFileFormat FileFormat { get; set; } = LogFileFormat.Text;

    public long RotationTriggerBytes
    {
        get
        {
            double ratio = double.IsFinite(RotationTriggerRatio) ? RotationTriggerRatio : DefaultRotationTriggerRatio;
            ratio = Math.Clamp(ratio, 0.1d, 1.0d);
            long max = MaxFileBytes > 0 ? MaxFileBytes : DefaultMaxFileBytes;
            return (long)Math.Floor(max * ratio);
        }
    }

    public LogServiceOptions Clone()
    {
        return new LogServiceOptions
        {
            QueueCapacity = QueueCapacity,
            SubscriberQueueCapacity = SubscriberQueueCapacity,
            UiQueueCapacity = UiQueueCapacity,
            MaxQueuedBytes = MaxQueuedBytes,
            MaxMessageLength = MaxMessageLength,
            MaxPropertiesLength = MaxPropertiesLength,
            MaxDetailLength = MaxDetailLength,
            MaxFileBytes = MaxFileBytes,
            RotationTriggerRatio = RotationTriggerRatio,
            MaxFiles = MaxFiles,
            MaxDirectoryBytes = MaxDirectoryBytes,
            RetentionDays = RetentionDays,
            FlushIntervalMilliseconds = FlushIntervalMilliseconds,
            MaxExceptionDepth = MaxExceptionDepth,
            MaxEmergencyFileBytes = MaxEmergencyFileBytes,
            MaxEmergencyFiles = MaxEmergencyFiles,
            EmergencyRetentionDays = EmergencyRetentionDays,
            ReadRecentEventsLimit = ReadRecentEventsLimit,
            MaxExportLines = MaxExportLines,
            MaxRecentReadBytes = MaxRecentReadBytes,
            EmergencyMaxInnerDepth = EmergencyMaxInnerDepth,
            EmergencyReasonLength = EmergencyReasonLength,
            EmergencyStackLength = EmergencyStackLength,
            CustomLogDirectory = CustomLogDirectory,
            EmergencyDirectory = EmergencyDirectory,
            WriteFileEvents = WriteFileEvents,
            FileFormat = FileFormat
        };
    }

    public LogServiceOptions Normalize()
    {
        QueueCapacity = Clamp(QueueCapacity, 16, 1_000_000, DefaultQueueCapacity);
        SubscriberQueueCapacity = Clamp(SubscriberQueueCapacity, 16, 1_000_000, DefaultSubscriberQueueCapacity);
        UiQueueCapacity = Clamp(UiQueueCapacity, 16, 1_000_000, DefaultUiQueueCapacity);
        MaxQueuedBytes = Clamp(MaxQueuedBytes, 64 * 1024, 1 << 30, DefaultMaxQueuedBytes);
        MaxMessageLength = Clamp(MaxMessageLength, 64, 1 << 20, DefaultMaxMessageLength);
        MaxPropertiesLength = Clamp(MaxPropertiesLength, 64, 1 << 22, DefaultMaxPropertiesLength);
        MaxDetailLength = Clamp(MaxDetailLength, 64, 1 << 24, DefaultMaxDetailLength);
        MaxFileBytes = Clamp(MaxFileBytes, 4096, 1L << 40, DefaultMaxFileBytes);
        RotationTriggerRatio = double.IsFinite(RotationTriggerRatio) ? Math.Clamp(RotationTriggerRatio, 0.1d, 1.0d) : DefaultRotationTriggerRatio;
        MaxFiles = Clamp(MaxFiles, 1, 10_000, DefaultMaxFiles);
        MaxDirectoryBytes = Clamp(MaxDirectoryBytes, 4096, 1L << 46, DefaultMaxDirectoryBytes);
        RetentionDays = Clamp(RetentionDays, 1, 3650, DefaultRetentionDays);
        FlushIntervalMilliseconds = Clamp(FlushIntervalMilliseconds, 50, 600_000, DefaultFlushIntervalMilliseconds);
        MaxExceptionDepth = Clamp(MaxExceptionDepth, 1, 32, DefaultMaxExceptionDepth);
        MaxEmergencyFileBytes = Clamp(MaxEmergencyFileBytes, 1024, 1 << 24, DefaultMaxEmergencyFileBytes);
        MaxEmergencyFiles = Clamp(MaxEmergencyFiles, 1, 1000, DefaultMaxEmergencyFiles);
        EmergencyRetentionDays = Clamp(EmergencyRetentionDays, 1, 3650, DefaultEmergencyRetentionDays);
        ReadRecentEventsLimit = Clamp(ReadRecentEventsLimit, 1, 1_000_000, DefaultReadRecentEventsLimit);
        MaxExportLines = Clamp(MaxExportLines, 1, 1_000_000, DefaultMaxExportLines);
        MaxRecentReadBytes = Clamp(MaxRecentReadBytes, 4096, 1 << 30, 4 * 1024 * 1024);
        EmergencyMaxInnerDepth = EmergencyMaxInnerDepth < 0 ? 0 : Math.Min(EmergencyMaxInnerDepth, 8);
        EmergencyReasonLength = Clamp(EmergencyReasonLength, 64, 1 << 16, 1024);
        EmergencyStackLength = Clamp(EmergencyStackLength, 128, 1 << 20, 4096);
        return this;
    }

    private static int Clamp(int value, int min, int max, int fallback) =>
        value <= 0 ? Math.Clamp(fallback, min, max) : Math.Clamp(value, min, max);

    private static long Clamp(long value, long min, long max, long fallback) =>
        value <= 0 ? Math.Clamp(fallback, min, max) : Math.Clamp(value, min, max);
}
