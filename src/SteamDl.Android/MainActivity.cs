// Android 入口:WebView 壳加载本地服务提供的同一份 index.html。
// 服务端(WebApi + JobManager)运行在前台服务中,保证锁屏/切后台时下载不被系统回收。
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Webkit;
using AndroidX.Activity;

namespace SteamDl.Android
{
    [global::Android.App.Activity(
        MainLauncher = true,
        Exported = true,
        ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation
                             | global::Android.Content.PM.ConfigChanges.ScreenSize
                             | global::Android.Content.PM.ConfigChanges.UiMode)]
    public class MainActivity : ComponentActivity
    {
        WebView _webView;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            RequestRuntimePermissions();

            // 先拉起前台服务(内含 HTTP 服务),再加载页面
            var intent = new Intent(this, typeof(DownloadService));
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
            SetContentView(_webView);

            // 给服务一点启动时间后加载;失败时页面下拉刷新即可
            _webView.PostDelayed(() =>
                _webView.LoadUrl($"http://127.0.0.1:{DownloadService.Port}/"), 600);
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
