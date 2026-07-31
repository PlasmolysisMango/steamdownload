using Microsoft.AspNetCore.Components.WebView.Maui;
using SteamDl.Maui.Services;

namespace SteamDl.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services.AddSingleton(new ApiClient("127.0.0.1", 8630));
#if ANDROID
        builder.Services.AddSingleton<IPlatformFeatures, Platforms.Android.AndroidPlatformFeatures>();
#else
        builder.Services.AddSingleton<IPlatformFeatures, DefaultPlatformFeatures>();
#endif
        builder.Services.AddSingleton<EngineController>();
        builder.Services.AddSingleton<AppState>();

        return builder.Build();
    }
}
