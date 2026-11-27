// -*- coding: utf-8 -*-
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты разовой передачи путей, сброшенных на карточку домашнего экрана, в рабочую панель.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class CardDropHandoffTests
{
    /// <summary>
    /// Проверяет, что запомненные пути забираются один раз, повторный забор возвращает null.
    /// </summary>
    [TestMethod]
    public void Stage_KnownName_TakeReturnsPathsAndConsumes()
    {
        // Arrange
        const string scriptName = "Скрипт47Разовый";

        try
        {
            // Act
            CardDropHandoff.Stage(scriptName, new[] { "film.mkv", "clip.mp4" });
            var first = CardDropHandoff.Take(scriptName);
            var second = CardDropHandoff.Take(scriptName);

            // Assert
            first.Should().BeEquivalentTo("film.mkv", "clip.mp4");
            second.Should().BeNull();
        }
        finally
        {
            CardDropHandoff.Take(scriptName);
        }
    }

    /// <summary>
    /// Проверяет, что для неизвестного скрипта забор возвращает null.
    /// </summary>
    [TestMethod]
    public void Take_UnknownName_ReturnsNull()
    {
        // Act
        var result = CardDropHandoff.Take("НесуществующийСкрипт47");

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// Проверяет, что повторное запоминание для того же скрипта заменяет предыдущие пути.
    /// </summary>
    [TestMethod]
    public void Stage_SameNameTwice_OverwritesPrevious()
    {
        // Arrange
        const string scriptName = "Скрипт47Перезапись";

        try
        {
            // Act
            CardDropHandoff.Stage(scriptName, new[] { "old.mkv" });
            CardDropHandoff.Stage(scriptName, new[] { "new.mkv" });

            // Assert
            CardDropHandoff.Take(scriptName).Should().BeEquivalentTo("new.mkv");
        }
        finally
        {
            CardDropHandoff.Take(scriptName);
        }
    }

    /// <summary>
    /// Проверяет, что запоминание без имени скрипта отклоняется исключением.
    /// </summary>
    [TestMethod]
    public void Stage_BlankName_ThrowsArgumentException()
    {
        // Act
        Action act = () => CardDropHandoff.Stage("   ", new[] { "film.mkv" });

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Проверяет, что забор без имени скрипта возвращает null, а не бросает исключение.
    /// </summary>
    [TestMethod]
    public void Take_BlankName_ReturnsNull()
    {
        // Act
        var result = CardDropHandoff.Take("   ");

        // Assert
        result.Should().BeNull();
    }
}
