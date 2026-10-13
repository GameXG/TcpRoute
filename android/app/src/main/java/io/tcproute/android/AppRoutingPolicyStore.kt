package io.tcproute.android

import android.content.Context

enum class AppRoutingMode {
    ALL,
    ALLOWLIST,
    BLOCKLIST
}

data class AppRoutingPolicy(
    val mode: AppRoutingMode,
    val packages: Set<String>
)

object AppRoutingPolicyStore {
    private const val PREFERENCES = "app-routing-policy"
    private const val KEY_MODE = "mode"
    private const val KEY_PACKAGES = "packages"

    fun load(context: Context): AppRoutingPolicy {
        val preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        val mode = preferences.getString(KEY_MODE, AppRoutingMode.ALL.name)
            ?.let { runCatching { AppRoutingMode.valueOf(it) }.getOrNull() }
            ?: AppRoutingMode.ALL
        val packages = preferences.getStringSet(KEY_PACKAGES, emptySet()).orEmpty()
            .filterNot { it == context.packageName }
            .toSet()

        return AppRoutingPolicy(mode, packages)
    }

    fun save(context: Context, policy: AppRoutingPolicy) {
        val packages = policy.packages
            .filterNot { it == context.packageName }
            .toSet()

        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_MODE, policy.mode.name)
            .putStringSet(KEY_PACKAGES, packages)
            .apply()
    }
}
