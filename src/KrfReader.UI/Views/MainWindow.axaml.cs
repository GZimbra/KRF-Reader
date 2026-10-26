using Avalonia.Controls;
using KrfReader.UI.ViewModels;

namespace KrfReader.UI.Views;

public partial class MainWindow : Window
{
    private bool _closeInProgress;

    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Fechamento em duas fases: cancela o primeiro Close, encerra o pipeline (drena e persiste)
    /// e então fecha de verdade.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || DataContext is not MainWindowViewModel vm || vm.IsShutdownComplete)
            return;

        e.Cancel = true;
        if (_closeInProgress)
            return;
        _closeInProgress = true;

        try
        {
            await vm.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Erro no encerramento: {ex}");
        }
        finally
        {
            Close();
        }
    }
}
