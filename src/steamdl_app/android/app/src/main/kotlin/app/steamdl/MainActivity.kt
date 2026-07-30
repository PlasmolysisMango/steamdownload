// Flutter 宿主 Activity:
// - MethodChannel(app.steamdl/platform): startEngineService / pickDirectory / requestPermissions
// - SAF 目录选择结果解析为真实文件系统路径(primary/外置卡/media_rw),
//   逻辑移植自原 .NET MainActivity。
package app.steamdl

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.DocumentsContract
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.File

class MainActivity : FlutterActivity() {

    companion object {
        private const val CHANNEL = "app.steamdl/platform"
        private const val PICK_DIRECTORY_REQUEST = 2001
        private const val PERMISSION_REQUEST = 100
    }

    private var pickDirectoryResult: MethodChannel.Result? = null

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, CHANNEL)
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "startEngineService" -> {
                        try {
                            startEngineService()
                            result.success(EngineService.statusSnapshot())
                        } catch (e: Exception) {
                            result.error("engine_start_failed", e.message, e.toString())
                        }
                    }

                    "engineServiceStatus" -> {
                        result.success(EngineService.statusSnapshot())
                    }

                    "pickDirectory" -> {
                        if (pickDirectoryResult != null) {
                            result.error("busy", "目录选择进行中", null)
                        } else {
                            pickDirectoryResult = result
                            launchDirectoryPicker()
                        }
                    }

                    "requestPermissions" -> {
                        requestRuntimePermissions()
                        result.success(null)
                    }

                    else -> result.notImplemented()
                }
            }
    }

    override fun onCreate(savedInstanceState: android.os.Bundle?) {
        super.onCreate(savedInstanceState)
        // 进入界面即拉起引擎,不等待 Dart 侧首次调用
        try {
            startEngineService()
        } catch (_: Exception) {
            // Dart 侧会再次调用 startEngineService 并拿到明确错误。
        }
    }

    private fun startEngineService() {
        val intent = Intent(this, EngineService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
    }

    private fun requestRuntimePermissions() {
        val wanted = mutableListOf<String>()
        if (checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE)
            != PackageManager.PERMISSION_GRANTED
        ) {
            wanted += Manifest.permission.WRITE_EXTERNAL_STORAGE
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission("android.permission.POST_NOTIFICATIONS")
            != PackageManager.PERMISSION_GRANTED
        ) {
            wanted += "android.permission.POST_NOTIFICATIONS"
        }
        if (wanted.isNotEmpty()) {
            requestPermissions(wanted.toTypedArray(), PERMISSION_REQUEST)
        }
    }

    private fun launchDirectoryPicker() {
        try {
            val intent = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
                addFlags(
                    Intent.FLAG_GRANT_READ_URI_PERMISSION
                            or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                            or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION
                            or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION
                )
            }
            startActivityForResult(intent, PICK_DIRECTORY_REQUEST)
        } catch (e: Exception) {
            pickDirectoryResult?.success(null)
            pickDirectoryResult = null
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != PICK_DIRECTORY_REQUEST) return

        val result = pickDirectoryResult ?: return
        pickDirectoryResult = null

        val uri = data?.data
        if (resultCode != RESULT_OK || uri == null) {
            result.success(null)
            return
        }

        try {
            val flags = (data.flags
                    and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION))
            contentResolver.takePersistableUriPermission(uri, flags)
        } catch (_: Exception) {
            // 部分文件管理器不授予可持久 URI 权限;真实路径可写时仍可继续
        }

        result.success(resolveTreeUriToPath(uri))
    }

    private fun resolveTreeUriToPath(uri: Uri): String? {
        return try {
            val docId = DocumentsContract.getTreeDocumentId(uri)
            if (docId.isNullOrBlank()) return null

            val parts = docId.split(":", limit = 2)
            val volume = parts[0]
            val relative = (parts.getOrNull(1) ?: "").trimStart('/')

            if (volume.equals("primary", ignoreCase = true)) {
                return File("/storage/emulated/0", relative).absolutePath
            }

            val storagePath = File("/storage/$volume", relative)
            if (storagePath.exists()) return storagePath.absolutePath

            val mediaRwPath = File("/mnt/media_rw/$volume", relative)
            if (mediaRwPath.exists()) mediaRwPath.absolutePath else storagePath.absolutePath
        } catch (e: Exception) {
            null
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        // 回到桌面但不销毁,下载继续由前台服务保活
        moveTaskToBack(true)
    }
}
