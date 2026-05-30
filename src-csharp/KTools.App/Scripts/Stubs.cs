// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Scripts;

// Сборник заглушек для 11 оригинальных скриптов.
// Все они реализуют AbstractScript и используются для отладки навигации и автогенерации UI параметров.

public class VideoEncodingStub : AbstractScript
{
    public override string Name => "Кодирование видео";
    public override string Description => "Кодирование видео: изменение формата, вшивание субтитров, фильтрация тегов и настройка звука";
    public override string Category => "Видео";
    public override string IconName => "video";
    public override string[] FileExtensions => new[] { ".mp4", ".mkv", ".avi", ".ts" };
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
    public override string Name => "Конвертация контейнера";
    public override string Description => "Перемещение видео/аудио потоков в другой контейнер без перекодирования";
    public override string Category => "Видео";
    public override string IconName => "forward";
    public override string[] FileExtensions => new[] { ".mp4", ".mkv", ".avi", ".ts", ".m2ts" };
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
    public override string Name => "Кодирование аудио";
    public override string Description => "Перекодирование аудио в QAAC, AAC, FLAC, WAV, E-AC3, AC3 и др. с настройкой качества";
    public override string Category => "Аудио";
    public override string IconName => "music";
    public override string[] FileExtensions => new[] { ".wav", ".flac", ".m4a", ".aac", ".ac3", ".dts", ".mkv", ".mp4" };
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
    public override string Name => "Даунмикс в Stereo";
    public override string Description => "Даунмикс 5.1/7.1 в Stereo 2.0 (DDP/DD) через Dolby Encoding Engine";
    public override string Category => "Аудио";
    public override string IconName => "volume2";
    public override string[] FileExtensions => new[] { ".wav", ".flac", ".dts", ".ac3", ".mkv" };
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
    public override string Name => "Изменение скорости аудио";
    public override string Description => "Изменение скорости/тона аудио (PAL ↔ NTSC) с помощью eac3to.";
    public override string Category => "Аудио";
    public override string IconName => "sync";
    public override string[] FileExtensions => new[] { ".ac3", ".dts", ".wav", ".thd" };
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
    public override string Name => "Разделение каналов";
    public override string Description => "Разделение многоканального аудио на моно-WAV файлы с опциональной склейкой в стереопары";
    public override string Category => "Аудио";
    public override string IconName => "map";
    public override string[] FileExtensions => new[] { ".wav", ".flac", ".dts", ".ac3" };
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
    public override string Name => "Сборка MKV";
    public override string Description => "Сборка контейнера MKV из отдельных потоков видео, аудио и субтитров с сопоставлением по имени";
    public override string Category => "Контейнеры";
    public override string IconName => "add";
    public override string[] FileExtensions => new[] { ".mkv", ".mp4" };
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
    public override string Name => "Управление потоками";
    public override string Description => "Удаление или сохранение выбранных дорожек (видео, аудио, субтитры) в MKV и MP4 файлах.";
    public override string Category => "Контейнеры";
    public override string IconName => "list";
    public override string[] FileExtensions => new[] { ".mkv", ".mp4" };
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
    public override string Name => "Замена потоков";
    public override string Description => "Заменяет дорожки в MKV/MP4 на внешние файлы (видео, аудио, субтитры).";
    public override string Category => "Контейнеры";
    public override string IconName => "switch";
    public override string[] FileExtensions => new[] { ".mkv", ".mp4" };
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

public class ContainerDemuxStub : AbstractScript
{
    public override string Name => "Разборка контейнера";
    public override string Description => "Массовое извлечение потоков из контейнера с авто-именованием.";
    public override string Category => "Контейнеры";
    public override string IconName => "download";
    public override string[] FileExtensions => new[] { ".mkv", ".mp4" };
    public override string[] RequiredDependencies => new[] { "mkvtoolnix", "ffmpeg" };

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
            progressCallback(fileIndex, totalCount, $"Извлечение дорожек... {i * 10}%", i * 10.0);
            await Task.Delay(100);
        }
        return new List<string> { IsCancelled ? "⚠ Разборка отменена" : $"✅ Потоки успешно извлечены: {System.IO.Path.GetFileName(filePath)}" };
    }
}

public class SubtitlesConvertStub : AbstractScript
{
    public override string Name => "ASS/SRT → VTT";
    public override string Description => "Конвертация субтитров ASS/SSA/SRT в WebVTT с фильтрацией по актёрам и очисткой тегов.";
    public override string Category => "Субтитры";
    public override string IconName => "font";
    public override string[] FileExtensions => new[] { ".ass", ".ssa", ".srt" };

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
