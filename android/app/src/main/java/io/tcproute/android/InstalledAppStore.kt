package io.tcproute.android

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager

object InstalledAppStore {
    fun loadLaunchableApps(context: Context): List<InstalledApp> {
        val packageManager = context.packageManager
        val launcherIntent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)

        return packageManager.queryIntentActivities(launcherIntent, 0)
            .asSequence()
            .map { it.activityInfo.applicationInfo }
            .distinctBy { it.packageName }
            .filter { it.packageName != context.packageName }
            .map { app ->
                InstalledApp(
                    label = app.loadLabel(packageManager).toString(),
                    packageName = app.packageName
                )
            }
            .sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.label })
            .toList()
    }
}
