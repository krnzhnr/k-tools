// -*- coding: utf-8 -*-
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для провайдера дефолтных настроек SettingsDefaults.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class SettingsDefaultsTests
{
    /// <summary>
    /// Проверяет корректность стандартных шаблонов поиска.
    /// </summary>
    [TestMethod]
    public void GetDefaultSearchTemplates_ReturnsNonEmptyValidTemplates()
    {
        var templates = SettingsDefaults.GetDefaultSearchTemplates();

        templates.Should().NotBeNullOrEmpty();
        templates.Should().HaveCountGreaterThanOrEqualTo(5);
        templates.Should().Contain(t => t.Pattern.Contains("mkv"));
    }

    /// <summary>
    /// Проверяет корректность стандартных шаблонов замены.
    /// </summary>
    [TestMethod]
    public void GetDefaultReplaceTemplates_ReturnsNonEmptyValidTemplates()
    {
        var templates = SettingsDefaults.GetDefaultReplaceTemplates();

        templates.Should().NotBeNullOrEmpty();
        templates.Should().HaveCountGreaterThanOrEqualTo(10);
        templates.Should().Contain(t => t.Pattern == "$1");
    }
}
