package io.tcproute.android

import kotlin.concurrent.thread

internal class TcpRouteSessionLog(val name: String) {
    private val chunks = ArrayDeque<String>()
    private var size = 0
    private var truncated = false

    @Synchronized
    fun appendText(text: String) {
        val chunk = text.takeLast(16 * 1024)
        if (chunk.length < text.length) truncated = true
        chunks.addLast(chunk)
        size += chunk.toByteArray(Charsets.UTF_8).size
        while (size > TcpRouteLogReader.DEFAULT_MAX_BYTES && chunks.isNotEmpty()) {
            size -= chunks.removeFirst().toByteArray(Charsets.UTF_8).size
            truncated = true
        }
    }

    @Synchronized
    fun clear() { chunks.clear(); size = 0; truncated = false }

    @Synchronized
    fun snapshot() = TcpRouteLogSnapshot(chunks.joinToString("").trimEnd(), truncated)
}

internal object TcpRouteSessionLogs {
    private val sessions = mutableMapOf<String, TcpRouteSessionLog>()
    @Volatile private var latest = TcpRouteSessionLog("TcpRoute2")

    @Synchronized
    fun begin(mode: String): TcpRouteSessionLog = TcpRouteSessionLog(mode).also { sessions[mode] = it; latest = it }

    @Synchronized
    fun get(mode: String): TcpRouteSessionLog = sessions.getOrPut(mode) { TcpRouteSessionLog(mode) }

    fun active(): TcpRouteSessionLog = latest

    fun capture(process: Process, log: TcpRouteSessionLog) {
        thread(name = "TcpRoute2-output", isDaemon = true) {
            runCatching {
                process.inputStream.bufferedReader().use { reader ->
                    val chunk = CharArray(8192)
                    while (true) {
                        val count = reader.read(chunk)
                        if (count < 0) break
                        log.appendText(String(chunk, 0, count))
                    }
                }
            }
        }
    }
}
