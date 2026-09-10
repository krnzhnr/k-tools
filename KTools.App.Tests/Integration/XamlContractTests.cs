// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Статическая верификация XAML-контрактов без запуска XAML-движка:
/// репрессионная сетка против переименований свойств ViewModels, методов
/// code-behind, ресурсных ключей и типов x:Class.
///
/// Стратегия: XAML-файлы читаются как ТЕКСТ, пути x:Bind и Binding извлекаются
/// регекспами, а существование целей проверяется через reflection по реально
/// загруженной сборке KTools.App. Это стабилизирует UI-слой без WinUI-хоста.
///
/// Непроверяемое в headless пропускается с инлайн-комментариями:
/// - ThemeResource-ссылки (загружаются WinUI из системы);
/// - конвертеры в {x:Bind ... Converter={StaticResource}} проверяются как StaticResource.
/// </summary>
[TestClass]
public class XamlContractTests
{
    /// <summary>Корень основного проекта (три уровня вверх от тестовой bin-папки исходников).</summary>
    private static readonly string AppRoot = FindAppRoot();

    private static readonly Regex XamlNamespaceRegex = new(
        @"xmlns:(?<alias>\w+)=""using:(?<ns>KTools_App[\w\.]*)""",
        RegexOptions.Compiled);

    private static string FindAppRoot()
    {
        // Тестовая сборка лежит в KTools.App.Tests\bin\x64\Debug\...;
        // корень репозитория — F:\Programing\Utils\k-tools, основной проект — KTools.App
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "KTools.App", "MainPage.xaml");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir, "KTools.App");
            }
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new InvalidOperationException("Не удалось найти корень проекта KTools.App от " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Все XAML-файлы проекта (включая UI/Pages, UI/Controls, корень).
    /// </summary>
    private static IEnumerable<(string Path, string Content)> LoadAllXaml()
    {
        string[] roots =
        [
            Path.Combine(AppRoot, "MainPage.xaml"),
            Path.Combine(AppRoot, "MainWindow.xaml"),
            Path.Combine(AppRoot, "App.xaml"),
            Path.Combine(AppRoot, "UI", "AudioSyncWindow.xaml"),
            Path.Combine(AppRoot, "UI", "BitrateViewerWindow.xaml"),
        ];
        foreach (string root in roots)
        {
            if (File.Exists(root))
            {
                yield return (root, File.ReadAllText(root));
            }
        }

        foreach (string sub in new[] { "Pages", "Controls" })
        {
            string dir = Path.Combine(AppRoot, "UI", sub);
            foreach (string file in Directory.GetFiles(dir, "*.xaml"))
            {
                yield return (file, File.ReadAllText(file));
            }
        }
    }

    private static Assembly AppAssembly => typeof(KTools_App.App).Assembly;

    // ====================================================================
    // 1. x:Class → реальный тип
    // ====================================================================

    [TestMethod]
    public void XamlClassAttribute_AllXamlFiles_MapsToExistingRealType()
    {
        // Arrange
        var files = LoadAllXaml().ToList();

        // Act
        var missing = new List<string>();
        foreach ((string path, string content) in files)
        {
            var match = Regex.Match(content, @"x:Class=""(?<cls>[\w\.]+)""");
            if (!match.Success)
            {
                continue; // App.xaml имеет x:Class — но даже без него это допустимо
            }
            string cls = match.Groups["cls"].Value;
            Type? type = AppAssembly.GetType(cls);
            if (type == null)
            {
                missing.Add($"{Path.GetFileName(path)}: {cls}");
            }
        }

        // Assert
        missing.Should().BeEmpty(
            "каждый x:Class должен соответствовать реально существующему типу в сборке KTools.App");
    }

    [TestMethod]
    public void XamlClassAttribute_ExpectedFileCount_AllXamlPresent()
    {
        // Arrange / Act
        var files = LoadAllXaml().ToList();
        var names = files.Select(f => Path.GetFileName(f.Path)).ToList();

        // Assert — полная сетка XAML-файлов приложения (детектор случайно удалённых страниц)
        names.Should().Contain(new[]
        {
            "MainPage.xaml", "MainWindow.xaml", "App.xaml",
            "AudioSyncWindow.xaml", "BitrateViewerWindow.xaml",
            "HomePage.xaml", "SettingsPage.xaml", "LogPage.xaml",
            "WorkPanel.xaml", "DependencySetupPage.xaml",
            "SubtitlePreviewPage.xaml", "TimingCalculatorPage.xaml",
            "FileListControl.xaml", "TrackSelectionControl.xaml",
            "ScriptSettingsControl.xaml", "StreamReplaceControl.xaml",
            "AudioTransplantControl.xaml", "AccentBadge.xaml",
            "DropZoneOverlay.xaml"
        });
    }

    // ====================================================================
    // 2. x:Bind-пути → публичные свойства контекстов
    // ====================================================================

    /// <summary>
    /// Собирает маппинг "XAML-файл → тип корневого контекста привязки"
    /// по x:DataType корня и известным свойствам ViewModel в code-behind.
    /// </summary>
    private static Type? ResolveRootDataType(string xamlContent)
    {
        // Первый x:DataType в файле описывает корневой контекст (Page/Window/UserControl)
        var rootDataType = Regex.Match(xamlContent, @"x:DataType=""(?<type>[\w\:]+)""");
        if (!rootDataType.Success)
        {
            return null;
        }
        return ResolveXamlTypeReference(rootDataType.Groups["type"].Value, xamlContent);
    }

    private static Type? ResolveXamlTypeReference(string xamlType, string xamlContent)
    {
        // Формат "alias:TypeName" или "alias:Outer.Inner"
        var parts = xamlType.Split(':');
        if (parts.Length != 2)
        {
            return null;
        }
        string alias = parts[0];
        string typeName = parts[1];

        var nsMatch = XamlNamespaceRegex.Match(xamlContent);
        string? ns = null;
        foreach (Match m in XamlNamespaceRegex.Matches(xamlContent))
        {
            if (m.Groups["alias"].Value == alias)
            {
                ns = m.Groups["ns"].Value;
                break;
            }
        }
        if (ns == null)
        {
            return null;
        }

        // Пробуем Outer.Inner (вложенный класс) и потом Outer
        string fullName = $"{ns}.{typeName}";
        Type? type = AppAssembly.GetType(fullName);
        if (type == null && typeName.Contains('.'))
        {
            type = AppAssembly.GetType($"{ns}.{typeName.Split('.')[0]}");
        }
        return type;
    }

    [TestMethod]
    public void XamlBindPaths_ViewModelPrefixedPaths_ReferToExistingPublicProperties()
    {
        // Arrange — все x:Bind вида "ViewModel.XXX" и "ViewModel.XXX.YYY"
        var violations = new List<string>();

        foreach ((string path, string content) in LoadAllXaml())
        {
            // Определяем тип ViewModel-свойства code-behind по имени файла
            string fileName = Path.GetFileNameWithoutExtension(path);
            Type? pageType = AppAssembly.GetType("KTools_App." +
                (path.Contains("UI\\Pages") ? $"UI.Pages.{fileName}" :
                 path.Contains("UI\\Controls") ? $"UI.Controls.{fileName}" :
                 fileName));
            if (pageType == null)
            {
                continue; // AudioSyncWindow/BitrateViewerWindow не имеют ViewModel-свойства — x:Bind ViewModel.* там нет
            }

            PropertyInfo? vmProp = pageType.GetProperty("ViewModel", BindingFlags.Public | BindingFlags.Instance);
            if (vmProp == null)
            {
                continue; // страницы без ViewModel-свойства (Sub-container pages) не используют ViewModel.*-биндинги
            }

            Type vmType = vmProp.PropertyType;

            foreach (Match m in Regex.Matches(content, @"\{x:Bind\s+ViewModel\.(?<path>[\w\.]+)"))
            {
                // Assert — первый сегмент пути обязан быть публичным свойством ViewModel
                string bindPath = m.Groups["path"].Value;
                string firstSegment = bindPath.Split('.')[0];
                PropertyInfo? prop = vmType.GetProperty(firstSegment, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null)
                {
                    // Field-путь (например, команда из поля) — проверяем и поля
                    FieldInfo? field = vmType.GetField(firstSegment, BindingFlags.Public | BindingFlags.Instance);
                    if (field == null)
                    {
                        violations.Add($"{Path.GetFileName(path)}: ViewModel.{bindPath} — свойство '{firstSegment}' отсутствует в {vmType.Name}");
                    }
                }
            }
        }

        // Assert
        violations.Should().BeEmpty(
            "все x:Bind ViewModel.* пути должны ссылаться на существующие публичные члены ViewModels");
    }

    [TestMethod]
    public void XamlBindPaths_RootDataTypePaths_ReferToExistingPublicMembers()
    {
        // Arrange — x:Bind без префикса (пути от корневого x:DataType).
        // Ограничение метода: x:Bind внутри DataTemplate имеют контекст x:DataType шаблона,
        // а не корневой; для точности используется DataTemplate-скоп (см. следующий тест).
        var violations = new List<string>();

        foreach ((string path, string content) in LoadAllXaml())
        {
            Type? rootType = ResolveRootDataType(content);
            if (rootType == null)
            {
                continue; // файлы без корневого x:DataType (MainWindow/App) — непроверяемые, пропускаем
            }

            // Вырезаем DataTemplate-блоки: они имеют собственный контекст привязки
            string contentWithoutTemplates = Regex.Replace(
                content,
                @"<DataTemplate[\s\S]*?</DataTemplate>",
                string.Empty);

            foreach (Match m in Regex.Matches(contentWithoutTemplates, @"\{x:Bind\s+(?!ViewModel\.)(?<path>[A-Za-z_][\w\.]*)"))
            {
                string bindPath = m.Groups["path"].Value;

                // Пропускаем статические ссылки на классы-хелперы (core:AppConstants.X)
                if (bindPath.Contains(':'))
                {
                    continue;
                }

                string firstSegment = bindPath.Split('.')[0];
                PropertyInfo? prop = rootType.GetProperty(firstSegment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                FieldInfo? field = prop == null
                    ? rootType.GetField(firstSegment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    : null;
                if (prop == null && field == null)
                {
                    violations.Add($"{Path.GetFileName(path)}: x:Bind '{bindPath}' — член '{firstSegment}' отсутствует в {rootType.Name}");
                }
            }
        }

        // Assert
        violations.Should().BeEmpty("все корневые x:Bind пути должны ссылаться на существующие публичные члены");
    }

    [TestMethod]
    public void XamlBindPaths_DataTemplateTypes_ResolveToRealOrWinUITypes()
    {
        // Arrange — каждый x:DataType в DataTemplate должен резолвиться в реальный тип.
        // Допустимы типы KTools.App и WinUI (TreeViewNode из Microsoft.UI.Xaml.Controls).
        var violations = new List<string>();

        foreach ((string path, string content) in LoadAllXaml())
        {
            foreach (Match m in Regex.Matches(content, @"x:DataType=""(?<type>[\w\:\.]+)"""))
            {
                string typeRef = m.Groups["type"].Value;

                // WinUI-типы (без using-алиаса KTools_App) резолвятся через WinAppSDK-сборку
                if (!typeRef.Contains(':'))
                {
                    typeof(Microsoft.UI.Xaml.Controls.TreeViewNode).Assembly
                        .GetType($"Microsoft.UI.Xaml.Controls.{typeRef}")
                        .Should().NotBeNull($"встроенный WinUI-тип '{typeRef}' должен существовать");
                    continue;
                }

                Type? resolved = ResolveXamlTypeReference(typeRef, content);
                if (resolved == null)
                {
                    violations.Add($"{Path.GetFileName(path)}: x:DataType '{typeRef}' не резолвится в тип KTools.App");
                }
            }
        }

        // Assert
        violations.Should().BeEmpty("все x:DataType должны ссылаться на реальные типы");
    }

    // ====================================================================
    // 3. StaticResource-ключи
    // ====================================================================

    [TestMethod]
    public void StaticResourceReferences_UsedKeys_DefinedInAppXamlOrSameFile()
    {
        // Arrange
        string appXaml = File.ReadAllText(Path.Combine(AppRoot, "App.xaml"));
        var globalKeys = Regex.Matches(appXaml, @"x:Key=""(?<key>\w+)""")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet();

        // Системные стили WinUI (определены в XamlControlsResources — непроверяемые, пропускаем)
        var systemStyles = new HashSet<string>
        {
            "AccentButtonStyle", "DefaultButtonStyle", "CaptionTextBlockStyle",
            "TitleTextBlockStyle", "BaseTextBlockStyle", "BodyTextBlockStyle",
            "SubtitleTextBlockStyle", "InformationalValueInfoBadgeStyle"
        };

        var violations = new List<string>();

        foreach ((string path, string content) in LoadAllXaml())
        {
            // Ключи, определённые в этом же файле (Page.Resources и т.п.)
            var localKeys = Regex.Matches(content, @"x:Key=""(?<key>\w+)""")
                .Select(m => m.Groups["key"].Value)
                .ToHashSet();

            foreach (Match m in Regex.Matches(content, @"\{StaticResource\s+(?<key>\w+)\}"))
            {
                string key = m.Groups["key"].Value;
                if (globalKeys.Contains(key) || localKeys.Contains(key) || systemStyles.Contains(key))
                {
                    continue;
                }
                violations.Add($"{Path.GetFileName(path)}: StaticResource '{key}' не определён");
            }
        }

        // Assert
        violations.Should().BeEmpty(
            "каждый StaticResource-ключ должен определяться в App.xaml или в том же файле (ThemeResource — системные, вне проверки)");
    }

    // ====================================================================
    // 4. Обработчики событий Click/Tapped/... → методы code-behind
    // ====================================================================

    [TestMethod]
    public void XamlEventHandlers_AllAttributeHandlers_ExistInCodeBehindType()
    {
        // Arrange — имена событий WinUI, для которых XAML использует методы code-behind
        string[] eventNames =
        [
            "Click", "Tapped", "Loaded", "Unloaded", "SelectionChanged",
            "Checked", "Unchecked", "ValueChanged", "TextChanged",
            "PointerPressed", "PointerReleased", "PointerMoved", "PointerWheelChanged",
            "PointerEntered", "PointerExited", "KeyDown",
            "DragOver", "DragLeave", "Drop", "SizeChanged",
            "DataContextChanged", "PreviewKeyDown", "LostFocus",
            "PaneOpened", "PaneClosed", "PaneOpening", "PaneClosing",
            "CloseButtonClick", "CreateResources", "Draw", "QuerySubmitted"
        ];

        var violations = new List<string>();

        foreach ((string path, string content) in LoadAllXaml())
        {
            var classMatch = Regex.Match(content, @"x:Class=""(?<cls>[\w\.]+)""");
            if (!classMatch.Success)
            {
                continue;
            }
            Type? type = AppAssembly.GetType(classMatch.Groups["cls"].Value);
            if (type == null)
            {
                continue;
            }

            string eventsPattern = string.Join("|", eventNames);
            foreach (Match m in Regex.Matches(content,
                $@"(?:{eventsPattern})=""(?<handler>[A-Za-z_][A-Za-z0-9_]*)"""))
            {
                // Значения True/False/аргументы не-обработчиков пропускаем
                string handler = m.Groups["handler"].Value;
                if (handler is "True" or "False" or "Visible" or "Collapsed")
                {
                    continue;
                }

                MethodInfo? method = type.GetMethod(
                    handler,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (method == null)
                {
                    violations.Add($"{Path.GetFileName(path)}: обработчик '{handler}' отсутствует в {type.Name}");
                }
            }
        }

        // Assert
        violations.Should().BeEmpty("каждый XAML-обработчик события должен существовать в code-behind");
    }

    // ====================================================================
    // 5. x:Uid → resw-ключи
    // ====================================================================

    [TestMethod]
    public void XamlUidKeys_UsedUids_HaveReswEntries()
    {
        // Arrange — в проекте нет .resw-файлов (локализация текстами в XAML),
        // и x:Uid не используется. Детектор-сетка на будущее: если x:Uid появится,
        // проверим пару. Проверяем фактическое состояние контракта.
        string stringsDir = Path.Combine(AppRoot, "Strings");
        var reswFiles = Directory.Exists(stringsDir)
            ? Directory.GetFiles(stringsDir, "*.resw", SearchOption.AllDirectories)
            : Array.Empty<string>();
        var reswKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string resw in reswFiles)
        {
            XDocument doc = XDocument.Load(resw);
            foreach (var data in doc.Descendants("data"))
            {
                string? name = data.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    reswKeys.Add(name);
                }
            }
        }

        var usedUids = new List<string>();
        foreach ((string path, string content) in LoadAllXaml())
        {
            foreach (Match m in Regex.Matches(content, @"x:Uid=""(?<uid>\w+)"""))
            {
                usedUids.Add(m.Groups["uid"].Value);
            }
        }

        // Act / Assert — текущий контракт: x:Uid не используются (тексты в XAML напрямую);
        // если/когда появятся, каждый UID обязан иметь ключ в resw (включая Property.Field синтаксис)
        foreach (string uid in usedUids)
        {
            bool hasPlain = reswKeys.Contains(uid);
            bool hasPropertyForm = reswKeys.Contains($"{uid}.Content")
                || reswKeys.Contains($"{uid}.Header")
                || reswKeys.Contains($"{uid}.Text");
            (hasPlain || hasPropertyForm).Should().BeTrue(
                $"x:Uid '{uid}' должен иметь ключ в .resw (в форме 'UID' или 'UID.Property')");
        }

        // Текущее состояние — ни одного UID: сетка активируется при добавлении локализации
        usedUids.Should().BeEmpty(
            "в текущей версии x:Uid не используются; тест — детектор контракта при появлении локализации");
        reswFiles.Should().BeEmpty("файлов .resw в проекте нет (локализация не подключена)");
    }

    // ====================================================================
    // 6. Binding-пути (классический {Binding ...}) — детектор использования
    // ====================================================================

    [TestMethod]
    public void XamlBindings_ClassicBindingSyntax_NotUsedInProject()
    {
        // Arrange — проект полностью на x:Bind (компилируемые привязки);
        // классические {Binding} отсутствуют. Фиксируем контракт для защиты от
        // случайного добавления непроверяемых late-bound привязок.
        var found = new List<string>();

        foreach ((string path, string content) in LoadAllXaml())
        {
            foreach (Match m in Regex.Matches(content, @"\{Binding\s+(?<path>[\w\.]+)"))
            {
                found.Add($"{Path.GetFileName(path)}: {{Binding {m.Groups["path"].Value}}}");
            }
        }

        // Assert
        found.Should().BeEmpty(
            "проект должен использовать только компилируемые x:Bind привязки; классический {Binding} недопустим");
    }

    // ====================================================================
    // 7. Tag-контракты навигации MainPage → MainViewModel
    // ====================================================================

    [TestMethod]
    public void MainPageNavigationTags_AllScriptTags_MappedInMainViewModelDictionaries()
    {
        // Arrange — теги script:* из MainPage.xaml обязаны поддерживаться MainViewModel.
        // Тег "settings" НЕ в XAML: пункт настроек — встроенный NavView.SettingsItem,
        // обрабатываемый в MainPage.xaml.cs через IsSettingsSelected (MainPage.xaml.cs:207).
        string mainPageXaml = File.ReadAllText(Path.Combine(AppRoot, "MainPage.xaml"));
        var scriptTags = Regex.Matches(mainPageXaml, @"Tag=""(?<tag>script:[\w]+|tool:[\w]+|home|logs|dependencies)""")
            .Select(m => m.Groups["tag"].Value)
            .Distinct()
            .ToList();

        scriptTags.Should().NotBeEmpty("MainPage.xaml обязан содержать навигационные теги");

        // Act / Assert — все статические теги из XAML
        var staticTags = scriptTags.Where(t => !t.StartsWith("script:")).ToList();
        staticTags.Should().Contain(new[] { "home", "logs", "dependencies", "tool:timing_calculator" },
            "ключевые статические теги навигации должны присутствовать в MainPage.xaml");

        // Все script-теги соответствуют legacy-маппингам MainViewModel.InitializeScripts
        var expectedLegacyTags = new[]
        {
            "script:video_encoding", "script:container_conversion", "script:metadata_cleanup",
            "script:audio_encoding", "script:audio_downmix", "script:audio_speed",
            "script:audio_channels", "script:audio_shift", "script:mkv_assembly",
            "script:stream_management", "script:stream_replacement", "script:container_demux",
            "script:subtitles_convert", "script:subtitles_shift", "script:media_downloader"
        };
        var scriptTagsOnly = scriptTags.Where(t => t.StartsWith("script:")).ToList();
        scriptTagsOnly.Should().BeEquivalentTo(expectedLegacyTags,
            "каждый script-тег XAML должен иметь legacy-маппинг в MainViewModel.InitializeScripts");
    }
}
