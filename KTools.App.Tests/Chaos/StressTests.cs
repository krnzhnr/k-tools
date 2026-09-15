// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Encoders;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Chaos;

/// <summary>
/// Хаос-стресс-тесты: реестр всех 17 скриптов, тысячи сообщений мессенджера,
/// конкурентная запись настроек, большие структуры данных.
/// Класс помечен DoNotParallelize: работает с глобальным мессенджером
/// и реальными файлами настроек.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class StressTests
{
    /// <summary>
    /// Создаёт все реальные скрипты приложения через DI-контейнер
    /// с моками внешних зависимостей. Заодно верифицирует разрешение
    /// всех зарегистрированных сервисов без падений в рантайме.
    /// </summary>
    private static List<AbstractScript> CreateAllRealScripts(
        Mock<ILogService> logMock,
        Mock<ISettingsManager> settingsMock,
        Mock<IPathManager> pathMock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogService>(logMock.Object);
        services.AddSingleton<ISettingsManager>(settingsMock.Object);
        services.AddSingleton<IPathManager>(pathMock.Object);
        services.AddSingleton(MockBuilders.CreateMediaProbeMock().Object);
        services.AddSingleton(MockBuilders.CreateUpdateMock().Object);
        services.AddSingleton(MockBuilders.CreateDependencyManagerMock().Object);
        services.AddSingleton(MockBuilders.CreateDialogMock().Object);
        services.AddSingleton(new Mock<IAudioWaveformService>().Object);

        services.AddSingleton<Scripts.MetadataCleanupScript>();
        services.AddSingleton<Scripts.VideoEncodingScript>();
        services.AddSingleton<Scripts.ContainerConversionScript>();
        services.AddSingleton<Scripts.AudioEncodingScript>();
        services.AddSingleton<Scripts.AudioDownmixScript>();
        services.AddSingleton<Scripts.AudioSpeedScript>();
        services.AddSingleton<Scripts.AudioChannelsScript>();
        services.AddSingleton<Scripts.AudioShiftScript>();
        services.AddSingleton<Scripts.AudioTransplantScript>();
        services.AddSingleton<Scripts.MkvAssemblyScript>();
        services.AddSingleton<Scripts.StreamManagementScript>();
        services.AddSingleton<Scripts.StreamReplacementScript>();
        services.AddSingleton<Scripts.TrackExtractorScript>();
        services.AddSingleton<Scripts.SubtitlesConvertScript>();
        services.AddSingleton<Scripts.SubtitleShiftScript>();
        services.AddSingleton<Scripts.SpeechRecognitionScript>();
        services.AddSingleton<Scripts.MediaDownloaderScript>();
        services.AddSingleton<Scripts.BitrateViewerScript>();

        // Раннеры и парсеры — моки по контрактам
        services.AddSingleton(new Mock<IFFmpegRunner>().Object);
        services.AddSingleton(new Mock<IMkvmergeRunner>().Object);
        services.AddSingleton(new Mock<IAssParser>().Object);
        services.AddSingleton(new Mock<IBitrateAnalyzerService>().Object);
        services.AddSingleton(new Mock<IDiskTypeDetectorService>().Object);
        services.AddSingleton(new Mock<IEac3toRunner>().Object);
        services.AddSingleton(new Mock<IWhisperRunner>().Object);
        services.AddSingleton(new Mock<IWhisperModelManager>().Object);
        services.AddSingleton(new Mock<IDependencyManager>().Object);
        services.AddSingleton(new Mock<IDialogService>().Object);

        // Конкретные раннеры — реальные экземпляры с моками зависимостей
        // (sealed-классы; внешние процессы не запускаются при конструировании)
        services.AddSingleton(new KTools_App.Infrastructure.QaacRunner(
            logMock.Object, pathMock.Object));
        services.AddSingleton(new KTools_App.Infrastructure.DeeRunner(
            logMock.Object,
            new Mock<IFFmpegRunner>().Object,
            pathMock.Object));

        // Реестр видеокодировщиков: Nvenc/X265 + кэш возможностей
        services.AddSingleton(new Mock<IHardwareCapabilityCache>().Object);
        services.AddSingleton<KTools_App.Encoders.IVideoEncoder, KTools_App.Encoders.NvencEncoder>();
        services.AddSingleton<KTools_App.Encoders.IVideoEncoder, KTools_App.Encoders.X265Encoder>();
        services.AddSingleton<KTools_App.Encoders.VideoEncoderRegistry>();

        // Каждому конкретному типу скрипта нужна и регистрация по базовому
        // типу — для GetServices<AbstractScript>() при сборе полного списка
        var provider = services.BuildServiceProvider();
        return new List<AbstractScript>
        {
            provider.GetRequiredService<Scripts.MetadataCleanupScript>(),
            provider.GetRequiredService<Scripts.VideoEncodingScript>(),
            provider.GetRequiredService<Scripts.ContainerConversionScript>(),
            provider.GetRequiredService<Scripts.AudioEncodingScript>(),
            provider.GetRequiredService<Scripts.AudioDownmixScript>(),
            provider.GetRequiredService<Scripts.AudioSpeedScript>(),
            provider.GetRequiredService<Scripts.AudioChannelsScript>(),
            provider.GetRequiredService<Scripts.AudioShiftScript>(),
            provider.GetRequiredService<Scripts.AudioTransplantScript>(),
            provider.GetRequiredService<Scripts.MkvAssemblyScript>(),
            provider.GetRequiredService<Scripts.StreamManagementScript>(),
            provider.GetRequiredService<Scripts.StreamReplacementScript>(),
            provider.GetRequiredService<Scripts.TrackExtractorScript>(),
            provider.GetRequiredService<Scripts.SubtitlesConvertScript>(),
            provider.GetRequiredService<Scripts.SubtitleShiftScript>(),
            provider.GetRequiredService<Scripts.SpeechRecognitionScript>(),
            provider.GetRequiredService<Scripts.MediaDownloaderScript>(),
            provider.GetRequiredService<Scripts.BitrateViewerScript>(),
        };
    }

    /// <summary>
    /// Стресс: реестр из всех 18 реальных скриптов инициализируется без
    /// исключений, имена уникальны, каждый находим по имени.
    /// </summary>
    [TestMethod]
    public void ScriptRegistry_AllRealScripts_RegisteredAndFindable()
    {
        // Arrange
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var pathMock = MockBuilders.CreatePathManagerMock();
        var scripts = CreateAllRealScripts(logMock, settingsMock, pathMock);

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsManager>(settingsMock.Object);
        services.AddSingleton<ILogService>(logMock.Object);
        foreach (var script in scripts)
        {
            services.AddSingleton(script);
        }

        // ScriptRegistry резолвит каждый конкретный тип скрипта —
        // регистрируем конкретные типы, взяв их из уже созданного списка
        services.AddSingleton<Scripts.MetadataCleanupScript>(_ =>
            (Scripts.MetadataCleanupScript)scripts[0]);
        services.AddSingleton<Scripts.VideoEncodingScript>(_ =>
            (Scripts.VideoEncodingScript)scripts[1]);
        services.AddSingleton<Scripts.ContainerConversionScript>(_ =>
            (Scripts.ContainerConversionScript)scripts[2]);
        services.AddSingleton<Scripts.AudioEncodingScript>(_ =>
            (Scripts.AudioEncodingScript)scripts[3]);
        services.AddSingleton<Scripts.AudioDownmixScript>(_ =>
            (Scripts.AudioDownmixScript)scripts[4]);
        services.AddSingleton<Scripts.AudioSpeedScript>(_ =>
            (Scripts.AudioSpeedScript)scripts[5]);
        services.AddSingleton<Scripts.AudioChannelsScript>(_ =>
            (Scripts.AudioChannelsScript)scripts[6]);
        services.AddSingleton<Scripts.AudioShiftScript>(_ =>
            (Scripts.AudioShiftScript)scripts[7]);
        services.AddSingleton<Scripts.AudioTransplantScript>(_ =>
            (Scripts.AudioTransplantScript)scripts[8]);
        services.AddSingleton<Scripts.MkvAssemblyScript>(_ =>
            (Scripts.MkvAssemblyScript)scripts[9]);
        services.AddSingleton<Scripts.StreamManagementScript>(_ =>
            (Scripts.StreamManagementScript)scripts[10]);
        services.AddSingleton<Scripts.StreamReplacementScript>(_ =>
            (Scripts.StreamReplacementScript)scripts[11]);
        services.AddSingleton<Scripts.TrackExtractorScript>(_ =>
            (Scripts.TrackExtractorScript)scripts[12]);
        services.AddSingleton<Scripts.SubtitlesConvertScript>(_ =>
            (Scripts.SubtitlesConvertScript)scripts[13]);
        services.AddSingleton<Scripts.SubtitleShiftScript>(_ =>
            (Scripts.SubtitleShiftScript)scripts[14]);
        services.AddSingleton<Scripts.SpeechRecognitionScript>(_ =>
            (Scripts.SpeechRecognitionScript)scripts[15]);
        services.AddSingleton<Scripts.MediaDownloaderScript>(_ =>
            (Scripts.MediaDownloaderScript)scripts[16]);
        services.AddSingleton<Scripts.BitrateViewerScript>(_ =>
            (Scripts.BitrateViewerScript)scripts[17]);
        services.AddSingleton<IScriptRegistry, ScriptRegistry>();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IScriptRegistry>();

        // Assert
        registry.Scripts.Should().HaveCount(18,
            "все 18 скриптов должны быть зарегистрированы");
        registry.Scripts.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().HaveCount(18, "имена скриптов обязаны быть уникальны");

        // Каждый скрипт находим по собственному имени
        foreach (var script in registry.Scripts)
        {
            registry.GetScriptByName(script.Name).Should().NotBeNull(
                $"скрипт '{script.Name}' обязан находиться по собственному имени");
        }

        registry.GetScriptByName("Несуществующий скрипт 999").Should().BeNull();
    }

    /// <summary>
    /// Стресс: константы имён из AppConstants.ScriptMetadata соответствуют
    /// реальным скриптам реестра — сетка против переименований
    /// (изменение имени скрипта ломает legacy-навигацию и настройки).
    /// </summary>
    [TestMethod]
    public void ScriptRegistry_NamesMatchAppConstants_NoDrift()
    {
        // Arrange
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var pathMock = MockBuilders.CreatePathManagerMock();
        var scripts = CreateAllRealScripts(logMock, settingsMock, pathMock);
        var names = scripts.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Act / Assert
        string[] expectedNames =
        {
            AppConstants.ScriptMetadata.VideoProcessorName,
            AppConstants.ScriptMetadata.ContainerConvName,
            AppConstants.ScriptMetadata.MetadataCleanName,
            AppConstants.ScriptMetadata.AudioConverterName,
            AppConstants.ScriptMetadata.AudioDownmixName,
            AppConstants.ScriptMetadata.AudioSpeedName,
            AppConstants.ScriptMetadata.AudioSplitName,
            AppConstants.ScriptMetadata.AudioShiftName,
            AppConstants.ScriptMetadata.AudioTransplantName,
            AppConstants.ScriptMetadata.MuxerName,
            AppConstants.ScriptMetadata.StreamMgrName,
            AppConstants.ScriptMetadata.StreamReplName,
            AppConstants.ScriptMetadata.TrackExtrName,
            AppConstants.ScriptMetadata.AssToVttName,
            AppConstants.ScriptMetadata.SubtitleShiftName,
            AppConstants.ScriptMetadata.BitrateViewerName,
        };

        foreach (string expected in expectedNames)
        {
            names.Should().Contain(expected,
                $"константа '{expected}' обязана соответствовать реальному имени скрипта");
        }
    }

    /// <summary>
    /// Стресс: 100 получателей × 500 сообщений — все доставлены,
    /// GC не ломает weak-подписки живых объектов.
    /// </summary>
    [TestMethod]
    public void WeakReferenceMessenger_ManySubscribersAllDelivered()
    {
        // Arrange
        MessengerIsolation.ResetMessenger();
        try
        {
            const int subscribers = 100;
            const int messages = 500;
            var counters = new int[subscribers];
            var recipients = new List<object>();

            for (int i = 0; i < subscribers; i++)
            {
                int idx = i;
                var recipient = new object();
                WeakReferenceMessenger.Default.Register<string>(
                    recipient, (_, _) => Interlocked.Increment(ref counters[idx]));
                recipients.Add(recipient);
            }

            // Act
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            for (int m = 0; m < messages; m++)
            {
                WeakReferenceMessenger.Default.Send($"msg{m}");
            }

            // Assert
            counters.Should().OnlyContain(c => c == messages,
                $"каждый из {subscribers} живых получателей обязан получить все {messages} сообщений");
        }
        finally
        {
            MessengerIsolation.ResetMessenger();
        }
    }

    /// <summary>
    /// Стресс: реальные SettingsManager — round-trip всех типов значений.
    /// </summary>
    [TestMethod]
    public void SettingsManager_AllTypes_RoundTrip()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        var settings = new SettingsManager(
            MockBuilders.CreateLogServiceMock().Object,
            CreatePathManagerForDir(tempDir).Object);

        // Act / Assert
        settings.SetSetting("Group", "k_bool", true);
        settings.SetSetting("Group", "k_int", 42);
        settings.SetSetting("Group", "k_string", "значение");
        settings.SetSetting("Group", "k_double", 3.14);

        settings.GetSetting("Group", "k_bool", false).Should().BeTrue();
        settings.GetSetting("Group", "k_int", 0).Should().Be(42);
        settings.GetSetting("Group", "k_string", string.Empty).Should().Be("значение");
        settings.GetSetting("Group", "k_double", 0.0).Should().Be(3.14);

        // значения по умолчанию для отсутствующих ключей
        settings.GetSetting("Group", "missing", "fallback").Should().Be("fallback");
        settings.GetSetting("MissingGroup", "k", "fallback").Should().Be("fallback");
    }

    /// <summary>
    /// Стресс: реальные SettingsManager — сохранение и загрузка состояния
    /// через SaveSettings + новый экземпляр в той же папке.
    /// </summary>
    [TestMethod]
    public void SettingsManager_SaveAndReload_PersistsAcrossInstances()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        var pathMock = CreatePathManagerForDir(tempDir);

        var first = new SettingsManager(
            MockBuilders.CreateLogServiceMock().Object,
            pathMock.Object);
        first.SetSetting("Group", "key", "persisted");
        first.SaveSettings();

        // Act — новый экземпляр читает ту же папку настроек
        var second = new SettingsManager(
            MockBuilders.CreateLogServiceMock().Object,
            pathMock.Object);

        // Assert
        second.GetSetting("Group", "key", string.Empty).Should().Be("persisted",
            "настройки обязаны переживать пересоздание менеджера");
    }

    /// <summary>
    /// Стресс: конкурентная запись 100 настроек из 10 потоков — после
    /// SaveSettings и перечитывания все ключи должны присутствовать.
    /// </summary>
    [TestMethod]
    public async Task SettingsManager_ConcurrentWrites_NoKeysLost()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        var pathMock = CreatePathManagerForDir(tempDir);
        var settings = new SettingsManager(
            MockBuilders.CreateLogServiceMock().Object,
            pathMock.Object);
        const int threads = 10;
        const int perThread = 10;

        // Act — конкурентная запись разных ключей
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                int key = (t * perThread) + i;
                settings.SetSetting("Stress", $"key_{key}", $"value_{key}");
            }
        }));
        await Task.WhenAll(tasks);
        settings.SaveSettings();

        // Assert — новый экземпляр видит все ключи
        var reloaded = new SettingsManager(
            MockBuilders.CreateLogServiceMock().Object,
            pathMock.Object);
        for (int key = 0; key < threads * perThread; key++)
        {
            reloaded.GetSetting("Stress", $"key_{key}", string.Empty)
                .Should().Be($"value_{key}",
                    $"ключ key_{key} не должен теряться при конкурентной записи");
        }
    }

    /// <summary>
    /// Стресс: MediaStructure с 1000 треков — доступ к свойствам без
    /// исключений, полнота данных сохраняется.
    /// </summary>
    [TestMethod]
    public void MediaStructure_1000Tracks_FunctionalIntegrity()
    {
        // Arrange
        var structure = new MediaStructure { FilePath = "C:\\big.mkv" };
        for (int i = 0; i < 1000; i++)
        {
            structure.Tracks.Add(new MediaTrack
            {
                TrackId = i,
                TrackType = i % 3 == 0 ? "video" : i % 3 == 1 ? "audio" : "subtitles",
                Codec = "h264",
            });
        }

        // Act / Assert
        structure.Tracks.Should().HaveCount(1000);
        structure.Tracks.Count(t => t.TrackType == "video").Should().Be(334,
            "распределение 1000 треков по типам должно сохранять точность данных");
    }

    private static Mock<IPathManager> CreatePathManagerForDir(TempDirectoryScope scope)
    {
        var mock = new Mock<IPathManager>();
        mock.Setup(p => p.GetSettingsDirectory()).Returns(scope.RootPath);
        mock.Setup(p => p.GetBaseDirectory()).Returns(scope.RootPath);
        mock.Setup(p => p.GetBinDirectory())
            .Returns(System.IO.Path.Combine(scope.RootPath, "bin"));
        mock.Setup(p => p.GetBinaryPath(It.IsAny<string>()))
            .Returns<string>(b => System.IO.Path.Combine(scope.RootPath, "bin", b));
        mock.Setup(p => p.GetShortPath(It.IsAny<string>())).Returns<string>(p => p);
        return mock;
    }
}
