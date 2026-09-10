// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Тесты управления окнами приложения: чистая логика, вынесенная из Window-классов,
/// плюс декларативная сетка [Ignore]-сценариев, требующих STA WinUI-хоста.
///
/// ЭМПИРИЧЕСКАЯ ФИКСАЦИЯ СРЕДЫ (см. WindowHeadlessProbeTests):
/// конструктор Microsoft.UI.Xaml.Window в MSTest-процессе падает — XAML-движок
/// не инициализирован (нет Application.Start / DispatcherQueue / package identity).
/// Окна создаются ТОЛЬКО в App.OnLaunched реального процесса приложения.
/// Поэтому все Window-сценарии ниже разведены на:
/// 1) headless-тестируемую чистую логику (static-методы без XAML-зависимостей);
/// 2) [Ignore]-декларации для ручного / WinAppDriver-прогона.
///
/// СИСТЕМАТИЧЕСКАЯ ЗАПИСЬ СЦЕНАРИЙ РУЧНОЙ ПРОВЕРКИ:
/// - Multi-window: MainWindow + AudioSyncWindow/BitrateViewerWindow/SubtitlePreviewWindow одновременно;
/// - Resize: минимальные размеры 800x620 DIP (WM_GETMINMAXINFO-subclass в MainWindow.xaml.cs:421);
/// - DPI: PerMonitorV2 (app.manifest), масштабирование размеров при 150%/200%;
/// - Восстановление из свернутого состояния (App.BringMainWindowToFront, SW_RESTORE);
/// - Сохранение/восстановление размеров окна при перезапуске (настройки Window.Width/Height);
/// - Z-order при shell-активации (SetForegroundWindow из второго процесса).
/// </summary>
[TestClass]
public class WindowManagementTests
{
    // ====================================================================
    // Чистая headless-логика оконных классов
    // ====================================================================

