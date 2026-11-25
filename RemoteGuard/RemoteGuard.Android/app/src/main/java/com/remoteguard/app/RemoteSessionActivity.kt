package com.remoteguard.app

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.ActivityInfo
import android.graphics.BitmapFactory
import android.os.Bundle
import android.text.InputType
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import org.json.JSONObject
import java.util.concurrent.TimeUnit

class RemoteSessionActivity : AppCompatActivity() {

    private lateinit var store: DeviceTrustStore
    private lateinit var desktopView: RemoteDesktopView
    private lateinit var tvStatusBanner: TextView
    private lateinit var layoutKeyboardBar: LinearLayout
    private lateinit var etTypeText: EditText
    private lateinit var btnModeToggle: Button
    private lateinit var btnScreenSelect: Button
    private lateinit var btnRotate: Button
    private lateinit var btnKeyboard: Button
    private lateinit var btnRightClick: Button

    private var webSocket: WebSocket? = null
    private var lastChallenge: String = ""
    private var mode: String = "auth"
    private var pairingCode: String = ""
    private var initialPassword: String = ""
    private var serverUrl: String = ""
    private var highQuality: Boolean = false
    private var isLandscape: Boolean = true
    private var selectedMonitorIndex: Int = 0
    private val availableMonitors = mutableListOf<Pair<Int, String>>()

