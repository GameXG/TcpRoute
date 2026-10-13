package io.tcproute.android

import android.content.Context
import android.content.res.Configuration
import android.os.LocaleList
import java.util.Locale

object LanguageManager {
    const val SYSTEM = "system"
    const val ENGLISH = "en"
    const val SIMPLIFIED_CHINESE = "zh-CN"
    const val TRADITIONAL_CHINESE = "zh-TW"

    val choices = listOf(SYSTEM, ENGLISH, SIMPLIFIED_CHINESE, TRADITIONAL_CHINESE)

    fun selection(context: Context): String =
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .getString(KEY_LANGUAGE, SYSTEM)
            ?.takeIf { it in choices }
            ?: SYSTEM

    fun save(context: Context, choice: String) {
        require(choice in choices)
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_LANGUAGE, choice)
            .apply()
    }

    fun wrap(context: Context): Context {
        val locale = Locale.forLanguageTag(resolveLanguageTag(context))
        val configuration = Configuration(context.resources.configuration).apply {
            setLocale(locale)
            setLocales(LocaleList(locale))
            setLayoutDirection(locale)
        }
        return context.createConfigurationContext(configuration)
    }

    fun selectionLabelResource(choice: String): Int = when (choice) {
        ENGLISH -> R.string.language_english
        SIMPLIFIED_CHINESE -> R.string.language_simplified_chinese
        TRADITIONAL_CHINESE -> R.string.language_traditional_chinese
        else -> R.string.language_system
    }

    private fun resolveLanguageTag(context: Context): String {
        val choice = selection(context)
        if (choice != SYSTEM) return choice

        val locales = context.resources.configuration.locales
        for (index in 0 until locales.size()) {
            val locale = locales[index]
            when (locale.language.lowercase(Locale.ROOT)) {
                "zh" -> return if (usesTraditionalChinese(locale)) {
                    TRADITIONAL_CHINESE
                } else {
                    SIMPLIFIED_CHINESE
                }
                "en" -> return ENGLISH
            }
        }
        return ENGLISH
    }

    private fun usesTraditionalChinese(locale: Locale): Boolean {
        val script = locale.script.lowercase(Locale.ROOT)
        val region = locale.country.uppercase(Locale.ROOT)
        return script == "hant" || region in setOf("TW", "HK", "MO")
    }

    private const val PREFERENCES = "language-settings"
    private const val KEY_LANGUAGE = "language"
}
