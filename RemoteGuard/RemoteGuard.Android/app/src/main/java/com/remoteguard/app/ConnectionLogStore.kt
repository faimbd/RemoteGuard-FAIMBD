package com.remoteguard.app

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList

object ConnectionLogStore {
    private val entries = CopyOnWriteArrayList<String>()
    private val fmt = SimpleDateFormat("HH:mm:ss", Locale.US)

    @Volatile
    var onChanged: (() -> Unit)? = null

    fun log(tag: String, message: String) {
        val ts = fmt.format(Date())
        entries.add("[$ts] [$tag] $message")
        while (entries.size > 150) {
            entries.removeAt(0)
        }
        onChanged?.invoke()
    }

    fun getFullLog(): String {
        if (entries.isEmpty()) return "No logs yet. Tap 'Test Connection' or 'Connect' to see details."
        return entries.joinToString("\n")
    }

    fun clear() {
        entries.clear()
        onChanged?.invoke()
    }
}
