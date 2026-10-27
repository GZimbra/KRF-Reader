using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KrfReader.UI.ViewModels;
using KrfReader.UI.Views;

namespace KrfReader.UI;

public partial class App : Avalonia.Application
{
    private CompositionRoot? _root;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _root = CompositionRoot.Create();

            var viewModel = new MainWindowViewModel(
                _root.Repository,
                _root.ProtocolFactory,
                _root.CreatePipeline,
                _root.ReaderOptions,
                _root.PipelineOptions);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => _root?.Dispose();

            // Banco + leitor iniciam após a janela abrir; falhas vão para a barra de status.
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
