package io.tcproute.android

import android.content.Context

data class VpnFeaturePolicy(
    val allowStun: Boolean = false,
    val allowQuic: Boolean = false,
    val enableIpv6: Boolean = false
)

object VpnFeaturePolicyStore {
    private const val PREFERENCES = "vpn-feature-policy"
    private const val KEY_ALLOW_STUN = "allow-stun"
    private const val KEY_ALLOW_QUIC = "allow-quic"
    private const val KEY_ENABLE_IPV6 = "enable-ipv6"

    fun load(context: Context): VpnFeaturePolicy {
        val preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        return VpnFeaturePolicy(
            allowStun = preferences.getBoolean(KEY_ALLOW_STUN, false),
            allowQuic = preferences.getBoolean(KEY_ALLOW_QUIC, false),
            enableIpv6 = preferences.getBoolean(KEY_ENABLE_IPV6, false)
        )
    }

    fun save(context: Context, policy: VpnFeaturePolicy) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(KEY_ALLOW_STUN, policy.allowStun)
            .putBoolean(KEY_ALLOW_QUIC, policy.allowQuic)
            .putBoolean(KEY_ENABLE_IPV6, policy.enableIpv6)
            .apply()
    }
}
