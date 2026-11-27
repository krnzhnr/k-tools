// -*- coding: utf-8 -*-
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты ThreadSafeViewModel — базового класса потокобезопасных моделей представления.
/// Проверяют, что очередь диспетчера не захватывается в конструкторе (ишью 126),
/// а резолвится лениво через главное окно: создание модели в фоновом потоке
/// не теряет уведомления PropertyChanged и не требует инициализированного WinRT.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class ThreadSafeViewModelTests
{
    /// <summary>
    /// Минимальная проверяемая модель для тестов базового класса.
    /// </summary>
    private sealed class TestViewModel : ThreadSafeViewModel
    {
        private string _title = string.Empty;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
    }

    /// <summary>
    /// Проверяет, что конструктор не хранит DispatcherQueue в полях экземпляра:
    /// очередь обязана резолвиться лениво через главное окно при каждом уведомлении.
    /// </summary>
    [TestMethod]
    public void Constructor_AnyThread_HasNoCapturedDispatcherQueueField()
    {
        // Act
        string[] queueFields = typeof(ThreadSafeViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType.FullName == "Microsoft.UI.Dispatching.DispatcherQueue")
            .Select(f => f.Name)
            .ToArray();

        // Assert
        queueFields.Should().BeEmpty(
            "очередь диспетчера должна резолвиться лениво через главное окно, а не захватываться в конструкторе");
    }

    /// <summary>
    /// Проверяет, что модель, созданная в фоновом потоке, синхронно доставляет
    /// уведомления PropertyChanged без инициализированного окна.
    /// </summary>
    [TestMethod]
    public async Task OnPropertyChanged_BackgroundCreation_RaisesEventSynchronously()
    {
        // Act
        string? received = await Task.Run(() =>
        {
            var vm = new TestViewModel();
            string? observed = null;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TestViewModel.Title))
                {
                    observed = vm.Title;
                }
            };
            vm.Title = "Фон";
            return observed;
        });

        // Assert
        received.Should().Be("Фон", "уведомление, вызванное в фоне без окна, доставляется синхронно");
    }
}
