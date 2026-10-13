package io.tcproute.android

import android.app.Activity
import android.app.AlertDialog
import android.widget.Toast

internal fun showClearRequestLogDialog(activity: Activity, presenter: RequestLogPresenter) {
    AlertDialog.Builder(activity)
        .setTitle(R.string.clear_request_log)
        .setMessage(R.string.clear_request_log_confirm)
        .setPositiveButton(R.string.clear_request_log) { _, _ ->
            presenter.clear { succeeded ->
                Toast.makeText(
                    activity,
                    if (succeeded) R.string.request_log_cleared else R.string.request_log_clear_failed,
                    Toast.LENGTH_SHORT
                ).show()
            }
        }
        .setNegativeButton(android.R.string.cancel, null)
        .show()
}
