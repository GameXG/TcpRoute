package io.tcproute.android

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.ScrollView
import android.widget.TextView
import java.util.concurrent.Executors

class RequestLogPresenter(
    private val context: Context,
    private val logScroll: ScrollView,
    private val logText: TextView,
    private val logStatus: TextView
) {
    private val mainHandler = Handler(Looper.getMainLooper())
    private val logReader = Executors.newSingleThreadExecutor()
    private var currentDisplay = ""
    private var currentSource = ""
    private var refreshActive = false
    private var destroyed = false

    private val refreshLogs = object : Runnable {
        override fun run() {
            loadLatestLog()
            if (refreshActive) mainHandler.postDelayed(this, REFRESH_INTERVAL_MS)
        }
    }

    fun start() {
        if (destroyed || refreshActive) return
        refreshActive = true
        mainHandler.post(refreshLogs)
    }

    fun stop() {
        refreshActive = false
        mainHandler.removeCallbacks(refreshLogs)
    }

    fun destroy() {
        stop()
        destroyed = true
        logReader.shutdownNow()
    }

    fun clear(onComplete: (Boolean) -> Unit) {
        logReader.execute {
            val source = TcpRouteSessionLogs.active()
            val succeeded = runCatching { source.clear() }.isSuccess
            mainHandler.post {
                if (destroyed) return@post
                if (succeeded) {
                    currentSource = ""
                    currentDisplay = ""
                    renderSnapshot(source.name, TcpRouteLogSnapshot("", false))
                }
                onComplete(succeeded)
            }
        }
    }

    private fun loadLatestLog() {
        logReader.execute {
            val source = TcpRouteSessionLogs.active()
            val snapshot = runCatching {
                source.snapshot()
            }.getOrElse { TcpRouteLogSnapshot(it.message.orEmpty(), false) }
            mainHandler.post {
                if (!destroyed) renderSnapshot(source.name, snapshot)
            }
        }
    }

    private fun renderSnapshot(source: String, snapshot: TcpRouteLogSnapshot) {
        val display = when {
            snapshot.content.isBlank() -> context.getString(R.string.request_log_empty)
            snapshot.truncated -> context.getString(R.string.request_log_truncated, snapshot.content)
            else -> snapshot.content
        }
        if (source == currentSource && display == currentDisplay) return

        val wasFollowing = source != currentSource || currentDisplay.isEmpty() ||
            logScroll.scrollY + logScroll.height >= logText.height - FOLLOW_THRESHOLD_PX
        currentSource = source
        currentDisplay = display
        logText.text = display
        logStatus.setText(if (snapshot.content.isBlank()) R.string.request_log_waiting else R.string.request_log_live)
        if (wasFollowing) logScroll.post { logScroll.fullScroll(View.FOCUS_DOWN) }
    }

    companion object {
        private const val REFRESH_INTERVAL_MS = 1_000L
        private const val FOLLOW_THRESHOLD_PX = 48
    }
}
