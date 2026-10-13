package io.tcproute.android

import android.content.Context
import java.io.File
import java.security.MessageDigest

object TcpRouteBinaryInstaller {
    fun install(context: Context): File {
        installGeoData(context)
        val nativeLibrary = File(context.applicationInfo.nativeLibraryDir, "libtcproute.so")
        if (nativeLibrary.canExecute()) {
            return nativeLibrary
        }

        val abi = android.os.Build.SUPPORTED_ABIS.firstOrNull() ?: "arm64-v8a"
        val assetPath = "bin/$abi/TcpRoute2"
        val destination = File(context.filesDir, "TcpRoute2")

        if (!destination.exists()) {
            context.assets.open(assetPath).use { input ->
                destination.outputStream().use { output ->
                    input.copyTo(output)
                }
            }
            destination.setExecutable(true, true)
        }

        return destination
    }

    @Synchronized
    fun installGeoData(context: Context) {
        installBundledGeoData(context, "geosite.dat")
        installBundledGeoData(context, "geoip.dat")
    }

    private fun installBundledGeoData(context: Context, name: String) {
        val bundledHash = context.assets.open("$name.sha256").bufferedReader().use { it.readText().trim() }
        val destination = File(context.filesDir, name)
        val marker = File(context.filesDir, "$name.sha256")
        val installedHash = marker.readTextOrEmpty()
        if (destination.isFile && installedHash.matches(SHA256) && destination.sha256() == installedHash) return

        val temporary = File(context.filesDir, "$name.tmp")
        context.assets.open(name).use { input ->
            temporary.outputStream().use { output -> input.copyTo(output) }
        }
        check(temporary.sha256() == bundledHash) { "Bundled $name checksum mismatch" }
        if (destination.exists() && !destination.delete()) {
            temporary.delete()
            error("Unable to replace $name")
        }
        if (!temporary.renameTo(destination)) {
            temporary.copyTo(destination, overwrite = true)
            temporary.delete()
        }
        marker.writeText(bundledHash)
    }

    private fun File.readTextOrEmpty(): String =
        if (isFile) runCatching { readText().trim() }.getOrDefault("") else ""

    private fun File.sha256(): String {
        val digest = MessageDigest.getInstance("SHA-256")
        inputStream().use { input ->
            val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                digest.update(buffer, 0, count)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private val SHA256 = Regex("^[0-9a-f]{64}$")
}