    private val client = OkHttpClient.Builder()
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_remote_session)
        enableImmersiveFullScreen()

        store = DeviceTrustStore(this)
        desktopView = findViewById(R.id.remoteDesktopView)
        tvStatusBanner = findViewById(R.id.tvStatusBanner)
        layoutKeyboardBar = findViewById(R.id.layoutKeyboardBar)
        etTypeText = findViewById(R.id.etTypeText)
        btnModeToggle = findViewById(R.id.btnModeToggle)
        btnScreenSelect = findViewById(R.id.btnScreenSelect)
        btnRotate = findViewById(R.id.btnRotate)
        btnKeyboard = findViewById(R.id.btnKeyboard)
        btnRightClick = findViewById(R.id.btnRightClick)

        // Apply saved Yes/No options
        isLandscape = store.defaultLandscape
        applyOrientation()
        desktopView.trackpadMode = store.mouseModeDefault
        desktopView.touchInputEnabled = store.touchEnabled
        applyControlVisibility()

        btnRotate.setOnClickListener {
            isLandscape = !isLandscape
            store.defaultLandscape = isLandscape
            applyOrientation()
        }

        btnRightClick.setOnClickListener {
            desktopView.triggerRightClick()
        }

        findViewById<Button>(R.id.btnZoomIn).setOnClickListener {
            desktopView.zoomIn()
            Toast.makeText(this, "Zoom: ${desktopView.getZoomPercent()}%", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.btnZoomOut).setOnClickListener {
            desktopView.zoomOut()
            Toast.makeText(this, "Zoom: ${desktopView.getZoomPercent()}%", Toast.LENGTH_SHORT).show()
        }

        val topBar = findViewById<View>(R.id.topToolbarScroll)
        val btnShowBar = findViewById<Button>(R.id.btnShowBar)
        findViewById<Button>(R.id.btnHideBar).setOnClickListener {
            topBar.visibility = View.GONE
            btnShowBar.visibility = View.VISIBLE
            enableImmersiveFullScreen()
        }
        btnShowBar.setOnClickListener {
            topBar.visibility = View.VISIBLE
            btnShowBar.visibility = View.GONE
        }

        btnScreenSelect.setOnClickListener {
            sendJson(JSONObject().apply { put("type", "get_monitors") })
            showMonitorPickerDialog()
        }

        mode = intent.getStringExtra("mode") ?: "auth"
        serverUrl = intent.getStringExtra("url") ?: store.serverUrl
        pairingCode = intent.getStringExtra("pairingCode") ?: ""
        initialPassword = intent.getStringExtra("password") ?: ""

        desktopView.mouseListener = object : RemoteDesktopView.MouseListener {
            override fun onMouseEvent(action: String, normX: Double, normY: Double, delta: Int) {
                val json = JSONObject().apply {
                    put("type", "mouse")
                    put("action", action)
                    put("x", normX)
                    put("y", normY)
                    if (delta != 0) put("delta", delta)
                }
                webSocket?.send(json.toString())
            }
        }

        btnKeyboard.setOnClickListener {
            if (!store.keyboardEnabled) {
                Toast.makeText(this, "Keyboard is set to OFF (enable in Options)", Toast.LENGTH_SHORT).show()
                return@setOnClickListener
            }
            layoutKeyboardBar.visibility =
                if (layoutKeyboardBar.visibility == View.VISIBLE) View.GONE else View.VISIBLE
        }

        findViewById<Button>(R.id.btnSendText).setOnClickListener {
            val txt = etTypeText.text.toString()
            if (txt.isNotEmpty()) {
                sendJson(JSONObject().apply {
                    put("type", "text")
                    put("text", txt)
                })
                etTypeText.text.clear()
            }
        }

        findViewById<Button>(R.id.btnSendEnter).setOnClickListener {
            sendSpecialKey("ENTER")
        }

        findViewById<Button>(R.id.btnSendBackspace).setOnClickListener {
            sendSpecialKey("BACKSPACE")
        }

        findViewById<Button>(R.id.btnSpecialKeys).setOnClickListener {
            showSpecialKeysMenu()
        }

        btnModeToggle.setOnClickListener {
            if (!desktopView.trackpadMode) {
                desktopView.trackpadMode = true
                store.mouseModeDefault = true
            } else if (store.touchEnabled) {
                desktopView.trackpadMode = false
                store.mouseModeDefault = false
            } else {
                Toast.makeText(this, "Touch is disabled (Yes/No in Options)", Toast.LENGTH_SHORT).show()
            }
            applyControlVisibility()
        }

        findViewById<Button>(R.id.btnQuality).setOnClickListener {
            highQuality = !highQuality
            sendJson(JSONObject().apply {
                put("type", "quality")
                put("quality", if (highQuality) 75 else 48)
                put("fps", if (highQuality) 18 else 12)
                put("maxWidth", if (highQuality) 1600 else 1080)
            })
            Toast.makeText(this, if (highQuality) "HD Mode" else "Fast Mode", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.btnDisconnect).setOnClickListener {
            finish()
        }

        connectWebSocket()
    }

    private fun connectWebSocket() {
        tvStatusBanner.text = "Connecting to $serverUrl ..."
        ConnectionLogStore.log("WS", "Connecting ($mode) to $serverUrl | VPN=${DeviceTrustStore.isVpnActive(this)}")
        val req = try {
            Request.Builder().url(serverUrl).build()
        } catch (ex: Exception) {
            ConnectionLogStore.log("WS_ERR", "Invalid URL '$serverUrl': ${ex.message}")
            showConnectionErrorDialog("Invalid URL: ${ex.message}")
            return
        }
        webSocket = client.newWebSocket(req, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                ConnectionLogStore.log("WS_OPEN", "WebSocket connected (HTTP ${response.code})")
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                ConnectionLogStore.log("WS_RX", text.take(180))
                runOnUiThread { handleServerJson(text) }
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                val arr = bytes.toByteArray()
                if (arr.size <= 6 || arr[0].toInt() != 0x01) return
                val isLocked = arr[1].toInt() == 1
                val bmp = BitmapFactory.decodeByteArray(arr, 6, arr.size - 6) ?: return
                runOnUiThread {
                    desktopView.updateFrame(bmp)
                    tvStatusBanner.text = if (isLocked) {
                        "PC LOCKED • Tap Keys -> Wake / Ctrl+Alt+Del to Unlock"
                    } else {
                        "Connected • ${store.hostName}"
                    }
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                val httpInfo = if (response != null) " | HTTP ${response.code} ${response.message}" else ""
                val detail = "${t.javaClass.simpleName}: ${t.message}$httpInfo"
                ConnectionLogStore.log("WS_FAIL", detail)
                runOnUiThread {
                    tvStatusBanner.text = "Failed: ${t.message}"
                    showConnectionErrorDialog(detail)
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                ConnectionLogStore.log("WS_CLOSED", "Closed ($code): $reason")
                runOnUiThread {
                    tvStatusBanner.text = "Session closed"
                }
            }
        })
    }

    private fun showConnectionErrorDialog(detail: String) {
        if (isFinishing || isDestroyed) return
        AlertDialog.Builder(this)
            .setTitle("Connection Failed")
            .setMessage("URL: $serverUrl\n\nError: $detail\n\nTip: Check Windows Firewall (Allow Firewall Port), ensure PC Host is running, or use Cloudflare Tunnel URL.")
            .setPositiveButton("Copy Log") { _, _ ->
                val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                cm.setPrimaryClip(ClipData.newPlainText("RemoteGuard Log", ConnectionLogStore.getFullLog()))
                Toast.makeText(this, "Error log copied!", Toast.LENGTH_SHORT).show()
                finish()
            }
            .setNegativeButton("Close") { _, _ -> finish() }
            .show()
    }

    private fun handleServerJson(raw: String) {
        val obj = try { JSONObject(raw) } catch (_: Exception) { return }
        when (obj.optString("type")) {
            "hello" -> {
                lastChallenge = obj.optString("challenge")
                val host = obj.optString("hostName")
                if (host.isNotBlank()) store.hostName = host

                if (mode == "pair") {
                    sendPairRequest()
                } else {
                    sendAuthRequest(password = "")
                }
            }
            "pair_ok" -> {
                val secret = obj.optString("deviceSecret")
                val host = obj.optString("hostName")
                store.deviceSecret = secret
                if (host.isNotBlank()) store.hostName = host
                parseMonitorsFromJson(obj)
                tvStatusBanner.text = "Paired & Connected to $host!"
                Toast.makeText(this, "Device Trusted! Next time only Fingerprint is needed.", Toast.LENGTH_LONG).show()
            }
            "auth_ok" -> {
                val host = obj.optString("hostName")
                if (host.isNotBlank()) store.hostName = host
                parseMonitorsFromJson(obj)
                tvStatusBanner.text = "Connected • $host"
            }
            "monitors" -> {
                parseMonitorsFromJson(obj)
            }
            "auth_pending_pc" -> {
                tvStatusBanner.text = obj.optString("message", "Waiting for PC confirmation...")
            }
            "auth_require_password" -> {
                val msg = obj.optString("message", "IP/VPN changed. Enter Master Password.")
                showConditionalPasswordDialog(msg)
            }
            "auth_require_pair", "auth_error" -> {
                val msg = obj.optString("message", "Authentication failed")
                AlertDialog.Builder(this)
                    .setTitle("RemoteGuard Security")
                    .setMessage(msg)
                    .setPositiveButton("OK") { _, _ -> finish() }
                    .setCancelable(false)
                    .show()
            }
        }
    }

    private fun sendPairRequest() {
        tvStatusBanner.text = "Pairing trusted device..."
        sendJson(JSONObject().apply {
            put("type", "pair")
            put("deviceId", store.deviceId)
            put("deviceName", store.deviceName)
            put("pairingCode", pairingCode)
            put("password", initialPassword)
            put("biometricVerified", true)
        })
    }

    private fun sendAuthRequest(password: String) {
        val proof = store.signChallenge(lastChallenge)
        val isVpn = DeviceTrustStore.isVpnActive(this)
        tvStatusBanner.text = if (isVpn) "Authenticating (VPN detected)..." else "Authenticating..."
        sendJson(JSONObject().apply {
            put("type", "auth")
            put("deviceId", store.deviceId)
            put("deviceName", store.deviceName)
            put("proof", proof)
            put("biometricVerified", true)
            put("isVpn", isVpn)
            if (password.isNotEmpty()) put("password", password)
        })
    }

    private fun showConditionalPasswordDialog(message: String) {
        val input = EditText(this).apply {
            hint = "Master Password"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            setPadding(48, 32, 48, 32)
        }
        AlertDialog.Builder(this)
            .setTitle("Security Verification Required")
            .setMessage(message)
            .setView(input)
            .setCancelable(false)
            .setPositiveButton("Verify") { _, _ ->
                sendAuthRequest(input.text.toString())
            }
            .setNegativeButton("Cancel") { _, _ -> finish() }
            .show()
    }

    private fun parseMonitorsFromJson(obj: JSONObject) {
        val arr = obj.optJSONArray("monitors") ?: return
        availableMonitors.clear()
        for (i in 0 until arr.length()) {
            val m = arr.optJSONObject(i) ?: continue
            availableMonitors.add(Pair(m.optInt("index", i), m.optString("name", "Screen ${i + 1}")))
        }
        selectedMonitorIndex = obj.optInt("selectedMonitor", selectedMonitorIndex)
        updateScreenButtonLabel()
    }

    private fun updateScreenButtonLabel() {
        btnScreenSelect.text = if (selectedMonitorIndex < 0) "All" else "S${selectedMonitorIndex + 1}"
    }

    private fun showMonitorPickerDialog() {
        val items = mutableListOf<Pair<Int, String>>()
        items.add(Pair(-1, "All Screens (Combined / Dual View)"))
        if (availableMonitors.isEmpty()) {
            items.add(Pair(0, "Screen 1 (Primary)"))
            items.add(Pair(1, "Screen 2 (Secondary)"))
        } else {
            items.addAll(availableMonitors)
        }

        val labels = items.map { (idx, name) ->
            if (idx == selectedMonitorIndex) "✓ $name" else name
        }.toTypedArray()

        AlertDialog.Builder(this)
            .setTitle("Select PC Screen / Monitor")
            .setItems(labels) { _, which ->
                val chosen = items[which]
                selectedMonitorIndex = chosen.first
                updateScreenButtonLabel()
                sendJson(JSONObject().apply {
                    put("type", "select_monitor")
                    put("index", selectedMonitorIndex)
                })
                desktopView.resetZoom()
                Toast.makeText(this, "Switched to: ${chosen.second}", Toast.LENGTH_SHORT).show()
            }
            .show()
    }

    private fun enableImmersiveFullScreen() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        val controller = WindowInsetsControllerCompat(window, window.decorView)
        controller.hide(WindowInsetsCompat.Type.systemBars())
        controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }

    private fun applyOrientation() {
        requestedOrientation = if (isLandscape) {
            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        } else {
            ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
        btnRotate.text = if (isLandscape) "Port" else "Land"
    }

    private fun applyControlVisibility() {
        btnModeToggle.text = if (desktopView.trackpadMode) "Mouse" else "Touch"
        btnRightClick.visibility = if (desktopView.trackpadMode) View.VISIBLE else View.GONE
        btnKeyboard.visibility = if (store.keyboardEnabled) View.VISIBLE else View.GONE
        if (!store.keyboardEnabled) layoutKeyboardBar.visibility = View.GONE
    }

    private fun showSpecialKeysMenu() {
        val labels = arrayOf(
            "Toggle Touch Option (${if (store.touchEnabled) "YES" else "NO"})",
            "Toggle Keyboard Option (${if (store.keyboardEnabled) "YES" else "NO"})",
            "Wake Screen (Lock Curtain)",
            "Ctrl + Alt + Del (Unlock Screen)",
            "Windows Key",
            "Alt + Tab",
            "Escape",
            "Tab",
            "Ctrl + C (Copy)",
            "Ctrl + V (Paste)",
            "Ctrl + A (Select All)",
            "Task Manager",
            "Lock PC (Win+L)",
            "Reset Zoom"
        )
        val keys = arrayOf(
            "TOGGLE_TOUCH", "TOGGLE_KEYB",
            "WAKE_SCREEN", "CTRL_ALT_DEL", "WIN", "ALT_TAB",
            "ESC", "TAB", "CTRL_C", "CTRL_V", "CTRL_A", "TASK_MGR", "LOCK", "RESET_ZOOM"
        )
        AlertDialog.Builder(this)
            .setTitle("PC Keys & Controls")
            .setItems(labels) { _, which ->
                when (keys[which]) {
                    "TOGGLE_TOUCH" -> {
                        store.touchEnabled = !store.touchEnabled
                        desktopView.touchInputEnabled = store.touchEnabled
                        if (!store.touchEnabled) desktopView.trackpadMode = true
                        applyControlVisibility()
                        Toast.makeText(this, "Touch Input: ${if (store.touchEnabled) "YES" else "NO"}", Toast.LENGTH_SHORT).show()
                    }
                    "TOGGLE_KEYB" -> {
                        store.keyboardEnabled = !store.keyboardEnabled
                        applyControlVisibility()
                        Toast.makeText(this, "Keyboard: ${if (store.keyboardEnabled) "YES" else "NO"}", Toast.LENGTH_SHORT).show()
                    }
                    "RESET_ZOOM" -> desktopView.resetZoom()
                    else -> sendSpecialKey(keys[which])
                }
            }
            .show()
    }

    private fun sendSpecialKey(key: String) {
        sendJson(JSONObject().apply {
            put("type", "key")
            put("key", key)
        })
    }

    private fun sendJson(obj: JSONObject) {
        webSocket?.send(obj.toString())
    }

    override fun onDestroy() {
        super.onDestroy()
        webSocket?.close(1000, "User disconnected")
        client.dispatcher.executorService.shutdown()
    }
}
