package com.remoteguard.app

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.util.Base64
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import java.net.NetworkInterface
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

class DeviceTrustStore(context: Context) {
    private val prefs = try {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            "remoteguard_secure_prefs",
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    } catch (_: Exception) {
        context.getSharedPreferences("remoteguard_fallback_prefs", Context.MODE_PRIVATE)
    }

    val deviceId: String
        get() {
            var id = prefs.getString("device_id", null)
            if (id.isNullOrBlank()) {
                id = "AND-" + UUID.randomUUID().toString().substring(0, 12).uppercase()
                prefs.edit().putString("device_id", id).apply()
            }
            return id
        }

    val deviceName: String
        get() = "${Build.MANUFACTURER} ${Build.MODEL}".trim()

    var serverUrl: String
        get() = prefs.getString("server_url", "") ?: ""
        set(value) = prefs.edit().putString("server_url", value.trim()).apply()

    var deviceSecret: String
        get() = prefs.getString("device_secret", "") ?: ""
        set(value) = prefs.edit().putString("device_secret", value).apply()

    var hostName: String
        get() = prefs.getString("host_name", "") ?: ""
        set(value) = prefs.edit().putString("host_name", value).apply()

    var defaultLandscape: Boolean
        get() = prefs.getBoolean("default_landscape", true)
        set(value) = prefs.edit().putBoolean("default_landscape", value).apply()

    var touchEnabled: Boolean
        get() = prefs.getBoolean("touch_enabled", true)
        set(value) = prefs.edit().putBoolean("touch_enabled", value).apply()

    var keyboardEnabled: Boolean
        get() = prefs.getBoolean("keyboard_enabled", true)
        set(value) = prefs.edit().putBoolean("keyboard_enabled", value).apply()

    var mouseModeDefault: Boolean
        get() = prefs.getBoolean("mouse_mode_default", true)
        set(value) = prefs.edit().putBoolean("mouse_mode_default", value).apply()

    val isPaired: Boolean
        get() = deviceSecret.isNotBlank()

    fun clearTrust() {
        prefs.edit().remove("device_secret").remove("host_name").apply()
    }

    fun signChallenge(challenge: String): String {
        if (deviceSecret.isBlank()) return ""
        return try {
            val keyBytes = Base64.decode(deviceSecret, Base64.NO_WRAP)
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(keyBytes, "HmacSHA256"))
            val sig = mac.doFinal(challenge.toByteArray(Charsets.UTF_8))
            Base64.encodeToString(sig, Base64.NO_WRAP)
        } catch (_: Exception) {
            ""
        }
    }

    companion object {
        fun isVpnActive(context: Context): Boolean {
            try {
                val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
                val active = cm.activeNetwork
                val caps = cm.getNetworkCapabilities(active)
                if (caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
                    return true
                }
                // Check all networks as fallback
                for (net in cm.allNetworks) {
                    val c = cm.getNetworkCapabilities(net)
                    if (c != null && c.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return true
                }
            } catch (_: Exception) { }

            try {
                val ifaces = NetworkInterface.getNetworkInterfaces()
                while (ifaces.hasMoreElements()) {
                    val ni = ifaces.nextElement()
                    if (ni.isUp && (ni.name.startsWith("tun") || ni.name.startsWith("ppp") || ni.name.startsWith("wg"))) {
                        return true
                    }
                }
            } catch (_: Exception) { }

            return false
        }
    }
}
