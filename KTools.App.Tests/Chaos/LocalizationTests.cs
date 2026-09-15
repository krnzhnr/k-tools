// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;

namespace KTools_App.Tests.Chaos;

/// <summary>
/// Хаос-тесты локализации и региональных форматов: десятичные разделители
/// (запятая/точка), размеры файлов в разных культурах, пути с кириллицей
/// и эмодзи, форматы placeholder.
/// Все тесты восстанавливают исходную культуру потока в finally.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LocalizationTests
{
    private static readonly CultureInfo OriginalCulture =
        CultureInfo.CurrentCulture;

    /// <summary>
    /// Вспомогательный запуск действия в заданной культуре с гарантированным
    /// восстановлением исходной культуры потока.
    /// </summary>
    private static void WithCulture(CultureInfo culture, Action action)
    {
        Thread.CurrentThread.CurrentCulture = culture;
        try
        {
            action();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = OriginalCulture;
        }
    }

    /// <summary>
    /// Хаос: формат размера файла зависит от текущей культуры — при ru-RU
    /// десятичный разделитель запятая, при en-US — точка.
    /// </summary>
    [TestMethod]
    public void FileQueueItem_FileSizeStr_RuCultureUsesCommaSeparator()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.GetFullPath("exact_mb.mkv");
        File.WriteAllBytes(file, new byte[1024 * 1024]); // ровно 1 МБ

        WithCulture(new CultureInfo("ru-RU"), () =>
        {
            // Act
            var item = new FileQueueItem(file);

            // Assert
            item.FileSizeStr.Should().StartWith("1,00",
                "русская культура использует запятую как десятичный разделитель");
            item.FileSizeStr.Should().EndWith("МБ");
        });
    }

    /// <summary>
    /// Хаос: при en-US культуре десятичный разделитель — точка.
    /// </summary>
    [TestMethod]
    public void FileQueueItem_FileSizeStr_EnCultureUsesDotSeparator()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.GetFullPath("exact_mb.mkv");
        File.WriteAllBytes(file, new byte[1024 * 1024]);

        WithCulture(new CultureInfo("en-US"), () =>
        {
            // Act
            var item = new FileQueueItem(file);

            // Assert
            item.FileSizeStr.Should().StartWith("1.00",
                "американская культура использует точку как десятичный разделитель");
        });
    }

    /// <summary>
    /// Хаос: формат F2 корректен для дробного размера в любой культуре —
    /// проверяем вычисленное значение по текущей культуре.
    /// </summary>
    [TestMethod]
    public void FileQueueItem_FileSizeStr_FractionalSize_MatchesCultureFormat()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.GetFullPath("partial.mkv");
        File.WriteAllBytes(file, new byte[1_500_000]); // ~1.43 МБ

        // Act
        var item = new FileQueueItem(file);

        // Assert — строка строго соответствует Format(length/1048576, "F2") текущей культуры
        string expected = (1_500_000 / (1024.0 * 1024.0)).ToString("F2") + " МБ";
        item.FileSizeStr.Should().Be(expected,
            "формат размера обязан точно соответствовать F2 текущей культуры");
    }

    /// <summary>
    /// Хаос: URL-ссылки распознаются в любом регистре схемы
    /// и помечаются как "Ссылка" без обращения к сети.
    /// </summary>
    [TestMethod]
    [DataRow("http://example.com/video.mkv")]
    [DataRow("https://example.com/video.mkv")]
    [DataRow("HTTPS://example.com/video.mkv")]
    [DataRow("HtTpS://пример.рф/фильм.mkv")]
    public void FileQueueItem_UrlVariants_MarkedAsLink(string url)
    {
        // Arrange / Act
        var item = new FileQueueItem(url);

        // Assert
        item.FileSizeStr.Should().Be("Ссылка",
            "URL в любом регистре схемы должен помечаться как ссылка");
    }

    /// <summary>
    /// Хаос: UpdateOutputPathPlaceholder корректно отображает путь
    /// кириллической папки с пробелами без искажений.
    /// </summary>
    [TestMethod]
    public void UpdateOutputPathPlaceholder_CyrillicFolder_PreservedVerbatim()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string cyrFile = tempDir.CreateFile(
            Path.Combine("Мои видео 2024", "ролик.mkv"), "data");

        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var vm = new WorkPanelViewModel(
            MockBuilders.CreateNavigationMock().Object,
            MockBuilders.CreateDialogMock().Object,
            settingsMock.Object,
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateDependencyManagerMock().Object,
            MockBuilders.CreateMediaProbeMock().Object);
        var script = new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
        var files = new ObservableCollection<FileQueueItem> { new(cyrFile) };
        vm.Initialize(script, files);
        MessengerIsolation.Track(vm);

        // Act
        vm.UpdateOutputPathPlaceholder();

        // Assert
        vm.OutputPathPlaceholder.Should().Contain("Мои видео 2024",
            "кириллица в пути плейсхолдера не должна искажаться");
        vm.OutputPathPlaceholder.Should().StartWith("По умолчанию: ");
    }

    /// <summary>
    /// Хаос: URL в очереди направляет плейсхолдер в папку Downloads профиля.
    /// </summary>
    [TestMethod]
    public void UpdateOutputPathPlaceholder_HttpFile_PointsToDownloads()
    {
        // Arrange
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var vm = new WorkPanelViewModel(
            MockBuilders.CreateNavigationMock().Object,
            MockBuilders.CreateDialogMock().Object,
            settingsMock.Object,
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateDependencyManagerMock().Object,
            MockBuilders.CreateMediaProbeMock().Object);
        var script = new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
        var files = new ObservableCollection<FileQueueItem>
        {
            new("https://example.com/video.mkv"),
        };
        vm.Initialize(script, files);
        MessengerIsolation.Track(vm);

        // Act
        vm.UpdateOutputPathPlaceholder();

        // Assert
        vm.OutputPathPlaceholder.Should().Contain("Downloads",
            "URL-очередь должна по умолчанию предлагать папку загрузок");
    }

    /// <summary>
    /// Хаос: включённая настройка UseAutoSubfolder с пустым именем подпапки
    /// использует резервное имя KTools_Result.
    /// </summary>
    [TestMethod]
    public void UpdateOutputPathPlaceholder_AutoSubfolderEmptyName_UsesKtoolsResult()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("video.mkv", "data");
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.Object.UseAutoSubfolder = true;
        settingsMock.Object.DefaultOutputSubfolder = "  ";

        var vm = new WorkPanelViewModel(
            MockBuilders.CreateNavigationMock().Object,
            MockBuilders.CreateDialogMock().Object,
            settingsMock.Object,
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateDependencyManagerMock().Object,
            MockBuilders.CreateMediaProbeMock().Object);
        var script = new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
        var files = new ObservableCollection<FileQueueItem> { new(file) };
        vm.Initialize(script, files);
        MessengerIsolation.Track(vm);

        // Act
        vm.UpdateOutputPathPlaceholder();

        // Assert
        vm.OutputPathPlaceholder.Should().Contain("KTools_Result",
            "пустое имя подпапки должно заменяться резервным KTools_Result");
    }

    /// <summary>
    /// Хаос: RTL-языки (арабский, иврит) как текущая культура не ломают
    /// формирование строк размеров и статусов.
    /// </summary>
    [TestMethod]
    public void FileQueueItem_UnderRtlCulture_DoesNotThrow()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("video.mkv", "data");

        WithCulture(new CultureInfo("ar-SA"), () =>
        {
            // Act
            var item = new FileQueueItem(file);

            // Assert
            item.FileSizeStr.Should().NotBeNullOrEmpty(
                "арабская RTL-культура не должна ломать формат размера файла");
        });
    }
}