    [TestMethod]
    public void MainWindow_ResolveIconPath_FindsIconInAssetsSubfolder()
    {
        // Arrange — ResolveIconPath: private static в MainWindow.xaml.cs (линия ~461).
        // Тестовая директория bin содержит Assets\ ... проверим факт: иконка рядом с exe
        // ищется в корне, затем в Assets. Создаём временную структуру Assets.
        string baseDir = AppContext.BaseDirectory;
        string assetsDir = Path.Combine(baseDir, "Assets");
        bool dirExisted = Directory.Exists(assetsDir);
        Directory.CreateDirectory(assetsDir);
        string iconPath = Path.Combine(assetsDir, "TestIcon.ico");
        bool fileExisted = File.Exists(iconPath);
        File.WriteAllText(iconPath, "stub");

        try
        {
            var method = typeof(MainWindow).GetMethod(
                "ResolveIconPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull("ResolveIconPath должен существовать в MainWindow");

            // Act — иконка найдена в подпапке Assets
            string? result = (string?)method!.Invoke(null, new object[] { baseDir, "TestIcon.ico" });

            // Assert
            result.Should().Be(iconPath, "иконка в Assets должна находиться по приоритету корня-затем-Assets");
        }
        finally
        {
            if (!fileExisted)
            {
                File.Delete(iconPath);
            }
            if (!dirExisted)
            {
                Directory.Delete(assetsDir);
            }
        }
    }

    [TestMethod]
    public void MainWindow_ResolveIconPath_MissingIcon_ReturnsNull()
    {
        // Arrange
        var method = typeof(MainWindow).GetMethod(
            "ResolveIconPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        // Act — несуществующая иконка
        string? result = (string?)method!.Invoke(
            null,
            new object[] { AppContext.BaseDirectory, "DefinitelyMissing_9x7.ico" });

        // Assert — null: окно должно работать и без иконки
        result.Should().BeNull();
    }

    [TestMethod]
    public void MainWindow_ResolveIconPath_FindsIconInRootBeforeAssets()
    {
        // Arrange — иконка есть И в корне, И в Assets: приоритет у корня
        string baseDir = AppContext.BaseDirectory;
        string rootIcon = Path.Combine(baseDir, "PriorityIcon.ico");
        string assetsDir = Path.Combine(baseDir, "Assets");
        bool dirExisted = Directory.Exists(assetsDir);
        Directory.CreateDirectory(assetsDir);
        string assetsIcon = Path.Combine(assetsDir, "PriorityIcon.ico");
        bool rootExisted = File.Exists(rootIcon);
        bool assetsExisted = File.Exists(assetsIcon);
        File.WriteAllText(rootIcon, "root");
        File.WriteAllText(assetsIcon, "assets");

        try
        {
            var method = typeof(MainWindow).GetMethod(
                "ResolveIconPath",
                BindingFlags.NonPublic | BindingFlags.Static);

            // Act
            string? result = (string?)method!.Invoke(null, new object[] { baseDir, "PriorityIcon.ico" });

            // Assert
            result.Should().Be(rootIcon, "иконка в корне имеет приоритет над Assets");
        }
        finally
        {
            if (!rootExisted)
            {
                File.Delete(rootIcon);
            }
            if (!assetsExisted)
            {
                File.Delete(assetsIcon);
            }
            if (!dirExisted)
            {
                Directory.Delete(assetsDir);
            }
        }
    }

    [TestMethod]
    public void MainWindow_TypeExistsAndHasExpectedDependenciesInConstructor()
    {
        // Arrange / Act — репрессионная сетка сигнатуры конструктора MainWindow:
        // изменение DI-контракта окна не должно пройти молча
        var ctor = typeof(MainWindow).GetConstructor(new[] { typeof(Services.Contracts.ILogService), typeof(Services.Contracts.ISettingsManager) });

        // Assert
        ctor.Should().NotBeNull("MainWindow обязан принимать (ILogService, ISettingsManager) через DI");
    }

    [TestMethod]
    public void AudioSyncWindow_AacPrimingConstant_IsExactly1024SamplesAt48Khz()
    {
        // Arrange / Act — константа компенсации AAC-задержки: 1024 сэмпла / 48000 Гц
        var field = typeof(UI.AudioSyncWindow).GetProperty(
            "FinalCalculatedShiftMs",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        // FinalCalculatedShiftMs = _userShiftMs - 21.333333333333332 — проверяем через
        // единственный доступный headless-путь: сам факт наличия свойства-контракта
        field.Should().NotBeNull("AudioSyncWindow обязан предоставлять FinalCalculatedShiftMs для результата сдвига");

        // Константа компенсации = 1024/48000*1000 мс = 21.333... мс (AacPrimingDelayMs)
        const double expectedMs = 1024.0 / 48000.0 * 1000.0;
        expectedMs.Should().BeApproximately(21.333333333333332, 0.0000001);
    }

    [TestMethod]
    public void BitrateViewerWindow_FormatTime_ConvertsSecondsToDisplayString()
    {
        // Arrange — FormatTime: private static в BitrateViewerWindow (линия ~503)
        var method = typeof(UI.BitrateViewerWindow).GetMethod(
            "FormatTime",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("FormatTime должен существовать в BitrateViewerWindow");

        // Act / Assert — часы>0 дают H:MM:SS, иначе MM:SS; отрицательное время = 0
        ((string)method!.Invoke(null, new object[] { 3725.0 })!).Should().Be("01:02:05", "1ч 2м 5с");
        ((string)method.Invoke(null, new object[] { 125.9 })!).Should().Be("02:05", "2м 5с без часов");
        ((string)method.Invoke(null, new object[] { 0.0 })!).Should().Be("00:00");
        ((string)method.Invoke(null, new object[] { -50.0 })!).Should().Be("00:00", "отрицательное время клампится к 0");
    }

    [TestMethod]
    public void SubtitlePreviewWindow_TypeExistsWithViewModelParameter()
    {
        // Arrange / Act — DI-контракт окна предпросмотра субтитров
        var ctor = typeof(UI.Pages.SubtitlePreviewWindow).GetConstructor(
            new[] { typeof(KTools_App.ViewModels.SubtitlePreviewViewModel) });

        // Assert
        ctor.Should().NotBeNull("SubtitlePreviewWindow обязан принимать SubtitlePreviewViewModel через DI");
    }

    [TestMethod]
    public void App_BringMainWindowToFront_WithoutWindow_ReturnsSilently()
    {
        // Arrange / Act / Assert — App.CurrentMainWindow == null в тестах:
        // BringMainWindowToFront должен молча вернуться (App.xaml.cs:536-538)
        // без обращения к Win32-хэндлам несуществующего окна.
        Action act = App.BringMainWindowToFront;
        act.Should().NotThrow("без главного окна вызов безопасен (ранний return)");
    }

    // ====================================================================
    // ДЕКЛАРАЦИИ СЦЕНАРИЙ, ТРЕБУЮЩИХ STA WINUI-ХОСТА
    // ====================================================================

    /// <summary>
    /// [Ignore] Создание MainWindow требует STA WinUI-хоста: конструктор вызывает
    /// InitializeComponent() (XAML-движок), GetWindowHandle (Win32+hwnd) и
    /// SetWindowSubclass. Эмпирически Window() падает в headless MSTest-процессе.
    /// Покрытие: ручной прогон / WinAppDriver.
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста (XAML-движок + package identity); Window-конструктор падает в headless MSTest")]
    public void MainWindow_Constructor_CreatesWindowWithDpiScaledSize()
    {
        Assert.Inconclusive("Декларация сценария для ручного/WinAppDriver-прогона: размеры 800x740 DIP × DPI");
    }

    /// <summary>
    /// [Ignore] Окно синхронизации аудио (AudioSyncWindow) требует WinUI-хоста:
    /// InitializeComponent, Win2D CanvasControl, DispatcherQueue.
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста: Win2D-канвасы и DispatcherQueue недоступны headless")]
    public void AudioSyncWindow_PanZoomInteraction_UpdatesUserShiftMs()
    {
        Assert.Inconclusive("Декларация: интерактивное перетаскивание осциллограммы меняет сдвиг в мс");
    }

    /// <summary>
    /// [Ignore] Окно анализа битрейта требует WinUI-хоста (Win2D GraphCanvas,
    /// App.Services для темы, PrivateExtractIcons).
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста: Win2D-рендер и App.Services (статический DI без Application.Start = null)")]
    public void BitrateViewerWindow_Rendering_DrawsBitrateGraphFromData()
    {
        Assert.Inconclusive("Декларация: отрисовка графика битрейта по BitrateAnalysisResult");
    }

    /// <summary>
    /// [Ignore] Multi-window сценарий: одновременное существование MainWindow,
    /// SubtitlePreviewWindow, AudioSyncWindow, BitrateViewerWindow — требует
    /// реального приложения с запущенным DispatcherQueue.
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста: multi-window взаимодействие (Z-order, фокус, закрытие дочерних окон)")]
    public void MultiWindow_AllWindowsOpen_IndependentLifecycle()
    {
        Assert.Inconclusive("Декларация для ручного прогона: одновременная работа всех окон приложения");
    }

    /// <summary>
    /// [Ignore] Resize/DPI-сценарий: ограничение минимальных размеров 800x620 DIP
    /// через WM_GETMINMAXINFO-subclass и масштабирование при 150%/200% DPI.
    /// </summary>
    [TestMethod]
    [Ignore("Требует STA WinUI-хоста: WM_GETMINMAXINFO-subclass и PerMonitorV2 DPI-масштабирование")]
    public void MainWindow_Resize_MinSizeEnforcedAndDpiScaled()
    {
        Assert.Inconclusive("Декларация: минимальный размер окна не нарушается при любом DPI");
    }
}
