// Android 入口:WebView 壳加载本地服务提供的同一份 index.html。
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
            base.OnCreate(savedInstanceState);

            RequestRuntimePermissions();

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

            _webView = new WebView(this);
            _webView.Settings.JavaScriptEnabled = true;
            _webView.Settings.DomStorageEnabled = true;
            _webView.SetWebViewClient(new WebViewClient());
            WebApi.PickDirectoryHandler = PickDirectory;
            SetContentView(_webView);

            // 给服务一点启动时间后加载;失败时页面下拉刷新即可
            _webView.PostDelayed(() =>
                _webView.LoadUrl($"http://127.0.0.1:{(global::SteamDl.Android.DownloadService.Port)}/"), 600);
        }

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
