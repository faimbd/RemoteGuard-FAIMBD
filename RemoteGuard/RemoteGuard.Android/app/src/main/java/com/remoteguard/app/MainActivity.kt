package com.remoteguard.app

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.widget.SwitchCompat
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import okhttp3.Call
import okhttp3.Callback
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

class MainActivity : AppCompatActivity() {

    private lateinit var store: DeviceTrustStore
    private lateinit var tvTrustStatus: TextView
    private lateinit var tvDeviceId: TextView
    private lateinit var tvVpnStatus: TextView
    private lateinit var etServerUrl: EditText
    private lateinit var etPairingCode: EditText
    private lateinit var etMasterPassword: EditText
    private lateinit var btnConnect: Button
    private lateinit var btnScanQr: Button
    private lateinit var btnTestConn: Button
    private lateinit var btnPair: Button
    private lateinit var btnUnpair: Button
    private lateinit var tvDiagLog: TextView
    private lateinit var btnCopyLog: Button
    private lateinit var btnClearLog: Button
    private lateinit var layoutPairingSection: LinearLayout
    private lateinit var swLandscape: SwitchCompat
    private lateinit var swMouseDefault: SwitchCompat
    private lateinit var swTouchEnabled: SwitchCompat
    private lateinit var swKeyboardEnabled: SwitchCompat

    private val testHttpClient = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(8, TimeUnit.SECONDS)
        .build()

