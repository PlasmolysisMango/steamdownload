// Android 入口:默认 WebView 壳加载本地服务提供的同一份 index.html。
// Flutter PoC 启用时,.NET-for-Android 仍作为 APK 宿主,MainActivity 仅尝试启动 Flutter add-to-app UI。
// 服务端(WebApi + JobManager)运行在前台服务中,保证锁屏/切后台时下载不被系统回收。
using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using SteamDl.Core;

namespace SteamDl.Android
{
    [global::Android.App.Activity(
        MainLauncher = true,
        Exported = true,
        ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation
                             | global::Android.Content.PM.ConfigChanges.ScreenSize
                             | global::Android.Content.PM.ConfigChanges.UiMode)]
    public class MainActivity : Activity
    {
        const int PickDirectoryRequest = 2001;
        WebView _webView;
        TaskCompletionSource<string> _pickDirectoryCompletion;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            RequestWindowFeature(WindowFeatures.NoTitle);
            base.OnCreate(savedInstanceState);
            ActionBar?.Hide();

            // 先拉起前台服务(内含 HTTP 服务),再加载页面
            var intent = new Intent(this, typeof(global::SteamDl.Android.DownloadService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                StartForegroundService(intent);
            }
            else
            {
                StartService(intent);
            }

            WebApi.PickDirectoryHandler = PickDirectory;

#if ENABLE_FLUTTER_POC
            if (TryLaunchFlutterUi())
            {
                return;
            }
#endif

            LoadWebViewUi();
        }

        void LoadWebViewUi()
        {
            _webView = new WebView(this);
            _webView.Settings.JavaScriptEnabled = true;
            _webView.Settings.DomStorageEnabled = true;
            _webView.Settings.SetSupportZoom(false);
            _webView.Settings.BuiltInZoomControls = false;
            _webView.Settings.DisplayZoomControls = false;
            _webView.SetWebViewClient(new WebViewClient());
            SetContentView(_webView);

            // 给服务一点启动时间后加载；权限弹窗/系统设置页延后，避免用户点击图标后先被带离应用。
            _webView.PostDelayed(() =>
                _webView.LoadUrl($"http://127.0.0.1:{(global::SteamDl.Android.DownloadService.Port)}/"), 600);
            _webView.PostDelayed(RequestRuntimePermissions, 1500);
        }

#if ENABLE_FLUTTER_POC
        bool TryLaunchFlutterUi()
        {
            try
            {
                // 不在 C# 中绑定 Flutter Java API,只通过类名验证并启动 AAR 内的 FlutterActivity。
                global::Java.Lang.Class.ForName("io.flutter.embedding.android.FlutterActivity");
                var flutterIntent = new Intent();
                flutterIntent.SetClassName(PackageName, "io.flutter.embedding.android.FlutterActivity");
                StartActivity(flutterIntent);

                var placeholder = new TextView(this)
                {
                    Text = "SteamDl Flutter PoC 已启动。按返回键可回到此宿主页；下载服务继续由 .NET foreground service 保持。",
                    Gravity = GravityFlags.Center,
                };
                placeholder.SetPadding(32, 32, 32, 32);
                SetContentView(placeholder);
                placeholder.PostDelayed(RequestRuntimePermissions, 1500);
                return true;
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, "Flutter PoC 启动失败,回退 WebView: " + ex.GetType().Name, ToastLength.Long)?.Show();
                return false;
            }
        }
#endif

        void RequestRuntimePermissions()
        {
            // Android 13+ 通知权限
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
                CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
                    != global::Android.Content.PM.Permission.Granted)
            {
                RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 100);
            }

            // Android 11+ 写 /sdcard/Download 需要"所有文件访问"授权
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R && !global::Android.OS.Environment.IsExternalStorageManager)
            {
                var uri = global::Android.Net.Uri.Parse("package:" + PackageName);
                StartActivity(new Intent(
                    global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, uri));
            }
        }

        string PickDirectory()
        {
            var completion = new TaskCompletionSource<string>();
            RunOnUiThread(() =>
            {
                try
                {
                    _pickDirectoryCompletion = completion;
                    var intent = new Intent(Intent.ActionOpenDocumentTree);
                    intent.AddFlags(ActivityFlags.GrantReadUriPermission
                                    | ActivityFlags.GrantWriteUriPermission
                                    | ActivityFlags.GrantPersistableUriPermission
                                    | ActivityFlags.GrantPrefixUriPermission);
                    StartActivityForResult(intent, PickDirectoryRequest);
                }
                catch
                {
                    _pickDirectoryCompletion = null;
                    completion.TrySetResult(null);
                }
            });

            return completion.Task.Wait(TimeSpan.FromMinutes(5)) ? completion.Task.Result : null;
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != PickDirectoryRequest)
            {
                return;
            }

            var completion = _pickDirectoryCompletion;
            _pickDirectoryCompletion = null;

            if (completion == null)
            {
                return;
            }

            if (resultCode != Result.Ok || data?.Data == null)
            {
                completion.TrySetResult(null);
                return;
            }

            try
            {
                var flags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                ContentResolver.TakePersistableUriPermission(data.Data, flags);
            }
            catch
            {
                // 部分文件管理器不会授予可持久 URI 权限；真实路径可写时仍可继续。
            }

            completion.TrySetResult(ResolveTreeUriToPath(data.Data));
        }

        static string ResolveTreeUriToPath(global::Android.Net.Uri uri)
        {
            try
            {
                var docId = DocumentsContract.GetTreeDocumentId(uri);
                if (string.IsNullOrWhiteSpace(docId))
                {
                    return null;
                }

                var parts = docId.Split(':', 2);
                var volume = parts[0];
                var relative = parts.Length > 1 ? parts[1] : string.Empty;
                relative = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

                if (string.Equals(volume, "primary", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine("/storage/emulated/0", relative);
                }

                var storagePath = Path.Combine("/storage", volume, relative);
                if (Directory.Exists(storagePath))
                {
                    return storagePath;
                }

                var mediaRwPath = Path.Combine("/mnt/media_rw", volume, relative);
                return Directory.Exists(mediaRwPath) ? mediaRwPath : storagePath;
            }
            catch
            {
                return null;
            }
        }

        public override void OnBackPressed()
        {
            if (_webView != null && _webView.CanGoBack())
            {
                _webView.GoBack();
                return;
            }

            // 回到桌面但不销毁,下载继续由前台服务保活
            MoveTaskToBack(true);
        }
    }
}
