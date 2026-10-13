package io.tcproute.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context

object TcpRouteNotification {
    private const val CHANNEL_ID = "tcproute"

    fun build(context: Context, textResource: Int): Notification {
        val localizedContext = LanguageManager.wrap(context)
        ensureChannel(localizedContext)
        return Notification.Builder(localizedContext, CHANNEL_ID)
            .setContentTitle("TcpRoute2")
            .setContentText(localizedContext.getString(textResource))
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .setOngoing(true)
            .build()
    }

    private fun ensureChannel(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        val channel = NotificationChannel(
            CHANNEL_ID,
            context.getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW
        )
        manager.createNotificationChannel(channel)
    }
}
