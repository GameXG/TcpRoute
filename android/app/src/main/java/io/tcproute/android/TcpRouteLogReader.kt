package io.tcproute.android

import android.content.Context
import java.io.File
import java.io.RandomAccessFile

internal data class TcpRouteLogSnapshot(
    val content: String,
    val truncated: Boolean
)

internal object TcpRouteLogReader {
    const val DEFAULT_MAX_BYTES = 256 * 1024

    fun read(file: File, maxBytes: Int = DEFAULT_MAX_BYTES): TcpRouteLogSnapshot {
        require(maxBytes > 0) { "maxBytes must be positive" }
        if (!file.isFile || file.length() == 0L) return TcpRouteLogSnapshot("", false)

        return RandomAccessFile(file, "r").use { input ->
            val start = (input.length() - maxBytes).coerceAtLeast(0L)
            input.seek(start)
            val bytes = ByteArray((input.length() - start).toInt())
            input.readFully(bytes)
            var content = bytes.toString(Charsets.UTF_8)
            if (start > 0L) content = content.substringAfter('\n', content)
            TcpRouteLogSnapshot(content.trimEnd(), start > 0L)
        }
    }
}

internal object TcpRouteLogFiles {
    fun active(context: Context): File = select(
        context.filesDir,
        vpnActive = TcpRouteVpnService.currentState() in ACTIVE_STATES,
        socksActive = TcpRouteService.currentState() in ACTIVE_STATES
    )

    fun select(directory: File, vpnActive: Boolean, socksActive: Boolean): File {
        val vpnLog = File(directory, TcpRouteVpnService.LOG_FILE_NAME)
        val socksLog = File(directory, TcpRouteService.LOG_FILE_NAME)
        return when {
            vpnActive -> vpnLog
            socksActive -> socksLog
            vpnLog.lastModified() > socksLog.lastModified() -> vpnLog
            else -> socksLog
        }
    }

    fun clear(file: File) {
        file.parentFile?.mkdirs()
        file.writeText("")
    }

    private val ACTIVE_STATES = setOf(
        TcpRouteVpnService.STATE_STARTING,
        TcpRouteVpnService.STATE_RUNNING
    )
}
