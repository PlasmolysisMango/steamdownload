// 前台服务:将 .NET 引擎 sidecar 自包含发布整个目录(作为 Android 原生 asset zip
// 随 APK 打包)解包到 filesDir 后 exec;配常驻通知防止下载中进程被系统回收;
// 进程退出自动重拉。
//
// 为何不用 single-file/jniLibs: linux-bionic-arm64 的 self-contained +
// PublishSingleFile 在 .NET SDK 里是已知缺陷(dotnet/sdk#35518),产物缺
// libhostfxr.so 等文件,在设备上报 "You must install .NET"。因此 CI 改用
// 普通 self-contained 发布(产出一堆松散文件),整个目录打包为
// assets/engine-bionic.zip;这里在首次启动(或 APK 更新后)解包到
// filesDir/engine-bionic,保持所有文件同目录,让 apphost/hostfxr 按同目录
// 规则相互找到。
//
// bionic .NET 运行时的两个关键环境准备:
// 1. OpenSSL: Android 无系统 libssl,CI 已将 libssl.so.3/libcrypto.so.3(版本化
//    SONAME)与引擎同目录打包,无需再在设备上建符号链接。
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
import android.util.Log
import java.io.File
import java.io.OutputStream
import java.util.zip.ZipInputStream

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

        @Volatile
        private var status = "idle"

        @Volatile
        private var error = ""

        @Volatile
        private var lastOutput = ""

        fun statusSnapshot(): Map<String, Any> = mapOf(
            "status" to status,
            "error" to error,
            "last_output" to lastOutput,
            "process_alive" to (process?.isAlive == true),
        )

        private fun setStatus(value: String, message: String = "") {
            status = value
            error = message
            if (message.isNotBlank()) Log.e(TAG, "engine status=$value: $message")
        }

        private fun appendOutput(line: String) {
            val merged = if (lastOutput.isBlank()) line else "$lastOutput\n$line"
            lastOutput = if (merged.length > 3000) merged.takeLast(3000) else merged
        }
    }

    private var wakeLock: PowerManager.WakeLock? = null
    private var engineStdin: OutputStream? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        setStatus("service_started")
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
        Thread({
            while (supervising) {
                try {
                    if (process?.isAlive != true) {
                        setStatus("starting")
                        launchEngine()
                    }
                    val exitCode = process?.waitFor()
                    if (supervising) {
                        val message = "engine exited code=${exitCode ?: "unknown"}"
                        setStatus("exited", message)
                        process = null
                        Log.w(TAG, "$message, restarting in 2s")
                        Thread.sleep(2000)
                    }
                } catch (e: InterruptedException) {
                    break
                } catch (e: Exception) {
                    setStatus("failed", e.toString())
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
        val engineDir = prepareEngineBundle()
        val engine = File(engineDir, "steamdl-engine")
        if (!engine.exists()) {
            throw IllegalStateException("engine binary missing: $engine")
        }
        engine.setExecutable(true, false)
        if (!engine.canExecute()) {
            Log.w(TAG, "engine binary is not marked executable: $engine")
        }

        val filesDir = filesDir.absolutePath
        val certFile = prepareCaCertificates()
        val dataDir = File(filesDir, "steamdl").apply { mkdirs() }
        val bundleDir = File(filesDir, "bundle").apply { mkdirs() }

        val builder = ProcessBuilder(engine.absolutePath)
            .redirectErrorStream(true)
            .directory(engineDir)
        val env = builder.environment()
        env["HOME"] = filesDir
        env["DOTNET_ROOT"] = engineDir.absolutePath
        env["TMPDIR"] = cacheDir.absolutePath
        env["PORT"] = PORT.toString()
        env["STEAMDL_BIND_HOST"] = "127.0.0.1"
        env["STEAMDL_SIDECAR"] = "1"
        env["STEAMDL_DATA_DIR"] = dataDir.absolutePath
        env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
        env["DOTNET_EnableWriteXorExecute"] = "0"
        env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleDir.absolutePath
        env["LD_LIBRARY_PATH"] = engineDir.absolutePath
        if (certFile != null) {
            env["SSL_CERT_FILE"] = certFile.absolutePath
        }

        Log.i(TAG, "starting engine: ${engine.absolutePath}")
        val proc = builder.start()
        process = proc
        engineStdin = proc.outputStream
        setStatus("process_started")

        // 引擎日志转发到 logcat
        Thread({
            try {
                proc.inputStream.bufferedReader().forEachLine { line ->
                    appendOutput(line)
                    if (line.contains("HTTP API listening")) setStatus("http_listening")
                    Log.i(TAG, line)
                }
            } catch (_: Exception) {
            }
        }, "steamdl-engine-log").start()
    }

    /**
     * 从 assets/engine-bionic.zip 解包 .NET 引擎自包含发布到 filesDir/engine-bionic,
     * 仅在首次运行或 APK 更新后重新解包(用 APK 安装/更新时间作为缓存标识)。
     */
    private fun prepareEngineBundle(): File {
        val dir = File(filesDir, "engine-bionic")
        val marker = File(filesDir, "engine-bionic.version")
        val lastUpdate = try {
            packageManager.getPackageInfo(packageName, 0).lastUpdateTime.toString()
        } catch (_: Exception) {
            "unknown"
        }
        if (dir.isDirectory && File(dir, "steamdl-engine").exists() &&
            marker.exists() && marker.readText() == lastUpdate
        ) {
            return dir
        }

        Log.i(TAG, "extracting engine bundle to $dir")
        dir.deleteRecursively()
        dir.mkdirs()
        assets.open("engine-bionic.zip").use { input ->
            ZipInputStream(input).use { zip ->
                var entry = zip.nextEntry
                while (entry != null) {
                    if (!entry.isDirectory) {
                        val outFile = File(dir, entry.name)
                        outFile.outputStream().use { output -> zip.copyTo(output) }
                    }
                    zip.closeEntry()
                    entry = zip.nextEntry
                }
            }
        }
        File(dir, "steamdl-engine").setExecutable(true, false)
        marker.writeText(lastUpdate)
        logNativeLibraryState(dir)
        return dir
    }

    /** 记录引擎包里关键依赖是否解包成功。 */
    private fun logNativeLibraryState(engineDir: File) {
        for (name in listOf(
            "steamdl-engine",
            "libhostfxr.so",
            "libhostpolicy.so",
            "libcoreclr.so",
            "libe_sqlite3.so",
            "libssl.so.3",
            "libcrypto.so.3",
        )) {
            val file = File(engineDir, name)
            if (file.exists()) {
                Log.i(TAG, "engine file ok: ${file.absolutePath} (${file.length()} bytes)")
            } else {
                Log.e(TAG, "engine file missing: ${file.absolutePath}")
            }
        }
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