    private val qrLauncher = registerForActivityResult(ScanContract()) { result ->
        val raw = result.contents
        if (raw.isNullOrBlank()) {
            ConnectionLogStore.log("QR", "QR scan cancelled.")
            return@registerForActivityResult
        }
        ConnectionLogStore.log("QR", "Scanned payload: $raw")
        try {
            val obj = JSONObject(raw)
            val scannedUrl = obj.optString("url", "").trim()
            val scannedCode = obj.optString("code", "").trim()
            val scannedHost = obj.optString("host", "").trim()

            if (scannedUrl.isNotBlank()) {
                val norm = normalizeUrl(scannedUrl)
                etServerUrl.setText(norm)
                store.serverUrl = norm
            }
            if (scannedCode.isNotBlank()) {
                etPairingCode.setText(scannedCode)
            }
            if (scannedHost.isNotBlank()) {
                store.hostName = scannedHost
            }
            Toast.makeText(this, "QR Scanned! URL & Code filled.", Toast.LENGTH_SHORT).show()
            ConnectionLogStore.log("QR", "Parsed URL=$scannedUrl Code=$scannedCode Host=$scannedHost")
        } catch (_: Exception) {
            // If user scanned a plain URL QR
            val norm = normalizeUrl(raw)
            etServerUrl.setText(norm)
            store.serverUrl = norm
            Toast.makeText(this, "URL filled from QR.", Toast.LENGTH_SHORT).show()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        store = DeviceTrustStore(this)

        tvTrustStatus = findViewById(R.id.tvTrustStatus)
        tvDeviceId = findViewById(R.id.tvDeviceId)
        tvVpnStatus = findViewById(R.id.tvVpnStatus)
        etServerUrl = findViewById(R.id.etServerUrl)
        etPairingCode = findViewById(R.id.etPairingCode)
        etMasterPassword = findViewById(R.id.etMasterPassword)
        btnConnect = findViewById(R.id.btnConnect)
        btnScanQr = findViewById(R.id.btnScanQr)
        btnTestConn = findViewById(R.id.btnTestConn)
        btnPair = findViewById(R.id.btnPair)
        btnUnpair = findViewById(R.id.btnUnpair)
        tvDiagLog = findViewById(R.id.tvDiagLog)
        btnCopyLog = findViewById(R.id.btnCopyLog)
        btnClearLog = findViewById(R.id.btnClearLog)
        layoutPairingSection = findViewById(R.id.layoutPairingSection)
        swLandscape = findViewById(R.id.swLandscape)
        swMouseDefault = findViewById(R.id.swMouseDefault)
        swTouchEnabled = findViewById(R.id.swTouchEnabled)
        swKeyboardEnabled = findViewById(R.id.swKeyboardEnabled)

        swLandscape.isChecked = store.defaultLandscape
        swMouseDefault.isChecked = store.mouseModeDefault
        swTouchEnabled.isChecked = store.touchEnabled
        swKeyboardEnabled.isChecked = store.keyboardEnabled

        swLandscape.setOnCheckedChangeListener { _, isChecked -> store.defaultLandscape = isChecked }
        swMouseDefault.setOnCheckedChangeListener { _, isChecked -> store.mouseModeDefault = isChecked }
        swTouchEnabled.setOnCheckedChangeListener { _, isChecked -> store.touchEnabled = isChecked }
        swKeyboardEnabled.setOnCheckedChangeListener { _, isChecked -> store.keyboardEnabled = isChecked }

        etServerUrl.setText(store.serverUrl)

        btnScanQr.setOnClickListener {
            val opts = ScanOptions().apply {
                setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                setPrompt("Scan the QR Code on RemoteGuard Windows Host")
                setBeepEnabled(true)
                setOrientationLocked(false)
            }
            qrLauncher.launch(opts)
        }

        btnTestConn.setOnClickListener {
            val wsUrl = normalizeUrl(etServerUrl.text.toString())
            if (wsUrl.isBlank()) {
                Toast.makeText(this, "Enter or scan Server URL first", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }
            store.serverUrl = wsUrl
            etServerUrl.setText(wsUrl)
            runHealthCheck(wsUrl)
        }

        btnConnect.setOnClickListener {
            val url = normalizeUrl(etServerUrl.text.toString())
            if (url.isBlank()) {
                Toast.makeText(this, "Enter PC WebSocket URL first", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }
            if (!store.isPaired) {
                Toast.makeText(this, "Pair this phone first using 6-digit code & Master Password", Toast.LENGTH_LONG).show()
                return@setOnClickListener
            }
            store.serverUrl = url
            etServerUrl.setText(url)
            authenticateWithBiometric("Verify Fingerprint to Connect") {
                val intent = Intent(this, RemoteSessionActivity::class.java).apply {
                    putExtra("mode", "auth")
                    putExtra("url", url)
                }
                startActivity(intent)
            }
        }

        btnPair.setOnClickListener {
            val url = normalizeUrl(etServerUrl.text.toString())
            val code = etPairingCode.text.toString().trim()
            val pwd = etMasterPassword.text.toString()

            if (url.isBlank() || code.length != 6 || pwd.isBlank()) {
                Toast.makeText(this, "Enter Server URL, 6-digit Pairing Code, and Master Password", Toast.LENGTH_LONG).show()
                return@setOnClickListener
            }
            store.serverUrl = url
            etServerUrl.setText(url)
            authenticateWithBiometric("Verify Fingerprint to Trust This Phone") {
                val intent = Intent(this, RemoteSessionActivity::class.java).apply {
                    putExtra("mode", "pair")
                    putExtra("url", url)
                    putExtra("pairingCode", code)
                    putExtra("password", pwd)
                }
                startActivity(intent)
            }
        }

        btnUnpair.setOnClickListener {
            store.clearTrust()
            ConnectionLogStore.log("TRUST", "Device trust cleared by user.")
            refreshStatus()
            Toast.makeText(this, "Device Trust cleared. Pair again to reconnect.", Toast.LENGTH_SHORT).show()
        }

        btnCopyLog.setOnClickListener {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("RemoteGuard Log", ConnectionLogStore.getFullLog()))
            Toast.makeText(this, "Connection log copied! You can paste & share it.", Toast.LENGTH_SHORT).show()
        }

        btnClearLog.setOnClickListener {
            ConnectionLogStore.clear()
            tvDiagLog.text = ConnectionLogStore.getFullLog()
        }
    }

    override fun onResume() {
        super.onResume()
        ConnectionLogStore.onChanged = {
            runOnUiThread { tvDiagLog.text = ConnectionLogStore.getFullLog() }
        }
        refreshStatus()
        tvDiagLog.text = ConnectionLogStore.getFullLog()
    }

    override fun onPause() {
        super.onPause()
        ConnectionLogStore.onChanged = null
    }

    private fun runHealthCheck(wsUrl: String) {
        val httpUrl = wsUrl
            .replaceFirst("wss://", "https://")
            .replaceFirst("ws://", "http://")
            .removeSuffix("/ws") + "/"

        ConnectionLogStore.log("TEST", "Testing PC reachability: $httpUrl (VPN=${DeviceTrustStore.isVpnActive(this)})")
        val req = Request.Builder().url(httpUrl).build()
        testHttpClient.newCall(req).enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                ConnectionLogStore.log("TEST_FAIL", "${e.javaClass.simpleName}: ${e.message}")
                runOnUiThread {
                    Toast.makeText(this@MainActivity, "Reachability failed: ${e.message}", Toast.LENGTH_LONG).show()
                }
            }

            override fun onResponse(call: Call, response: Response) {
                val code = response.code
                val body = try { response.body?.string() ?: "" } catch (_: Exception) { "" }
                response.close()
                ConnectionLogStore.log("TEST_OK", "HTTP $code | $body")
                runOnUiThread {
                    Toast.makeText(this@MainActivity, "PC Online! HTTP $code", Toast.LENGTH_SHORT).show()
                }
            }
        })
    }

    private fun refreshStatus() {
        tvDeviceId.text = "Device ID: ${store.deviceId} (${store.deviceName})"

        val vpn = DeviceTrustStore.isVpnActive(this)
        tvVpnStatus.text = if (vpn) {
            "Network: VPN Detected (Master Password will be required)"
        } else {
            "Network: Direct Connection (No VPN)"
        }

        if (store.isPaired) {
            tvTrustStatus.text = "TRUSTED DEVICE • Paired with ${store.hostName.ifBlank { "Windows PC" }}"
            btnConnect.isEnabled = true
            btnUnpair.visibility = View.VISIBLE
            layoutPairingSection.visibility = View.VISIBLE
        } else {
            tvTrustStatus.text = "NOT PAIRED YET • Scan QR or Pair Below"
            btnConnect.isEnabled = false
            btnUnpair.visibility = View.GONE
            layoutPairingSection.visibility = View.VISIBLE
        }
    }

    private fun normalizeUrl(raw: String): String {
        var u = raw.trim().replace(" ", "").replace("\n", "")
        if (u.isEmpty()) return ""
        if (u.startsWith("https://", ignoreCase = true)) u = "wss://" + u.substring(8)
        if (u.startsWith("http://", ignoreCase = true)) u = "ws://" + u.substring(7)
        if (!u.startsWith("ws://", ignoreCase = true) && !u.startsWith("wss://", ignoreCase = true)) {
            u = if (u.contains("trycloudflare.com", ignoreCase = true)) "wss://$u" else "ws://$u"
        }
        // If bare IP without port (e.g. ws://192.168.1.5), auto-append :9443
        val withoutScheme = u.substringAfter("://")
        val hostPart = withoutScheme.substringBefore("/")
        val isIp = hostPart.matches(Regex("^\\d+\\.\\d+\\.\\d+\\.\\d+$"))
        if (isIp) {
            u = u.replaceFirst(hostPart, "$hostPart:9443")
        }
        if (!u.endsWith("/ws", ignoreCase = true)) {
            u = u.trimEnd('/') + "/ws"
        }
        return u
    }

    private fun authenticateWithBiometric(subtitle: String, onSuccess: () -> Unit) {
        val bm = BiometricManager.from(this)
        val authenticators = BiometricManager.Authenticators.BIOMETRIC_STRONG or
                BiometricManager.Authenticators.BIOMETRIC_WEAK or
                BiometricManager.Authenticators.DEVICE_CREDENTIAL

        if (bm.canAuthenticate(authenticators) != BiometricManager.BIOMETRIC_SUCCESS) {
            Toast.makeText(this, "Set up Fingerprint / Screen Lock on your Android phone for RemoteGuard security.", Toast.LENGTH_LONG).show()
            return
        }

        val executor = ContextCompat.getMainExecutor(this)
        val prompt = BiometricPrompt(this, executor, object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                super.onAuthenticationSucceeded(result)
                onSuccess()
            }

            override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                super.onAuthenticationError(errorCode, errString)
                ConnectionLogStore.log("BIO", "Biometric error ($errorCode): $errString")
                Toast.makeText(this@MainActivity, "Fingerprint cancelled: $errString", Toast.LENGTH_SHORT).show()
            }
        })

        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle("RemoteGuard Security")
            .setSubtitle(subtitle)
            .setAllowedAuthenticators(authenticators)
            .build()

        prompt.authenticate(info)
    }
}
