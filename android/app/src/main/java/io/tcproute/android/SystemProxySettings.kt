package io.tcproute.android

import android.content.Context

object SystemProxySettings {
    private const val STORE = "tcproute-system-proxy"

    fun enabled(context: Context): Boolean = context.getSharedPreferences(STORE, Context.MODE_PRIVATE).getBoolean("enabled", false)
    fun setEnabled(context: Context, value: Boolean) {
        check(context.getSharedPreferences(STORE, Context.MODE_PRIVATE).edit().putBoolean("enabled", value).commit())
    }
}
