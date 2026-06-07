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

public class ContainerConversionStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.ContainerConvName;
    public override string Description => AppConstants.ScriptMetadata.ContainerConvDesc;
    public override string Category => AppConstants.ScriptCategory.Video;
    public override string IconName => "forward";
    public override string[] FileExtensions => AppConstants.VideoContainers.ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField("target_format", "Формат контейнера", SettingType.Combo, "mkv", "Общие",
            options: new List<string> { "mkv", "mp4" }),
        new SettingField("copy_all", "Копировать все дорожки", SettingType.Checkbox, true, "Потоки")
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
            progressCallback(fileIndex, totalCount, $"Ремуксинг в контейнер... {i * 10}%", i * 10.0);
            await Task.Delay(100);
        }
        return new List<string> { IsCancelled ? "⚠ Ремуксинг отменен" : $"✅ Контейнер успешно конвертирован: {System.IO.Path.GetFileName(filePath)}" };
    }
}

public class AudioEncodingStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.AudioConverterName;
    public override string Description => AppConstants.ScriptMetadata.AudioConverterDesc;
    public override string Category => AppConstants.ScriptCategory.Audio;
    public override string IconName => "music";
    public override string[] FileExtensions => AppConstants.AudioContainers.Concat(AppConstants.AudioStreams).Concat(AppConstants.VideoContainers).ToArray();
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField("codec", "Аудиокодек", SettingType.Combo, "AAC", "Кодирование",
            options: new List<string> { "AAC", "QAAC", "FLAC", "AC3", "E-AC3" }),
        new SettingField("bitrate", "Битрейт (kbps)", SettingType.Int, 192, "Кодирование"),
        new SettingField("wav_depth", "Разрядность WAV", SettingType.Combo, "24-bit", "Экспорт WAV",
            options: new List<string> { "16-bit", "24-bit", "32-bit Float" })
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
            progressCallback(fileIndex, totalCount, $"Кодирование аудио... {i * 10}%", i * 10.0);
            await Task.Delay(150);
        }
        return new List<string> { IsCancelled ? "⚠ Кодирование отменено" : $"✅ Аудио успешно закодировано: {System.IO.Path.GetFileName(filePath)}" };
    }
}

public class AudioDownmixStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.AudioDownmixName;
    public override string Description => AppConstants.ScriptMetadata.AudioDownmixDesc;
    public override string Category => AppConstants.ScriptCategory.Audio;
    public override string IconName => "volume2";
    public override string[] FileExtensions => AppConstants.AudioContainers.Concat(AppConstants.AudioStreams).Concat(AppConstants.VideoContainers).ToArray();
    public override string[] RequiredDependencies => new[] { "dee", "ffmpeg" };

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
            progressCallback(fileIndex, totalCount, $"Даунмикс в Stereo через DEE... {i * 10}%", i * 10.0);
            await Task.Delay(200);
        }
        return new List<string> { IsCancelled ? "⚠ Даунмикс отменен" : $"✅ Даунмикс успешно выполнен: {System.IO.Path.GetFileName(filePath)}" };
    }
}

public class AudioSpeedStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.AudioSpeedName;
    public override string Description => AppConstants.ScriptMetadata.AudioSpeedDesc;
    public override string Category => AppConstants.ScriptCategory.Audio;
    public override string IconName => "sync";
    public override string[] FileExtensions => AppConstants.AudioContainers.Concat(AppConstants.AudioStreams).Concat(AppConstants.VideoContainers).ToArray();
    public override string[] RequiredDependencies => new[] { "eac3to" };

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField("conversion", "Направление конвертации", SettingType.Combo, "25.000 -> 23.976", "Настройки скорости",
            options: new List<string> { "25.000 -> 23.976", "23.976 -> 25.000", "24.000 -> 23.976" })
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
            progressCallback(fileIndex, totalCount, $"Изменение скорости в eac3to... {i * 10}%", i * 10.0);
            await Task.Delay(150);
        }
        return new List<string> { IsCancelled ? "⚠ Изменение скорости отменено" : $"✅ Скорость аудио успешно изменена: {System.IO.Path.GetFileName(filePath)}" };
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


public class SubtitlesConvertStub : AbstractScript
{
    public override string Name => AppConstants.ScriptMetadata.AssToVttName;
    public override string Description => AppConstants.ScriptMetadata.AssToVttDesc;
    public override string Category => AppConstants.ScriptCategory.Subtitles;
    public override string IconName => "font";
    public override string[] FileExtensions => AppConstants.SubtitleExtensions.ToArray();

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField("filter_by_actor", "Фильтровать по актерам", SettingType.Checkbox, false, "Фильтрация"),
        new SettingField("clean_tags", "Очищать теги оформления ASS", SettingType.Checkbox, true, "Очистка"),
        new SettingField("remove_caps", "Удалять CAPS-реплики", SettingType.Checkbox, false, "Очистка")
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
            progressCallback(fileIndex, totalCount, $"Конвертация субтитров... {i * 10}%", i * 10.0);
            await Task.Delay(100);
        }
        return new List<string> { IsCancelled ? "⚠ Конвертация отменена" : $"✅ Субтитры конвертированы: {System.IO.Path.GetFileName(filePath)}" };
    }
}
