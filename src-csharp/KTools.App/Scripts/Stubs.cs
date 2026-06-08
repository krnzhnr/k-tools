// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Scripts;

// Сборник заглушек для 11 оригинальных скриптов.
// Все они реализуют AbstractScript и используются для отладки навигации и автогенерации UI параметров.

public class VideoEncodingStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.VideoProcessorName;
    public override string Description => AppConstants.ScriptMetadata.VideoProcessorDesc;
    public override string Category => AppConstants.ScriptCategory.Video;
    public override string IconName => "video";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField("crf", "Показатель CRF (Качество)", SettingType.Int, 22, "Кодирование:Качество"),
        new SettingField("preset", "Пресет кодирования", SettingType.Combo, "slow", "Кодирование:Скорость",
            options: new List<string> { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" }),
        new SettingField("burn_subs", "Вшить субтитры (Burn-in)", SettingType.Checkbox, false, "Субтитры"),
        new SettingField("gpu_accel", "Аппаратное ускорение (NVENC)", SettingType.Checkbox, false, "Общие")
    };

    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        Action<int, int, string, double?> progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        for (int i = 0; i <= 10; i++)
        {
            if (IsCancelled) break;
            progressCallback(fileIndex, totalCount, $"Кодирование видео... {i * 10}%", i * 10.0);
            await Task.Delay(200);
        }
        return new List<string> { IsCancelled ? "⚠ Кодирование отменено" : $"✅ Видео успешно закодировано: {System.IO.Path.GetFileName(filePath)}" };
    }
}


public class AudioChannelsStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.AudioSplitName;
    public override string Description => AppConstants.ScriptMetadata.AudioSplitDesc;
    public override string Category => AppConstants.ScriptCategory.Audio;
    public override string IconName => "map";
    public override string[] FileExtensions => AppConstants.AudioContainers.Concat(AppConstants.AudioStreams).Concat(AppConstants.VideoContainers).ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        Action<int, int, string, double?> progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        for (int i = 0; i <= 10; i++)
        {
            if (IsCancelled) break;
            progressCallback(fileIndex, totalCount, $"Разделение аудио по каналам... {i * 10}%", i * 10.0);
            await Task.Delay(100);
        }
        return new List<string> { IsCancelled ? "⚠ Разделение отменено" : $"✅ Каналы успешно разделены: {System.IO.Path.GetFileName(filePath)}" };
    }
}

public class MkvAssemblyStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.MuxerName;
    public override string Description => AppConstants.ScriptMetadata.MuxerDesc;
    public override string Category => AppConstants.ScriptCategory.Containers;
    public override string IconName => "add";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "mkvtoolnix" };

    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        Action<int, int, string, double?> progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        for (int i = 0; i <= 10; i++)
        {
            if (IsCancelled) break;
            progressCallback(fileIndex, totalCount, $"Сборка MKV в mkvmerge... {i * 10}%", i * 10.0);
            await Task.Delay(100);
        }
        return new List<string> { IsCancelled ? "⚠ Сборка отменена" : $"✅ Контейнер MKV собран: {System.IO.Path.GetFileName(filePath)}" };
    }
}

public class StreamManagementStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.StreamMgrName;
    public override string Description => AppConstants.ScriptMetadata.StreamMgrDesc;
    public override string Category => AppConstants.ScriptCategory.Containers;
    public override string IconName => "list";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "mkvtoolnix" };

    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        Action<int, int, string, double?> progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        for (int i = 0; i <= 10; i++)
        {
            if (IsCancelled) break;
            progressCallback(fileIndex, totalCount, $"Фильтрация потоков медиа... {i * 10}%", i * 10.0);
            await Task.Delay(100);
        }
        return new List<string> { IsCancelled ? "⚠ Отфильтровано" : $"✅ Потоки отфильтрованы: {System.IO.Path.GetFileName(filePath)}" };
    }
}

public class StreamReplacementStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.StreamReplName;
    public override string Description => AppConstants.ScriptMetadata.StreamReplDesc;
    public override string Category => AppConstants.ScriptCategory.Containers;
    public override string IconName => "switch";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "mkvtoolnix" };

    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        Action<int, int, string, double?> progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        for (int i = 0; i <= 10; i++)
        {
            if (IsCancelled) break;
            progressCallback(fileIndex, totalCount, $"Замена дорожек... {i * 10}%", i * 10.0);
            await Task.Delay(100);
        }
        return new List<string> { IsCancelled ? "⚠ Замена отменена" : $"✅ Дорожки заменены: {System.IO.Path.GetFileName(filePath)}" };
    }
}



