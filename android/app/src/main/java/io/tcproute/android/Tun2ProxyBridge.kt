package io.tcproute.android

object Tun2ProxyBridge {
    init {
        System.loadLibrary("tcproute_bridge")
    }

    external fun start(runnerPath: String, tunFd: Int, socksPort: Int, mtu: Int): Int
    external fun stop(pid: Int): Int
    external fun poll(pid: Int): Int
    external fun probe(): Int
}
