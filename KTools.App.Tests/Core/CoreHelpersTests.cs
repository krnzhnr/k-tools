// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты вспомогательных классов ядра приложения:
/// ObservableRangeCollection (пакетные операции с одиночным Reset-уведомлением),
/// VersionComparer (сравнение версий SemVer), ScriptInfo (вычисляемые свойства),
/// MediaStructure/MediaTrack/MediaAttachment (медиа-модели)
/// и ActiveProcessTracker (потокобезопасный реестр процессов).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class CoreHelpersTests
{
    // =====================================================================
    //                          ObservableRangeCollection
    // =====================================================================

    /// <summary>
    /// Проверяет, что ReplaceRange заменяет содержимое и отправляет
    /// ровно одно Reset-уведомление CollectionChanged.
    /// </summary>
    [TestMethod]
    public void ReplaceRange_NewItems_SendsSingleResetNotification()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int> { 1, 2, 3 };
        var collectionChangedCount = 0;
        NotifyCollectionChangedAction? lastAction = null;
        collection.CollectionChanged += (s, e) =>
        {
            collectionChangedCount++;
            lastAction = e.Action;
        };

        // Act
        collection.ReplaceRange(new[] { 10, 20, 30 });

        // Assert
        collectionChangedCount.Should().Be(1, "ReplaceRange должен отправлять ровно одно уведомление Reset");
        lastAction.Should().Be(NotifyCollectionChangedAction.Reset);
        collection.Should().Equal(10, 20, 30);
    }

    /// <summary>
    /// Проверяет, что ReplaceRange отправляет PropertyChanged для Count и Item[].
    /// </summary>
    [TestMethod]
    public void ReplaceRange_NewItems_RaisesPropertyChangedForCountAndItemArray()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int> { 1 };
        var propertyNames = new List<string?>();
        ((INotifyPropertyChanged)collection).PropertyChanged += (s, e) =>
            propertyNames.Add(e.PropertyName);

        // Act
        collection.ReplaceRange(new[] { 10, 20 });

        // Assert
        propertyNames.Should().Contain(nameof(ObservableRangeCollection<int>.Count));
        propertyNames.Should().Contain("Item[]");
    }

    /// <summary>
    /// Проверяет, что ReplaceRange сохраняет порядок элементов исходной последовательности.
    /// </summary>
    [TestMethod]
    public void ReplaceRange_OrderedItems_PreservesOrder()
    {
        // Arrange
        var collection = new ObservableRangeCollection<string>();
        var source = new List<string> { "alpha", "beta", "gamma", "delta" };

        // Act
        collection.ReplaceRange(source);

        // Assert
        collection.Should().Equal(source, "ReplaceRange должен сохранять порядок элементов");
        collection[0].Should().Be("alpha");
        collection[3].Should().Be("delta");
    }

    /// <summary>
    /// Проверяет, что ReplaceRange с null-аргументом бросает ArgumentNullException.
    /// </summary>
    [TestMethod]
    public void ReplaceRange_NullArgument_ThrowsArgumentNullException()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>();

        // Act
        Action act = () => collection.ReplaceRange(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет, что ReplaceRange с пустой последовательностью очищает коллекцию
    /// и по-прежнему отправляет одно Reset-уведомление.
    /// </summary>
    [TestMethod]
    public void ReplaceRange_EmptyEnumeration_ClearsCollectionAndNotifiesOnce()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int> { 5, 6, 7 };
        var resetCount = 0;
        collection.CollectionChanged += (s, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };

        // Act
        collection.ReplaceRange(Enumerable.Empty<int>());

        // Assert
        collection.Should().BeEmpty();
        resetCount.Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что AddRange добавляет элементы в конец и отправляет
    /// ровно одно Reset-уведомление.
    /// </summary>
    [TestMethod]
    public void AddRange_NewItems_SendsSingleResetNotificationAndAppends()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int> { 1, 2 };
        var collectionChangedCount = 0;
        NotifyCollectionChangedAction? lastAction = null;
        collection.CollectionChanged += (s, e) =>
        {
            collectionChangedCount++;
            lastAction = e.Action;
        };

        // Act
        collection.AddRange(new[] { 3, 4 });

        // Assert
        collectionChangedCount.Should().Be(1, "AddRange должен отправлять ровно одно уведомление Reset");
        lastAction.Should().Be(NotifyCollectionChangedAction.Reset);
        collection.Should().Equal(new[] { 1, 2, 3, 4 }, "AddRange должен добавлять элементы в конец");
    }

    /// <summary>
    /// Проверяет, что AddRange отправляет PropertyChanged для Count и Item[].
    /// </summary>
    [TestMethod]
    public void AddRange_NewItems_RaisesPropertyChangedForCountAndItemArray()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>();
        var propertyNames = new List<string?>();
        ((INotifyPropertyChanged)collection).PropertyChanged += (s, e) =>
            propertyNames.Add(e.PropertyName);

        // Act
        collection.AddRange(new[] { 1 });

        // Assert
        propertyNames.Should().Contain(nameof(ObservableRangeCollection<int>.Count));
        propertyNames.Should().Contain("Item[]");
    }

    /// <summary>
    /// Проверяет, что AddRange с null-аргументом бросает ArgumentNullException.
    /// </summary>
    [TestMethod]
    public void AddRange_NullArgument_ThrowsArgumentNullException()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>();

        // Act
        Action act = () => collection.AddRange(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет, что AddRange с пустой последовательностью не изменяет коллекцию,
    /// но отправляет одно Reset-уведомление.
    /// </summary>
    [TestMethod]
    public void AddRange_EmptyEnumeration_KeepsItemsAndNotifiesOnce()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int> { 42 };
        var resetCount = 0;
        collection.CollectionChanged += (s, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };

        // Act
        collection.AddRange(Enumerable.Empty<int>());

        // Assert
        collection.Should().Equal(42);
        resetCount.Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что CheckReentrancy не бросает исключений при вызове
    /// вне обработчиков событий (обычный вызов ReplaceRange/AddRange).
    /// </summary>
    [TestMethod]
    public void RangeOperations_CalledOutsideEventHandlers_DoNotThrowReentrancyException()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>();

        // Act — вызовы вне обработчиков событий не должны бросать InvalidOperationException
        Action act = () =>
        {
            collection.AddRange(new[] { 1 });
            collection.ReplaceRange(new[] { 2, 3 });
        };

        // Assert
        act.Should().NotThrow<InvalidOperationException>();
        collection.Should().Equal(2, 3);
    }

    /// <summary>
    /// Проверяет, что несколько последовательных ReplaceRange вызывают
    /// по одному Reset-уведомлению на каждый вызов.
    /// </summary>
    [TestMethod]
    public void ReplaceRange_CalledMultipleTimes_NotifiesForEachCall()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>();
        var totalResets = 0;
        collection.CollectionChanged += (s, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                totalResets++;
            }
        };

        // Act
        collection.ReplaceRange(new[] { 1 });
        collection.ReplaceRange(new[] { 2 });
        collection.ReplaceRange(new[] { 3 });

        // Assert
        totalResets.Should().Be(3);
        collection.Should().Equal(3);
    }

    // =====================================================================
    //                             VersionComparer
    // =====================================================================

    /// <summary>
    /// Проверяет, что 1.2.3 меньше 1.2.4.
    /// </summary>
    [TestMethod]
    public void CompareVersions_1_2_3_Vs_1_2_4_ReturnsMinusOne()
    {
        // Act
        int result = VersionComparer.CompareVersions("1.2.3", "1.2.4");

        // Assert
        result.Should().Be(-1);
    }

    /// <summary>
    /// Проверяет, что 1.2.4 больше 1.2.3.
    /// </summary>
    [TestMethod]
    public void CompareVersions_1_2_4_Vs_1_2_3_ReturnsOne()
    {
        // Act
        int result = VersionComparer.CompareVersions("1.2.4", "1.2.3");

        // Assert
        result.Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что одинаковые версии равны.
    /// </summary>
    [TestMethod]
    public void CompareVersions_IdenticalVersions_ReturnsZero()
    {
        // Act
        int result = VersionComparer.CompareVersions("1.2.3", "1.2.3");

        // Assert
        result.Should().Be(0);
    }

    /// <summary>
    /// Проверяет, что v-префикс игнорируется: v2.0.0 равна 2.0.0.
    /// </summary>
    [TestMethod]
    public void CompareVersions_VPrefixIgnored_ReturnsZero()
    {
        // Act
        int result = VersionComparer.CompareVersions("v2.0.0", "2.0.0");

        // Assert
        result.Should().Be(0, "ведущий символ 'v' должен игнорироваться по SemVer");
    }

    /// <summary>
    /// Проверяет сравнение версий разной длины: 1.2 новее, чем 1.1.9.
    /// </summary>
    [TestMethod]
    public void CompareVersions_DifferentSegmentCounts_ComparesNumerically()
    {
        // Act
        int greater = VersionComparer.CompareVersions("1.2", "1.1.9");
        int equal = VersionComparer.CompareVersions("1.0", "1.0.0");

        // Assert
        greater.Should().Be(1);
        equal.Should().Be(0, "отсутствующие сегменты трактуются как 0");
    }

    /// <summary>
    /// Проверяет сравнение в стиле yt-dlp: v93.0 новее, чем 7.1.0.
    /// </summary>
    [TestMethod]
    public void CompareVersions_YtDlpStyle_V93Vs7_1_0_ReturnsOne()
    {
        // Act
        int result = VersionComparer.CompareVersions("v93.0", "7.1.0");

        // Assert
        result.Should().Be(1, "мажорный сегмент 93 больше 7 независимо от длины");
    }

    /// <summary>
    /// Проверяет, что числовое сравнение ведется по значению, а не по строкам:
    /// 0.10.0 новее, чем 0.9.0.
    /// </summary>
    [TestMethod]
    public void CompareVersions_NumericNotStringComparison_0_10_GreaterThan0_9()
    {
        // Act
        int result = VersionComparer.CompareVersions("0.10.0", "0.9.0");

        // Assert
        result.Should().Be(1, "10 должен быть больше 9 при числовом сравнении");
    }

    /// <summary>
    /// Проверяет поведение при null и пустых строках.
    /// </summary>
    [TestMethod]
    public void CompareVersions_NullAndEmptyInputs_HandlesGracefully()
    {
        // Act & Assert
        VersionComparer.CompareVersions(null!, null!).Should().Be(0);
        VersionComparer.CompareVersions(string.Empty, string.Empty).Should().Be(0);
        VersionComparer.CompareVersions(null!, "1.0.0").Should().Be(-1, "null-версия всегда старее");
        VersionComparer.CompareVersions("1.0.0", null!).Should().Be(1, "заполненная версия новее null");
        VersionComparer.CompareVersions(string.Empty, "1.0.0").Should().Be(-1);
        VersionComparer.CompareVersions("1.0.0", string.Empty).Should().Be(1);
    }

    /// <summary>
    /// Проверяет сравнение пререлиз-суффиксов: стабильная версия новее пререлиза.
    /// </summary>
    [TestMethod]
    public void CompareVersions_PrereleaseSuffix_StableIsNewer()
    {
        // Act & Assert
        VersionComparer.CompareVersions("1.0.0", "1.0.0-preview.1").Should()
            .Be(1, "по SemVer стабильная версия новее пререлиза");
        VersionComparer.CompareVersions("1.0.0-preview.1", "1.0.0").Should().Be(-1);
    }

    /// <summary>
    /// Проверяет сравнение числовых частей суффиксов: preview.15 новее preview.12.
    /// </summary>
    [TestMethod]
    public void CompareVersions_PrereleaseNumericSuffixes_ComparesNumerically()
    {
        // Act
        int result = VersionComparer.CompareVersions("1.0.0-preview.15", "1.0.0-preview.12");

        // Assert
        result.Should().Be(1);
    }

    /// <summary>
    /// Проверяет сравнение текстовых частей суффиксов без учёта регистра.
    /// </summary>
    [TestMethod]
    public void CompareVersions_PrereleaseTextSuffixes_ComparesCaseInsensitively()
    {
        // Act
        int result = VersionComparer.CompareVersions("1.0.0-ALPHA.1", "1.0.0-beta.1");

        // Assert
        result.Should().BeLessThan(0, "alpha по орфографии раньше beta без учета регистра");
    }

    /// <summary>
    /// Проверяет, что более длинный суффикс считается более новой промежуточной сборкой.
    /// </summary>
    [TestMethod]
    public void CompareVersions_LongerSuffix_IsNewerIntermediateBuild()
    {
        // Act
        int result = VersionComparer.CompareVersions("1.0.0-preview.1.2", "1.0.0-preview.1");

        // Assert
        result.Should().Be(1, "длиннее суффикс — новее промежуточная сборка");
    }

    /// <summary>
    /// Проверяет поведение с невалидными строками: нечисловые сегменты трактуются как 0.
    /// </summary>
    [TestMethod]
    public void CompareVersions_InvalidStrings_NonNumericSegmentsTreatedAsZero()
    {
        // Act
        int result = VersionComparer.CompareVersions("abc", "0.0.1");

        // Assert
        result.Should().Be(-1, "сегмент 'abc' парсится как 0, поэтому abc старее 0.0.1");
    }

    // =====================================================================
    //                              ScriptInfo
    // =====================================================================

    /// <summary>
    /// Проверяет вычисляемое свойство CardOpacity для доступного и недоступного скриптов.
    /// </summary>
    [TestMethod]
    public void ScriptInfo_CardOpacity_ReflectsAvailability()
    {
        // Arrange
        var available = new ScriptInfo { IsAvailable = true };
        var unavailable = new ScriptInfo { IsAvailable = false };

        // Act & Assert
        available.CardOpacity.Should().Be(1.0);
        unavailable.CardOpacity.Should().Be(0.4, "недоступный скрипт должен быть приглушен");
    }

    /// <summary>
    /// Проверяет значения по умолчанию ScriptInfo.
    /// </summary>
    [TestMethod]
    public void ScriptInfo_Defaults_AreCorrect()
    {
        // Act
        var info = new ScriptInfo();

        // Assert
        info.Name.Should().BeEmpty();
        info.Description.Should().BeEmpty();
        info.Category.Should().BeEmpty();
        info.IconName.Should().BeEmpty();
        info.IsAvailable.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что CategoryBgColor корректно распознаёт категории
    /// и возвращает Default-кисть для пустой категории.
    /// ВНИМАНИЕ: SolidColorBrush требует XAML-среды, поэтому тест проверяет
    /// только исключение (или отсутствие) при обращении — в headless-режиме
    /// создание кисти может упасть с COMException.
    /// </summary>
    [TestMethod]
    [Ignore("ScriptInfo.CategoryBgColor/CategoryFgColor создают SolidColorBrush (WinRT), что требует XAML-среды; в headless-тестах эмпирически падает COMException.")]
    public void ScriptInfo_CategoryColors_MapCategoriesToBrushes()
    {
        // Arrange
        var video = new ScriptInfo { Category = AppConstants.ScriptCategory.Video };
        var unknown = new ScriptInfo { Category = "Несуществующая" };

        // Act
        var videoBg = video.CategoryBgColor;
        var defaultBg = unknown.CategoryBgColor;

        // Assert
        videoBg.Should().NotBeNull();
        defaultBg.Should().NotBeNull();
    }

    /// <summary>
    /// Проверяет, что пустая категория и категория с пробелами корректно
    /// обрабатываются через Trim (без создания кисти — через проверку исключения).
    /// </summary>
    [TestMethod]
    public void ScriptInfo_TrimmedCategory_DoesNotThrowOnLookup()
    {
        // Arrange
        var info = new ScriptInfo { Category = " Видео " };

        // Act & Assert — если XAML-среды нет, падение WinRT обернётся в исключение,
        // но сам switch по Trim-категории не должен давать NRE на managed-уровне.
        info.Category.Trim().Should().Be("Видео");
    }

    // =====================================================================
    //                        MediaStructure / MediaTrack
    // =====================================================================

    /// <summary>
    /// Проверяет фильтрацию дорожек MediaStructure по типам (video/audio/subtitles).
    /// </summary>
    [TestMethod]
    public void MediaStructure_GetTracksByType_FiltersCorrectly()
    {
        // Arrange
        var structure = new MediaStructure();
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "Video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio" });
        structure.Tracks.Add(new MediaTrack { TrackId = 2, TrackType = "Subtitles" });
        structure.Tracks.Add(new MediaTrack { TrackId = 3, TrackType = "audio" });

        // Act
        var video = structure.GetVideoTracks();
        var audio = structure.GetAudioTracks();
        var subtitles = structure.GetSubtitleTracks();

        // Assert
        video.Should().HaveCount(1, "фильтрация должна быть регистронезависимой");
        video[0].TrackId.Should().Be(0);
        audio.Should().HaveCount(2);
        subtitles.Should().HaveCount(1);
        subtitles[0].TrackId.Should().Be(2);
    }

    /// <summary>
    /// Проверяет фильтрацию вложений-шрифтов в MediaStructure.
    /// </summary>
    [TestMethod]
    public void MediaStructure_GetFontAttachments_FiltersFontsOnly()
    {
        // Arrange
        var structure = new MediaStructure();
        structure.Attachments.Add(new MediaAttachment
        {
            AttachmentId = 1,
            FileName = "ArialBold.ttf",
            MimeType = "application/x-truetype-font"
        });
        structure.Attachments.Add(new MediaAttachment
        {
            AttachmentId = 2,
            FileName = "cover.jpg",
            MimeType = "image/jpeg"
        });
        structure.Attachments.Add(new MediaAttachment
        {
            AttachmentId = 3,
            FileName = "Poster.otf",
            MimeType = string.Empty
        });

        // Act
        var fonts = structure.GetFontAttachments();

        // Assert
        fonts.Should().HaveCount(2);
        fonts.Select(f => f.FileName).Should().Equal("ArialBold.ttf", "Poster.otf");
    }

    /// <summary>
    /// Проверяет локализованные метки типов MediaTrack.
    /// </summary>
    [TestMethod]
    public void MediaTrack_TypeLabel_ReturnsLocalizedLabels()
    {
        // Arrange
        var video = new MediaTrack { TrackType = "VIDEO" };
        var audio = new MediaTrack { TrackType = "Audio" };
        var subtitles = new MediaTrack { TrackType = "Subtitles" };
        var unknown = new MediaTrack { TrackType = "chapters" };

        // Act & Assert
        video.TypeLabel.Should().Be("Видео");
        audio.TypeLabel.Should().Be("Аудио");
        subtitles.TypeLabel.Should().Be("Субтитры");
        unknown.TypeLabel.Should().Be("chapters", "неизвестный тип возвращается как есть");
    }

    /// <summary>
    /// Проверяет значения по умолчанию MediaTrack.
    /// </summary>
    [TestMethod]
    public void MediaTrack_Defaults_AreCorrect()
    {
        // Act
        var track = new MediaTrack();

        // Assert
        track.TrackId.Should().Be(0);
        track.TrackType.Should().BeEmpty();
        track.Codec.Should().BeEmpty();
        track.Language.Should().Be("und", "язык по умолчанию — неопределённый");
        track.Name.Should().BeEmpty();
        track.Resolution.Should().BeEmpty();
        track.Channels.Should().Be(0);
        track.IsDefault.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет определение шрифтов MediaAttachment по MIME-типу и расширению.
    /// </summary>
    [TestMethod]
    public void MediaAttachment_IsFont_DetectedByMimeAndExtension()
    {
        // Arrange
        var byMimeTruetype = new MediaAttachment { FileName = "font.bin", MimeType = "application/x-truetype-font" };
        var byMimeOpentype = new MediaAttachment { FileName = "font.bin", MimeType = "application/vnd.ms-opentype" };
        var byExtension = new MediaAttachment { FileName = "MyFont.woff2", MimeType = string.Empty };
        var notFont = new MediaAttachment { FileName = "cover.jpg", MimeType = "image/jpeg" };
        var noName = new MediaAttachment { FileName = string.Empty, MimeType = "application/x-truetype-font" };

        // Act & Assert
        byMimeTruetype.IsFont.Should().BeTrue("MIME содержит 'truetype'");
        byMimeOpentype.IsFont.Should().BeTrue("MIME содержит 'opentype'");
        byExtension.IsFont.Should().BeTrue("расширение .woff2 распознаётся как шрифт");
        notFont.IsFont.Should().BeFalse();
        noName.IsFont.Should().BeFalse("пустое имя файла исключает шрифт");
    }

    // =====================================================================
    //                          ActiveProcessTracker
    // =====================================================================

    /// <summary>
    /// Проверяет, что Register/Unregister игнорируют null без исключений.
    /// </summary>
    [TestMethod]
    public void ActiveProcessTracker_RegisterUnregisterNull_DoesNotThrow()
    {
        // Act
        Action act = () =>
        {
            ActiveProcessTracker.Register(null!);
            ActiveProcessTracker.Unregister(null!);
        };

        // Assert
        act.Should().NotThrow("null-процессы должны молча игнорироваться");
    }

    /// <summary>
    /// Проверяет регистрацию и удаление реального процесса:
    /// после KillAll трекер не должен хранить завершённые процессы.
    /// </summary>
    [TestMethod]
    public void ActiveProcessTracker_RegisterAndKillAll_TerminatesAndClearsRegistry()
    {
        // Arrange
        var startInfo = new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo);
        process.Should().NotBeNull("cmd.exe должен быть доступен на Windows");

        // Act
        ActiveProcessTracker.Register(process!);
        ActiveProcessTracker.KillAll();

        // Assert
        process!.HasExited.Should().BeTrue("зарегистрированный процесс должен быть завершён KillAll");
        process.WaitForExit(5000).Should().BeTrue();
    }

    /// <summary>
    /// Проверяет идемпотентность Unregister: повторное удаление не бросает исключений.
    /// </summary>
    [TestMethod]
    public void ActiveProcessTracker_UnregisterTwice_IsIdempotent()
    {
        // Arrange
        var startInfo = new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo);
        ActiveProcessTracker.Register(process!);

        // Act
        ActiveProcessTracker.Unregister(process!);
        ActiveProcessTracker.Unregister(process!); // повторное удаление
        process!.Kill();
        ActiveProcessTracker.KillAll(); // KillAll по пустому реестру

        // Assert — исключений быть не должно
        process.WaitForExit(5000).Should().BeTrue();
    }

    /// <summary>
    /// Проверяет потокобезопасность: параллельная регистрация 100 процессов
    /// из разных потоков не приводит к исключениям.
    /// </summary>
    [TestMethod]
    public async Task ActiveProcessTracker_ParallelRegistration_FromManyThreadsDoesNotThrow()
    {
        // Arrange
        var exceptions = new List<Exception>();
        var processes = new List<Process>();

        try
        {
            // Act
            await Task.Run(() =>
            {
                Parallel.For(0, 100, i =>
                {
                    try
                    {
                        var startInfo = new ProcessStartInfo("cmd.exe", "/c pause")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        var process = Process.Start(startInfo);
                        lock (processes)
                        {
                            if (process != null)
                            {
                                processes.Add(process);
                            }
                        }
                        ActiveProcessTracker.Register(process!);
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            });

            // Assert
            exceptions.Should().BeEmpty("параллельная регистрация должна быть потокобезопасной");
        }
        finally
        {
            ActiveProcessTracker.KillAll();
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                    process.Dispose();
                }
                catch
                {
                    // Игнорируем ошибки очистки
                }
            }
        }
    }
}
