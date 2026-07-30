// 前台服务:从 nativeLibraryDir exec .NET 引擎 sidecar(libsteamdl_engine.so),
// 配常驻通知防止下载中进程被系统回收;进程退出自动重拉。
//
// bionic .NET 运行时的两个关键环境准备:
// 1. OpenSSL: Android 无系统 libssl,APK 内打包 libssl_3.so/libcrypto_3.so,
//    这里在数据目录建 libssl.so.3/libcrypto.so.3 符号链接并通过 LD_LIBRARY_PATH
//    暴露给 .NET 的 opensslshim(它按版本化名称 dlopen)。
// 2. CA 证书: bionic 下 .NET 找不到系统证书库,从 flutter assets 释放 cacert.pem
//    并用 SSL_CERT_FILE 指定。
package app.steamdl

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.system.Os
import android.util.Log
import java.io.File
import java.io.OutputStream

class EngineService : Service() {

    companion object {
        const val PORT = 8630
        private const val TAG = "SteamDlEngine"
        private const val CHANNEL_ID = "steamdl"
        private const val NOTIFICATION_ID = 1

        @Volatile
        private var process: Process? = null

        @Volatile
        private var supervising = false
    }

    private var wakeLock: PowerManager.WakeLock? = null
    private var engineStdin: OutputStream? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(NOTIFICATION_ID, buildNotification())
        acquireWakeLock()
        startSupervisor()
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        supervising = false
        try {
            engineStdin?.close() // stdin EOF → 引擎自杀(STEAMDL_SIDECAR=1)
        } catch (_: Exception) {
        }
        process?.destroy()
        process = null
        wakeLock?.let { if (it.isHeld) it.release() }
        super.onDestroy()
    }

    private fun startSupervisor() {
        if (supervising) return
        supervising = true
        Thread {
            while (supervising) {
                try {
                    if (process?.isAlive != true) {
                        launchEngine()
                    }
                    process?.waitFor()
                    if (supervising) {
                        Log.w(TAG, "engine exited, restarting in 2s")
                        Thread.sleep(2000)
                    }
                } catch (e: InterruptedException) {
                    break
                } catch (e: Exception) {
                    Log.e(TAG, "engine supervise error: ${e.message}")
                    try {
                        Thread.sleep(3000)
                    } catch (_: InterruptedException) {
                        break
                    }
                }
            }
        }, "steamdl-engine-supervisor").start()
    }

    private fun launchEngine() {
        val nativeDir = applicationInfo.nativeLibraryDir
        val engine = File(nativeDir, "libsteamdl_engine.so")
        if (!engine.exists()) {
            throw IllegalStateException("engine binary missing: $engine")
        }

        val filesDir = filesDir.absolutePath
        val sslDir = prepareOpenSslLinks(nativeDir)
        val certFile = prepareCaCertificates()
        val dataDir = File(filesDir, "steamdl").apply { mkdirs() }
        val bundleDir = File(filesDir, "bundle").apply { mkdirs() }

        val builder = ProcessBuilder(engine.absolutePath)
            .redirectErrorStream(true)
        val env = builder.environment()
        env["HOME"] = filesDir
        env["TMPDIR"] = cacheDir.absolutePath
        env["PORT"] = PORT.toString()
        env["STEAMDL_BIND_HOST"] = "127.0.0.1"
        env["STEAMDL_SIDECAR"] = "1"
        env["STEAMDL_DATA_DIR"] = dataDir.absolutePath
        env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
        env["DOTNET_EnableWriteXorExecute"] = "0"
        env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleDir.absolutePath
        env["LD_LIBRARY_PATH"] = "${sslDir.absolutePath}:$nativeDir"
        if (certFile != null) {
            env["SSL_CERT_FILE"] = certFile.absolutePath
        }

        Log.i(TAG, "starting engine: ${engine.absolutePath}")
        val proc = builder.start()
        process = proc
        engineStdin = proc.outputStream

        // 引擎日志转发到 logcat
        Thread {
            try {
                proc.inputStream.bufferedReader().forEachLine { line ->
                    Log.i(TAG, line)
                }
            } catch (_: Exception) {
            }
        }, "steamdl-engine-log").start()
    }

    /** 为 .NET opensslshim 准备版本化命名的 OpenSSL 符号链接。 */
    private fun prepareOpenSslLinks(nativeDir: String): File {
        val sslDir = File(filesDir, "ssl").apply { mkdirs() }
        val links = mapOf(
            "libssl.so.3" to "libssl_3.so",
            "libcrypto.so.3" to "libcrypto_3.so",
        )
        for ((linkName, target) in links) {
            val targetFile = File(nativeDir, target)
            if (!targetFile.exists()) {
                Log.w(TAG, "openssl lib missing: $targetFile")
                continue
            }
            val link = File(sslDir, linkName)
            try {
                if (link.exists()) link.delete()
                Os.symlink(targetFile.absolutePath, link.absolutePath)
            } catch (e: Exception) {
                Log.w(TAG, "symlink failed for $linkName: ${e.message}")
            }
        }
        return sslDir
    }

    /** 从 flutter assets 释放 CA 证书包。 */
    private fun prepareCaCertificates(): File? {
        return try {
            val out = File(filesDir, "cacert.pem")
            assets.open("flutter_assets/assets/cacert.pem").use { input ->
                out.outputStream().use { output -> input.copyTo(output) }
            }
            out
        } catch (e: Exception) {
            Log.w(TAG, "cacert extract failed: ${e.message}")
            null
        }
    }

    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "steamdl:engine").apply {
            setReferenceCounted(false)
            acquire()
        }
    }

    private fun buildNotification(): Notification {
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Steam 下载器")
            .setContentText("下载引擎运行中")
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setOngoing(true)
            .build()
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID, "下载服务", NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "保持下载在后台持续运行"
            }
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(channel)
        }
    }
}
