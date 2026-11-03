using System.Windows;
using System.Windows.Threading;

namespace VideoCutter.Desktop;

public partial class App : Application
{
    public App()
    {
        // Si algo inesperado falla, se muestra un aviso en vez de cerrar el programa sin explicación.
        DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(e.Exception.Message, "VideoCutter · error inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
