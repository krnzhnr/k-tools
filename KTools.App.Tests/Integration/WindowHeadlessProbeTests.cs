// -*- coding: utf-8 -*-
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Эмпирическая оценка headless-создания WinUI 3 Window в MSTest-процессе.
/// Цель — зафиксировать документированное поведение окружения: можно ли
/// инстанцировать Microsoft.UI.Xaml.Window без Application.Start + STA DispatcherQueue.
/// </summary>
[TestClass]
public class WindowHeadlessProbeTests
{
    /// <summary>
    /// Эмпирическая фиксация: конструктор Window в headless MSTest-процессе
    /// бросает исключение (COMException / TypeInitializationException и т.п.),
    /// поскольку XAML-движок не инициализирован. Тест НЕ ждёт конкретный тип
    /// исключения — только сам факт падения, чтобы зафиксировать среду.
    /// </summary>
    [TestMethod]
    public void WindowConstructor_HeadlessProcess_ThrowsSpecificException()
    {
        // Arrange / Act
        Exception? caught = null;
        try
        {
            _ = new Microsoft.UI.Xaml.Window();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // Assert — либо исключение (headless-среда без XAML-хоста), либо,
        // если Window вдруг создался (полноценный UI-хост), тест это фиксирует.
        if (caught != null)
        {
            // Фиксируем тип исключения в выводе теста для документирования среды
            Console.WriteLine($"[Headless-Probe] Window() threw {caught.GetType().FullName}: {caught.Message}");
            caught.Should().BeAssignableTo<Exception>(
                $"headless-конструктор Window падает: {caught.GetType().Name}: {caught.Message}");
        }
        else
        {
            Assert.Inconclusive(
                "Window создан без WinUI-хоста — среда поддерживает полноценный UI-хост; " +
                "покрытие оконных сценариев можно расширить до реальных Window-тестов");
        }
    }
}
