// -*- coding: utf-8 -*-
#pragma warning disable MSTEST0037 // Отключаем предупреждение об использовании Assert.IsTrue для сравнений чисел ради читаемости

using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Core;

namespace KTools.App.Tests;

/// <summary>
/// Класс модульных тестов для валидации логики сравнения версий SemVer in VersionComparer.
/// Все комментарии и описание тестов написаны строго на русском языке.
/// </summary>
[TestClass]
public sealed class SemVerComparerTests
{
    /// <summary>
    /// Проверяет сценарий, когда удаленная стабильная версия больше локальной стабильной версии.
    /// Ожидается возвращаемое значение больше нуля.
    /// </summary>
    [TestMethod]
    public void CompareVersions_RemoteStableIsNewerThanLocalStable_ReturnsPositive()
    {
        // Arrange (Подготовка)
        string remote = "2.0.1";
        string local = "2.0.0";

        // Act (Действие)
        int result = VersionComparer.CompareVersions(remote, local);

        // Assert (Проверка)
        Assert.IsTrue(result > 0, "Версия 2.0.1 должна быть распознана как более новая по сравнению с 2.0.0");
    }

    /// <summary>
    /// Проверяет сценарий, когда удаленная стабильная версия меньше локальной стабильной версии.
    /// Ожидается возвращаемое значение меньше нуля.
    /// </summary>
    [TestMethod]
    public void CompareVersions_RemoteStableIsOlderThanLocalStable_ReturnsNegative()
    {
        // Arrange (Подготовка)
        string remote = "1.7.0";
        string local = "2.0.0";

        // Act (Действие)
        int result = VersionComparer.CompareVersions(remote, local);

        // Assert (Проверка)
        Assert.IsTrue(result < 0, "Версия 1.7.0 должна быть распознана как более старая по сравнению с 2.0.0");
    }

    /// <summary>
    /// Проверяет сценарий, когда удаленная версия без суффикса (стабильная) сравнивается с локальной версией с суффиксом (пререлиз).
    /// Стабильная версия всегда новее пререлизной. Ожидается возвращаемое значение больше нуля.
    /// </summary>
    [TestMethod]
    public void CompareVersions_RemoteStableVsLocalPreview_ReturnsPositive()
    {
        // Arrange (Подготовка)
        string remote = "2.0.0";
        string local = "2.0.0-preview.15";

        // Act (Действие)
        int result = VersionComparer.CompareVersions(remote, local);

        // Assert (Проверка)
        Assert.IsTrue(result > 0, "Стабильный релиз 2.0.0 должен быть новее, чем превью 2.0.0-preview.15");
    }

    /// <summary>
    /// Проверяет сценарий, когда удаленная превью-версия с большим номером сборки сравнивается с локальной превью-версией с меньшим номером сборки.
    /// Ожидается возвращаемое значение больше нуля.
    /// </summary>
    [TestMethod]
    public void CompareVersions_RemotePreviewIsNewerThanLocalPreview_ReturnsPositive()
    {
        // Arrange (Подготовка)
        string remote = "2.0.0-preview.42";
        string local = "2.0.0-preview.15";

        // Act (Действие)
        int result = VersionComparer.CompareVersions(remote, local);

        // Assert (Проверка)
        Assert.IsTrue(result > 0, "Версия 2.0.0-preview.42 должна быть новее, чем 2.0.0-preview.15");
    }

    /// <summary>
    /// Проверяет сценарий, когда удаленная превью-версия с меньшим номером сборки сравнивается с локальной превью-версией с большим номером сборки.
    /// Ожидается возвращаемое значение меньше нуля.
    /// </summary>
    [TestMethod]
    public void CompareVersions_RemotePreviewIsOlderThanLocalPreview_ReturnsNegative()
    {
        // Arrange (Подготовка)
        string remote = "2.0.0-preview.8";
        string local = "2.0.0-preview.12";

        // Act (Действие)
        int result = VersionComparer.CompareVersions(remote, local);

        // Assert (Проверка)
        Assert.IsTrue(result < 0, "Версия 2.0.0-preview.8 должна быть распознана как более старая по сравнению с 2.0.0-preview.12");
    }

    /// <summary>
    /// Проверяет сценарий, когда обе версии абсолютно идентичны.
    /// Ожидается возвращаемое значение равное нулю.
    /// </summary>
    [TestMethod]
    public void CompareVersions_VersionsAreEqual_ReturnsZero()
    {
        // Arrange (Подготовка)
        string remote = "2.0.0-preview.15";
        string local = "2.0.0-preview.15";

        // Act (Действие)
        int result = VersionComparer.CompareVersions(remote, local);

        // Assert (Проверка)
        Assert.AreEqual(0, result, "Одинаковые версии должны возвращать 0");
    }

    /// <summary>
    /// Проверяет корректность игнорирования префикса 'v' в строках версий.
    /// Ожидается корректное сравнение (v2.0.0-preview.15 == 2.0.0-preview.15).
    /// </summary>
    [TestMethod]
    public void CompareVersions_WithAndWithoutVPrefix_ReturnsZero()
    {
        // Arrange (Подготовка)
        string remote = "v2.0.0-preview.15";
        string local = "2.0.0-preview.15";

        // Act (Действие)
        int result = VersionComparer.CompareVersions(remote, local);

        // Assert (Проверка)
        Assert.AreEqual(0, result, "Сравнение должно игнорировать ведущую букву 'v'");
    }
}
