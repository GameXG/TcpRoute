package io.tcproute.android

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.view.View
import android.view.WindowInsets
import android.widget.ScrollView
import android.widget.TextView

class RequestLogActivity : Activity() {
    private lateinit var pageRoot: View
    private lateinit var logPresenter: RequestLogPresenter

    override fun attachBaseContext(newBase: android.content.Context) {
        super.attachBaseContext(LanguageManager.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_request_log)
        pageRoot = findViewById(R.id.logPageRoot)
        logPresenter = RequestLogPresenter(
            this,
            findViewById<ScrollView>(R.id.logScroll),
            findViewById<TextView>(R.id.logText),
            findViewById<TextView>(R.id.logStatus)
        )
        findViewById<View>(R.id.clearLogButton).setOnClickListener {
            showClearRequestLogDialog(this, logPresenter)
        }
        configureSystemBars()
    }

    override fun onStart() {
        super.onStart()
        logPresenter.start()
    }

    override fun onStop() {
        logPresenter.stop()
        super.onStop()
    }

    override fun onDestroy() {
        logPresenter.destroy()
        super.onDestroy()
    }

    private fun configureSystemBars() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return
        window.setDecorFitsSystemWindows(false)
        val left = pageRoot.paddingLeft
        val top = pageRoot.paddingTop
        val right = pageRoot.paddingRight
        val bottom = pageRoot.paddingBottom
        pageRoot.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars())
            view.setPadding(left + bars.left, top + bars.top, right + bars.right, bottom + bars.bottom)
            insets
        }
    }

}
