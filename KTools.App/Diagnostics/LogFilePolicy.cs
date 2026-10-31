// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace KTools_App.Diagnostics;

public sealed class LogFilePolicy
{
    public const string FileNamePrefix = "ktools_";
    public const string FileNameExtension = ".log";
    public const string FileSearchPattern = FileNamePrefix + "*" + FileNameExtension;
    public const string LogsFolderName = "logs";
    public const string ApplicationFolderName = "KTools";
    public const string SegmentMarker = ".part";

    private const string WritableProbeFileName = ".ktools_write_probe";

    private readonly LogServiceOptions _options;

    public LogFilePolicy(LogServiceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public long RotationTriggerBytes => _options.RotationTriggerBytes;

    public static string GetFallbackDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, ApplicationFolderName, LogsFolderName);
    }

    public string BuildFileName(DateTimeOffset timestampUtc, int processId, Guid sessionId)
    {
        return FileNamePrefix
            + timestampUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
            + "_" + processId.ToString(CultureInfo.InvariantCulture)
            + "_" + sessionId.ToString("N", CultureInfo.InvariantCulture)
            + FileNameExtension;
    }

    public string BuildBaseName(DateTimeOffset timestampUtc, int processId, Guid sessionId)
    {
        string fileName = BuildFileName(timestampUtc, processId, sessionId);
        return fileName[..^FileNameExtension.Length];
    }

    public string? ResolveDirectory(string? requestedDirectory, out string? rejectionReason)
    {
        rejectionReason = null;
        List<string> candidates = new(3);

        if (!string.IsNullOrWhiteSpace(requestedDirectory))
        {
            candidates.Add(requestedDirectory.Trim());
        }
        else
        {
            string baseDirectory = AppContext.BaseDirectory;
            if (IsDirectoryWritable(baseDirectory))
            {
                candidates.Add(Path.Combine(baseDirectory, LogsFolderName));
            }
        }

        string fallback = GetFallbackDirectory();
        if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(fallback);
        }

        string? firstFailure = null;
        foreach (string candidate in candidates)
        {
            if (TryEnsureDirectory(candidate))
            {
                if (firstFailure is not null)
                {
                    rejectionReason = firstFailure;
                }

                return candidate;
            }

            firstFailure ??= candidate;
        }

        rejectionReason = firstFailure ?? "none";
        return null;
    }

    public IReadOnlyList<string> EnumerateSessionFiles(string directory, string baseName)
    {
        return EnumerateSessionFiles(directory, baseName, out _);
    }

    public IReadOnlyList<string> EnumerateSessionFiles(string directory, string baseName, out bool completed)
    {
        List<string> result = new(4);
        completed = false;
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName))
        {
            return result;
        }

        try
        {
            List<int> segments = new(4);
            string[] files = Directory.GetFiles(directory, baseName + SegmentMarker + "*" + FileNameExtension);
            foreach (string file in files)
            {
                if (TryParseSegmentIndex(file, out int index))
                {
                    segments.Add(index);
                }
            }

            segments.Sort();
            foreach (int index in segments)
            {
                string path = BuildSegmentPath(directory, baseName, index);
                if (File.Exists(path))
                {
                    result.Add(path);
                }
            }

            string live = Path.Combine(directory, baseName + FileNameExtension);
            if (File.Exists(live))
            {
                result.Add(live);
            }

            completed = true;
        }
        catch (Exception)
        {
            result.Clear();
        }

        return result;
    }

    public int ApplyRetention(string directory, string? protectedPath = null)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            List<FileInfo> files = CollectFiles(directory);
            if (files.Count == 0)
            {
                return 0;
            }

            bool IsProtected(FileInfo file) => IsProtectedFile(file, protectedPath);

            DateTime cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            foreach (FileInfo file in files)
            {
                if (file.LastWriteTimeUtc >= cutoff || IsProtected(file))
                {
                    continue;
                }

                deleted += TryDeletePath(file.FullName) ? 1 : 0;
                file.Refresh();
            }

            files.RemoveAll(f => !f.Exists);
            files.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            while (files.Count > _options.MaxFiles)
            {
                int index = IndexOfFirstEvictable(files, IsProtected);
                if (index < 0)
                {
                    break;
                }

                if (!TryDeletePath(files[index].FullName))
                {
                    break;
                }

                files.RemoveAt(index);
                deleted++;
            }

            deleted += EvictToDirectoryBudget(files, IsProtected);
        }
        catch (Exception)
        {
            return deleted;
        }

        return deleted;
    }

    private int EvictToDirectoryBudget(List<FileInfo> files, Func<FileInfo, bool> isProtected)
    {
        long budget = _options.MaxDirectoryBytes;
        if (budget <= 0 || files.Count < 2)
        {
            return 0;
        }

        long total = 0;
        foreach (FileInfo file in files)
        {
            total += SafeLength(file);
        }

        int deleted = 0;
        for (int index = 0; index < files.Count - 1 && total > budget; index++)
        {
            FileInfo candidate = files[index];
            if (isProtected(candidate))
            {
                continue;
            }

            long size = SafeLength(candidate);
            if (!TryDeletePath(candidate.FullName))
            {
                continue;
            }

            total -= size;
            files.RemoveAt(index);
            index--;
            deleted++;
        }

        return deleted;
    }

    private static bool IsProtectedFile(FileInfo file, string? protectedPath)
    {
        return MatchesProtected(file, protectedPath) || IsFileInUse(file.FullName);
    }

    private static bool MatchesProtected(FileInfo file, string? protectedPath)
    {
        if (string.IsNullOrEmpty(protectedPath))
        {
            return false;
        }

        return string.Equals(file.FullName, protectedPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(file.FullName), protectedPath, StringComparison.OrdinalIgnoreCase);
    }

    public string? Rotate(string liveFilePath, out string? error)
    {
        error = null;
        try
        {
            string directory = Path.GetDirectoryName(liveFilePath) ?? string.Empty;
            string fileName = Path.GetFileName(liveFilePath);
            string baseName = fileName.EndsWith(FileNameExtension, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^FileNameExtension.Length]
                : fileName;
            int capacity = _options.MaxFiles - 1;
            bool hasLive = File.Exists(liveFilePath);
            string[] existing = Directory.GetFiles(directory, baseName + SegmentMarker + "*" + FileNameExtension);
            List<int> indices = new(existing.Length);
            foreach (string file in existing)
            {
                if (TryParseSegmentIndex(file, out int index))
                {
                    indices.Add(index);
                }
                else if (!TryDeletePath(file))
                {
                    error = "invalid-segment-delete-failed";
                    return null;
                }
            }

            indices.Sort();
            if (capacity < 1)
            {
                if (hasLive && !TryDeletePath(liveFilePath))
                {
                    error = "live-delete-failed";
                    return null;
                }

                foreach (int index in indices)
                {
                    if (!TryDeletePath(BuildSegmentPath(directory, baseName, index)))
                    {
                        error = "segment-delete-failed";
                        return null;
                    }
                }

                return liveFilePath;
            }

            int keepCount = Math.Min(indices.Count, hasLive ? capacity - 1 : capacity);
            HashSet<int> kept = new(indices.Skip(indices.Count - keepCount));
            foreach (int index in indices)
            {
                if (!kept.Contains(index) && !TryDeletePath(BuildSegmentPath(directory, baseName, index)))
                {
                    error = "segment-delete-failed";
                    return null;
                }
            }

            List<string> temporary = new(keepCount);
            try
            {
                for (int index = 0; index < keepCount; index++)
                {
                    int sourceIndex = indices[indices.Count - keepCount + index];
                    string source = BuildSegmentPath(directory, baseName, sourceIndex);
                    string target = Path.Combine(directory, baseName + SegmentMarker + ".compact-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                    File.Move(source, target, overwrite: false);
                    temporary.Add(target);
                }

                for (int index = 0; index < temporary.Count; index++)
                {
                    File.Move(temporary[index], BuildSegmentPath(directory, baseName, index + 1), overwrite: true);
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                foreach (string path in temporary)
                {
                    TryDeletePath(path);
                }

                return null;
            }

            if (hasLive)
            {
                File.Move(liveFilePath, BuildSegmentPath(directory, baseName, keepCount + 1), overwrite: true);
            }

            return liveFilePath;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return null;
        }
    }

    public static string BuildSegmentPath(string directory, string baseName, int index)
    {
        return Path.Combine(directory, baseName + SegmentMarker + index.ToString(CultureInfo.InvariantCulture) + FileNameExtension);
    }

    private static bool TryParseSegmentIndex(string path, out int index)
    {
        index = 0;
        string name = Path.GetFileNameWithoutExtension(path);
        int marker = name.LastIndexOf(SegmentMarker, StringComparison.Ordinal);
        return marker >= 0
            && int.TryParse(name[(marker + SegmentMarker.Length)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
            && index > 0;
    }

    private static int IndexOfFirstEvictable(List<FileInfo> files, Func<FileInfo, bool> isProtected)
    {
        for (int index = 0; index < files.Count; index++)
        {
            if (!isProtected(files[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static List<FileInfo> CollectFiles(string directory)
    {
        List<FileInfo> files = new(8);
        string[] paths = Directory.GetFiles(directory, FileSearchPattern);
        foreach (string path in paths)
        {
            try
            {
                files.Add(new FileInfo(path));
            }
            catch (Exception)
            {
            }
        }

        return files;
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            file.Refresh();
            return file.Exists ? file.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool IsFileInUse(string path)
    {
        try
        {
            using FileStream probe = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryDeletePath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, WritableProbeFileName + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            string probe = Path.Combine(path, WritableProbeFileName + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
