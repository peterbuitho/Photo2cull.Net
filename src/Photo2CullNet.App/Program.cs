using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace Photo2CullNet.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);
        appBuilder.Services.AddLogging();
        appBuilder.RootComponents.Add<App>("app");

        var app = appBuilder.Build();

        app.MainWindow
            .SetTitle("Photo2Cull")
            .SetSize(1300, 850)
            .Center();

        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject.ToString() ?? "unknown error");
        };

        app.Run();
    }
}
