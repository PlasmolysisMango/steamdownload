// 前台服务:承载 WebApi + 下载引擎,配常驻通知,防止下载中进程被系统回收
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using SteamDl.Core;

namespace SteamDl.Android
{
    [Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
    public class DownloadService : Service
    {
        public const int Port = 8630;
        const string ChannelId = "steamdl";
        const int NotificationId = 1;

        static bool _started;

        public override void OnCreate()
        {
            base.OnCreate();
            CreateNotificationChannel();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("Steam 下载器")
                .SetContentText($"服务运行中 · http://127.0.0.1:{Port}")
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
                .SetOngoing(true)
                .Build();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                StartForeground(NotificationId, notification,
                    global::Android.Content.PM.ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }

            if (!_started)
            {
                _started = true;
                // Console 中继 + 任务管理器接线,再启动本地 HTTP 服务
                ConsoleRelay.Instance.Install();
                _ = JobManager.Instance;
                WebApi.OpenPathHandler = OpenDirectory;
                new WebApi(Port, "127.0.0.1").Start();
            }

            return StartCommandResult.Sticky;
        }

        public override IBinder OnBind(Intent intent) => null;

        bool OpenDirectory(string path)
        {
            try
            {
                var uri = global::Android.Net.Uri.Parse("file://" + path.TrimEnd('/') + "/");
                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, "resource/folder");
                intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                StartActivity(Intent.CreateChooser(intent, "打开下载目录"));
                return true;
            }
            catch
            {
                Toast.MakeText(this, "无法自动打开目录,请在文件管理器中手动进入: " + path, ToastLength.Long)?.Show();
                return false;
            }
        }

        void CreateNotificationChannel()
        {
            var channel = new NotificationChannel(
                ChannelId, "下载服务", NotificationImportance.Low)
            {
                Description = "保持下载在后台持续运行",
            };
            var manager = (NotificationManager)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }
    }
}
