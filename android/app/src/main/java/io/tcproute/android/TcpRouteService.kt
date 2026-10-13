package io.tcproute.android

import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.os.Handler
import android.os.Looper
import java.time.Instant

class TcpRouteService : Service() {
    private val mainHandler = Handler(Looper.getMainLooper())
    private var process: Process? = null
    private var stopping = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> stopTcpRoute(notifyStopped = true)
            else -> startTcpRoute(
                allowLan = intent?.getBooleanExtra(EXTRA_ALLOW_LAN, false) ?: false,
                allowStun = intent?.getBooleanExtra(EXTRA_ALLOW_STUN, false) ?: false,
                allowQuic = intent?.getBooleanExtra(EXTRA_ALLOW_QUIC, false) ?: false,
                enableIpv6 = intent?.getBooleanExtra(EXTRA_ENABLE_IPV6, false) ?: false
            )
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        releaseProcess()
        if (currentState() != STATE_ERROR) publishStatus(STATE_STOPPED)
        super.onDestroy()
    }

    private fun startTcpRoute(allowLan: Boolean, allowStun: Boolean, allowQuic: Boolean, enableIpv6: Boolean) {
        if (process?.isAlive == true) {
            publishStatus(STATE_RUNNING)
            return
        }

        try {
            stopping = false
            publishStatus(STATE_STARTING)
            val notification = TcpRouteNotification.build(this, R.string.notification_socks_ready)
            startForeground(NOTIFICATION_ID, notification)

            val binary = TcpRouteBinaryInstaller.install(this)
            val config = TcpRouteConfigStore.ensureDefaultConfig(this)
            val runtime = TcpRouteConfigStore.writeRuntimeConfig(this, config, allowLan = allowLan)
            val logFile = TcpRouteSessionLogs.begin("SOCKS").apply {
                appendText("TcpRoute2 SOCKS session started at ${Instant.now()}\n")
            }

            val processBuilder = ProcessBuilder(binary.absolutePath, "-config", runtime.absolutePath)
            processBuilder.environment()["xray.location.asset"] = filesDir.absolutePath
            processBuilder.environment()["TCPROUTE_ALLOW_STUN"] = allowStun.toString()
            processBuilder.environment()["TCPROUTE_ALLOW_QUIC"] = allowQuic.toString()
            processBuilder.environment()["TCPROUTE_ENABLE_IPV6"] = enableIpv6.toString()
            val started = processBuilder
                .redirectErrorStream(true)
                .start()
            process = started
            TcpRouteSessionLogs.capture(started, logFile)
            mainHandler.postDelayed({
                if (!stopping && process === started) {
                    if (started.isAlive) {
                        publishStatus(STATE_RUNNING)
                    } else {
                        logFile.appendText("SOCKS startup failed; exit code ${started.exitValue()}\n")
                        publishStatus(STATE_ERROR)
                        stopTcpRoute()
                    }
                }
            }, STARTUP_CONFIRMATION_DELAY_MS)
        } catch (error: Throwable) {
            TcpRouteSessionLogs.get("SOCKS").appendText("SOCKS start failed: ${error.message}\n")
            publishStatus(STATE_ERROR)
            stopTcpRoute()
        }
    }

    private fun stopTcpRoute(notifyStopped: Boolean = false) {
        releaseProcess()
        if (notifyStopped && currentState() != STATE_ERROR) publishStatus(STATE_STOPPED)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun releaseProcess() {
        if (stopping && process == null) return
        stopping = true
        mainHandler.removeCallbacksAndMessages(null)
        process?.destroy()
        process = null
    }

    private fun publishStatus(state: String) {
        runtimeState = state
        sendBroadcast(
            Intent(ACTION_STATUS)
                .setPackage(packageName)
                .putExtra(EXTRA_STATE, state)
        )
    }

    companion object {
        const val ACTION_START = "io.tcproute.android.START"
        const val ACTION_STOP = "io.tcproute.android.STOP"
        const val ACTION_STATUS = "io.tcproute.android.SOCKS_STATUS"
        const val EXTRA_ALLOW_LAN = "io.tcproute.android.ALLOW_LAN"
        const val EXTRA_ALLOW_STUN = "io.tcproute.android.ALLOW_STUN"
        const val EXTRA_ALLOW_QUIC = "io.tcproute.android.ALLOW_QUIC"
        const val EXTRA_ENABLE_IPV6 = "io.tcproute.android.ENABLE_IPV6"
        const val EXTRA_STATE = "io.tcproute.android.SOCKS_STATE"
        const val STATE_STARTING = "starting"
        const val STATE_RUNNING = "running"
        const val STATE_STOPPED = "stopped"
        const val STATE_ERROR = "error"
        const val LOG_FILE_NAME = "tcproute.log"
        private const val NOTIFICATION_ID = 7070
        private const val STARTUP_CONFIRMATION_DELAY_MS = 500L
        @Volatile private var runtimeState: String? = null

        fun currentState(): String? = runtimeState
    }
}
