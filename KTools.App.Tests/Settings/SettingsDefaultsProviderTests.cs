// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Тесты разрыва зависимости SettingsManager от конкретных скриптов (DIP):
/// значения по умолчанию поступают через <see cref="ISettingsDefaultsProvider"/>
/// поверх <see cref="IScriptRegistry"/>, а секции незарегистрированных скриптов
/// фиксируются в журнале как mismatch без потери данных.
/// </summary>
[TestClass]
public class SettingsDefaultsProviderTests
{
    private Mock<IPathManager> _pathManagerMock = null!;
    private RecordingLogService _logService = null!;
    private string _tempSettingsDir = null!;
    private string _tempSettingsFile = null!;
    private readonly List<SettingsManager> _managers = new();

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _pathManagerMock = new Mock<IPathManager>();
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Defaults143_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");
        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempSettingsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (SettingsManager manager in _managers)
        {
            try
            {
                manager.Dispose();
            }
            catch (Exception)
            {
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    private SettingsManager CreateManager()
    {
        var manager = new SettingsManager(_logService, _pathManagerMock.Object);
        manager.InitializeAsync().GetAwaiter().GetResult();
        _managers.Add(manager);
        return manager;
    }

    private static StubSettingsDefaultsProvider CreateTestProvider()
    {
        return new StubSettingsDefaultsProvider(new[]
        {
            new ScriptSettingsDefaults("Тест", new[]
            {
                new SettingDefaultValue("режим", "быстрый"),
                new SettingDefaultValue("вложенный", false, new[]
                {
                    new SettingDefaultValue("глубина", 3)
                })
            })
        });
    }

    /// <summary>
    /// Проверяет, что публичный контракт уровня настроек не упоминает конкретные скрипты.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_PublicContract_HasNoConcreteScriptDependency()
    {
        // Assert — ни интерфейс, ни реализация не принимают доменные типы скриптов
        foreach (Type type in new[] { typeof(ISettingsManager), typeof(SettingsManager) })
        {
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(m => m.GetParameters())
                .Should().NotContain(
                    p => p.ParameterType == typeof(AbstractScript)
                        || p.ParameterType == typeof(List<AbstractScript>),
                    $"уровень настроек не должен зависеть от конкретных скриптов ({type.Name})");
        }

        MethodInfo initialize = typeof(ISettingsManager).GetMethod(nameof(ISettingsManager.InitializeDefaults))!;
        initialize.GetParameters()[0].ParameterType.Should().Be<ISettingsDefaultsProvider>();
        MethodInfo reset = typeof(ISettingsManager).GetMethod(nameof(ISettingsManager.ResetToDefaults))!;
        reset.GetParameters()[0].ParameterType.Should().Be<ISettingsDefaultsProvider>();
    }

    /// <summary>
    /// Проверяет, что значения по умолчанию применяются из схемы провайдера
    /// для известных скриптов без лишних событий.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_ProviderSchema_AppliesScriptDefaults()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.InitializeDefaults(CreateTestProvider());

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue();
        manager.GetSetting("Script_Тест", "режим", string.Empty).Should().Be("быстрый");
        manager.GetSetting("Script_Тест", "глубина", 0).Should().Be(3);
        _logService.EventsById(SettingsEventIds.DefaultsMismatch).Should().BeEmpty(
            "все секции принадлежат известным скриптам");
    }

    /// <summary>
    /// Проверяет, что секция незарегистрированного скрипта не удаляется,
    /// а фиксируется в журнале как mismatch.
    /// </summary>
    [TestMethod]
    public void InitializeDefaults_UnknownScriptGroup_LogsMismatchAndPreservesValues()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"Script_Удалённый\": { \"параметр\": 7 } }");
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.InitializeDefaults(StubSettingsDefaultsProvider.Empty);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue();
        IReadOnlyList<RecordedLogEvent> mismatch = _logService.EventsById(SettingsEventIds.DefaultsMismatch);
        mismatch.Should().HaveCount(1);
        mismatch[0].Level.Should().Be(LogLevel.Warning);
        mismatch[0].GetProperty<int>("Count").Should().Be(1);
        manager.GetSetting("Script_Удалённый", "параметр", 0).Should().Be(
            7, "чужая секция сохраняется без изменений");
        File.ReadAllText(_tempSettingsFile).Should().Contain("Script_Удалённый");
    }

    /// <summary>
    /// Проверяет, что явный сброс дополняет отсутствующие ключи скрипта
    /// и удаляет устаревшие, используя только схему провайдера.
    /// </summary>
    [TestMethod]
    public void ResetToDefaults_ProviderSchema_RemovesObsoleteKeysAndFillsMissing()
    {
        // Arrange
        File.WriteAllText(_tempSettingsFile, "{ \"Script_Тест\": { \"устаревший\": 1 } }");
        var manager = CreateManager();

        // Act
        PersistenceResult result = manager.ResetToDefaults(CreateTestProvider(), null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Persisted.Should().BeTrue("явный сброс подтверждается записью на диск");
        manager.GetSetting("Script_Тест", "режим", string.Empty).Should().Be("быстрый");
        manager.GetSetting("Script_Тест", "устаревший", -1).Should().Be(
            -1, "лишний ключ нормализован по схеме провайдера");
        _logService.EventsById(SettingsEventIds.DefaultsMismatch).Should().BeEmpty(
            "секция принадлежит известному скрипту");
    }

    /// <summary>
    /// Проверяет, что адаптер поверх реестра отдаёт имя скрипта и плоскую схему
    /// значений по умолчанию без неперсистентных полей.
    /// </summary>
    [TestMethod]
    public void GetScriptDefaults_RegistryScripts_MapsSchemaWithoutNonPersisted()
    {
        // Arrange
        var script = new StubScript(Mock.Of<ILogService>(), Mock.Of<ISettingsManager>(), Mock.Of<IPathManager>());
        script.SchemaOverride = new List<SettingField>
        {
            new SettingField("режим", "Режим", SettingType.Combo, "быстрый"),
            new SettingField("заголовок", "Заголовок", SettingType.Subtitle),
            new SettingField(
                "вложенный",
                "Вложенный",
                SettingType.Expander,
                false,
                childFields: new List<SettingField>
                {
                    new SettingField("глубина", "Глубина", SettingType.Int, 3)
                })
        };
        var registryMock = MockBuilders.CreateScriptRegistryMock(script);

        // Act
        ISettingsDefaultsProvider provider = new ScriptRegistrySettingsDefaultsProvider(registryMock.Object);
        IReadOnlyList<ScriptSettingsDefaults> scripts = provider.GetScriptDefaults();

        // Assert
        scripts.Should().ContainSingle();
        scripts[0].ScriptName.Should().Be(StubScript.DefaultName);
        scripts[0].Fields.Should().Contain(
            f => f.Key == "режим" && Equals(f.DefaultValue, "быстрый"));
        scripts[0].Fields.Should().NotContain(
            f => f.Key == "заголовок", "неперсистентные поля не передаются уровню настроек");
        SettingDefaultValue group = scripts[0].Fields.Single(f => f.Key == "вложенный");
        group.Children.Should().ContainSingle(c => c.Key == "глубина");
    }

    /// <summary>
    /// Проверяет, что поставщик работает с заранее построенным списком скриптов
    /// без обращения к реестру: фабрика ленивого реестра передаёт локальный список,
    /// иначе рекурсивный доступ к Scripts роняет запуск (ишью #143, ValueFactory).
    /// </summary>
    [TestMethod]
    public void GetScriptDefaults_PrebuiltList_DoesNotTouchRegistry()
    {
        // Arrange — реестр, обращение к Scripts которого бросает исключение,
        // как ленивая фабрика в момент собственного построения.
        var script = new StubScript(Mock.Of<ILogService>(), Mock.Of<ISettingsManager>(), Mock.Of<IPathManager>());
        script.SchemaOverride = new List<SettingField>
        {
            new SettingField("режим", "Режим", SettingType.Combo, "быстрый"),
        };
        var scripts = new List<AbstractScript> { script };

        // Act — провайдер поверх готового списка, реестр вообще не создаётся.
        ISettingsDefaultsProvider provider = new ScriptRegistrySettingsDefaultsProvider(scripts);
        IReadOnlyList<ScriptSettingsDefaults> defaults = provider.GetScriptDefaults();

        // Assert
        defaults.Should().ContainSingle();
        defaults[0].ScriptName.Should().Be(StubScript.DefaultName);
        defaults[0].Fields.Should().Contain(
            f => f.Key == "режим" && Equals(f.DefaultValue, "быстрый"));
    }
}
