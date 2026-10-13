package io.tcproute.android

import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.net.ProxyInfo
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import java.io.File
import java.util.concurrent.TimeUnit

class TcpRouteVpnService : VpnService() {
    private val mainHandler = Handler(Looper.getMainLooper())
    private var vpnInterface: ParcelFileDescriptor? = null
    private var tcpRouteProcess: Process? = null
    private var tun2ProxyPid: Int? = null
    private var sessionId = 0L
    @Volatile private var stopping = false

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> stopVpn(notifyStopped = true)
            else -> startVpn(
                VpnFeaturePolicy(
                    allowStun = intent?.getBooleanExtra(EXTRA_ALLOW_STUN, false) ?: false,
                    allowQuic = intent?.getBooleanExtra(EXTRA_ALLOW_QUIC, false) ?: false,
                    enableIpv6 = intent?.getBooleanExtra(EXTRA_ENABLE_IPV6, false) ?: false
                )
            )
        }
        return START_NOT_STICKY
    }

    override fun onRevoke() {
        stopVpn(notifyStopped = true)
        super.onRevoke()
    }

    override fun onDestroy() {
        releaseResources()
        if (currentState() != STATE_ERROR) publishStatus(STATE_STOPPED)
        super.onDestroy()
    }

    private fun startVpn(policy: VpnFeaturePolicy) {
        val existingPid = tun2ProxyPid
        if (tcpRouteProcess?.isAlive == true && existingPid != null && Tun2ProxyBridge.poll(existingPid) == 0) {
            publishStatus(STATE_RUNNING)
            return
        }

        try {
            releaseResources()
            stopping = false
            val activeSession = ++sessionId
            publishStatus(STATE_STARTING)
            startForeground(NOTIFICATION_ID, TcpRouteNotification.build(this, R.string.notification_vpn_running))

            val config = TcpRouteConfigStore.ensureDefaultConfig(this)
            val runtime = TcpRouteConfigStore.writeRuntimeConfig(this, config, allowLan = false)
            val port = TcpRouteConfigStore.readListenPort(runtime)
            val logFile = TcpRouteSessionLogs.begin("VPN")

            val tcpRouteBinary = TcpRouteBinaryInstaller.install(this)
            val processBuilder = ProcessBuilder(tcpRouteBinary.absolutePath, "-config", runtime.absolutePath)
            processBuilder.environment()["xray.location.asset"] = filesDir.absolutePath
            processBuilder.environment()["TCPROUTE_ALLOW_STUN"] = policy.allowStun.toString()
            processBuilder.environment()["TCPROUTE_ALLOW_QUIC"] = policy.allowQuic.toString()
            processBuilder.environment()["TCPROUTE_ENABLE_IPV6"] = policy.enableIpv6.toString()
            processBuilder.environment()["TCPROUTE_TUN_DNS"] = "true"
            tcpRouteProcess = processBuilder
                .redirectErrorStream(true)
                .start()
            TcpRouteSessionLogs.capture(tcpRouteProcess!!, logFile)

            val builder = Builder()
                .setSession("TcpRoute2")
                .setMtu(1500)
                .addAddress(TUN_ADDRESS, 32)
                .addDnsServer("198.18.0.1")
                .addRoute("0.0.0.0", 0)
            if (SystemProxySettings.enabled(this)) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    builder.setHttpProxy(ProxyInfo.buildDirectProxy("127.0.0.1", port))
                } else {
                    error("VPN network proxy requires Android 10 or later.")
                }
            }

            if (policy.enableIpv6) {
                builder.addAddress(TUN_IPV6_ADDRESS, 128)
                builder.addRoute("::", 0)
            }

            applyAppRoutingPolicy(builder, AppRoutingPolicyStore.load(this))

            vpnInterface = builder
                .establish()
                ?: error("Unable to establish Android VPN interface.")

            val tunFd = vpnInterface?.fd ?: error("VPN interface is not available.")
            startTun2Proxy(activeSession, tunFd, port, logFile)
        } catch (error: Throwable) {
            TcpRouteSessionLogs.get("VPN").appendText("VPN start failed: ${error.message}\n")
            publishStatus(STATE_ERROR)
            stopVpn()
        }
    }

    private fun startTun2Proxy(activeSession: Long, tunFd: Int, socksPort: Int, logFile: TcpRouteSessionLog) {
        val runner = File(applicationInfo.nativeLibraryDir, TUN2PROXY_RUNNER)
        check(runner.canExecute()) { "tun2proxy runner is not executable: ${runner.absolutePath}" }
        val pid = Tun2ProxyBridge.start(runner.absolutePath, tunFd, socksPort, VPN_MTU)
        check(pid > 0) { "Unable to start tun2proxy child process: $pid" }
        tun2ProxyPid = pid

        mainHandler.postDelayed({
            if (stopping || activeSession != sessionId || pid != tun2ProxyPid) return@postDelayed

            if (Tun2ProxyBridge.poll(pid) == 0 && tcpRouteProcess?.isAlive == true) {
                logFile.appendText("VPN bridge running on TUN fd $tunFd via SOCKS port $socksPort\n")
                publishStatus(STATE_RUNNING)
                monitorSession(activeSession, pid, logFile)
            } else {
                val backendExit = runCatching { tcpRouteProcess?.exitValue() }.getOrNull()
                logFile.appendText("VPN startup failed; TcpRoute2 exit code: ${backendExit ?: "still running"}\n")
                publishStatus(STATE_ERROR)
                stopVpn()
            }
        }, STARTUP_CONFIRMATION_DELAY_MS)
    }

    private fun monitorSession(activeSession: Long, pid: Int, logFile: TcpRouteSessionLog) {
        mainHandler.postDelayed({
            if (stopping || activeSession != sessionId || pid != tun2ProxyPid) return@postDelayed

            if (Tun2ProxyBridge.poll(pid) == 0 && tcpRouteProcess?.isAlive == true) {
                monitorSession(activeSession, pid, logFile)
            } else {
                logFile.appendText("VPN child process exited unexpectedly.\n")
                publishStatus(STATE_ERROR)
                stopVpn()
            }
        }, PROCESS_MONITOR_INTERVAL_MS)
    }

    private fun stopVpn(notifyStopped: Boolean = false) {
        releaseResources()
        if (notifyStopped) publishStatus(STATE_STOPPED)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun releaseResources() {
        if (stopping && tun2ProxyPid == null && tcpRouteProcess == null && vpnInterface == null) return

        stopping = true
        sessionId++
        mainHandler.removeCallbacksAndMessages(null)

        tun2ProxyPid?.let { pid -> runCatching { Tun2ProxyBridge.stop(pid) } }
        tun2ProxyPid = null

        tcpRouteProcess?.let { process ->
            process.destroy()
            if (!runCatching { process.waitFor(500, TimeUnit.MILLISECONDS) }.getOrDefault(false)) {
                process.destroyForcibly()
            }
        }
        tcpRouteProcess = null
        runCatching { vpnInterface?.close() }
        vpnInterface = null
    }

    private fun publishStatus(state: String) {
        runtimeState = state
        sendBroadcast(
            Intent(ACTION_STATUS)
                .setPackage(packageName)
                .putExtra(EXTRA_STATE, state)
        )
    }

    private fun applyAppRoutingPolicy(builder: Builder, policy: AppRoutingPolicy) {
        val packages = policy.packages.filterNot { it == packageName }.sorted()

        when (policy.mode) {
            AppRoutingMode.ALL -> Unit
            AppRoutingMode.ALLOWLIST -> {
                if (packages.isEmpty()) {
                    error("VPN allowlist is empty; select at least one app or switch to all-app routing.")
                }
                val addedApps = packages.count { addAllowedApplication(builder, it) }
                if (addedApps == 0) {
                    error("VPN allowlist has no installed apps; update the selected app list.")
                }
            }
            AppRoutingMode.BLOCKLIST -> {
                packages.forEach { addDisallowedApplication(builder, it) }
            }
        }

        if (policy.mode != AppRoutingMode.ALLOWLIST || packageName in packages) {
            addDisallowedApplication(builder, packageName)
        }
    }

    private fun addAllowedApplication(builder: Builder, packageName: String): Boolean {
        try {
            builder.addAllowedApplication(packageName)
            return true
        } catch (_: PackageManager.NameNotFoundException) {
            TcpRouteSessionLogs.get("VPN").appendText("Ignoring missing allowlist app: $packageName\n")
            return false
        }
    }

    private fun addDisallowedApplication(builder: Builder, packageName: String) {
        try {
            builder.addDisallowedApplication(packageName)
        } catch (_: PackageManager.NameNotFoundException) {
            TcpRouteSessionLogs.get("VPN").appendText("Ignoring missing bypass app: $packageName\n")
        }
    }

    companion object {
        const val ACTION_START = "io.tcproute.android.VPN_START"
        const val ACTION_STOP = "io.tcproute.android.VPN_STOP"
        const val ACTION_STATUS = "io.tcproute.android.VPN_STATUS"
        const val EXTRA_STATE = "io.tcproute.android.VPN_STATE"
        const val EXTRA_ALLOW_STUN = "io.tcproute.android.VPN_ALLOW_STUN"
        const val EXTRA_ALLOW_QUIC = "io.tcproute.android.VPN_ALLOW_QUIC"
        const val EXTRA_ENABLE_IPV6 = "io.tcproute.android.VPN_ENABLE_IPV6"
        const val STATE_STARTING = "starting"
        const val STATE_RUNNING = "running"
        const val STATE_STOPPED = "stopped"
        const val STATE_ERROR = "error"
        const val LOG_FILE_NAME = "tcproute-vpn.log"
        private const val NOTIFICATION_ID = 7071
        private const val VPN_MTU = 1500
        private const val STARTUP_CONFIRMATION_DELAY_MS = 750L
        private const val PROCESS_MONITOR_INTERVAL_MS = 1_000L
        private const val TUN_ADDRESS = "10.7.0.2"
        private const val TUN_IPV6_ADDRESS = "fd00:7::2"
        private const val TUN2PROXY_RUNNER = "libtun2proxy_runner.so"
        @Volatile private var runtimeState: String? = null

        fun currentState(): String? = runtimeState
    }
}
