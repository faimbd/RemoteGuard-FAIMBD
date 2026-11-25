package com.remoteguard.app

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import kotlin.math.max
import kotlin.math.min

class RemoteDesktopView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    interface MouseListener {
        fun onMouseEvent(action: String, normX: Double, normY: Double, delta: Int = 0)
    }

    var mouseListener: MouseListener? = null
    var trackpadMode: Boolean = true
    var touchInputEnabled: Boolean = true

    @Volatile
    private var currentBitmap: Bitmap? = null
    private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val cursorFillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.rgb(245, 197, 66)
        style = Paint.Style.FILL
    }
    private val cursorStrokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.BLACK
        style = Paint.Style.STROKE
        strokeWidth = 3f
    }
    private val destRect = RectF()

    private var zoom = 1f
    private var panX = 0f
    private var panY = 0f

    // Virtual cursor for trackpad mode
    private var cursorNormX = 0.5
    private var cursorNormY = 0.5
    private var lastTouchX = 0f
    private var lastTouchY = 0f
    private var isTwoFingerScrolling = false
    private var lastScrollY = 0f

    private val scaleDetector = ScaleGestureDetector(context, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
        override fun onScale(detector: ScaleGestureDetector): Boolean {
            zoom = (zoom * detector.scaleFactor).coerceIn(1f, 4f)
            if (zoom <= 1.02f) {
                zoom = 1f
                panX = 0f
                panY = 0f
            }
            invalidate()
            return true
        }
    })

    private val gestureDetector = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
        override fun onSingleTapConfirmed(e: MotionEvent): Boolean {
            if (!touchInputEnabled && !trackpadMode) return true
            val (nx, ny) = if (trackpadMode) Pair(cursorNormX, cursorNormY) else screenToNorm(e.x, e.y)
            mouseListener?.onMouseEvent("click", nx, ny)
            return true
        }

        override fun onDoubleTap(e: MotionEvent): Boolean {
            if (!touchInputEnabled && !trackpadMode) return true
            val (nx, ny) = if (trackpadMode) Pair(cursorNormX, cursorNormY) else screenToNorm(e.x, e.y)
            mouseListener?.onMouseEvent("dblclick", nx, ny)
            return true
        }

        override fun onLongPress(e: MotionEvent) {
            if (!touchInputEnabled && !trackpadMode) return
            val (nx, ny) = if (trackpadMode) Pair(cursorNormX, cursorNormY) else screenToNorm(e.x, e.y)
            mouseListener?.onMouseEvent("rclick", nx, ny)
        }
    })

    fun triggerLeftClick() {
        mouseListener?.onMouseEvent("click", cursorNormX, cursorNormY)
    }

    fun triggerRightClick() {
        mouseListener?.onMouseEvent("rclick", cursorNormX, cursorNormY)
    }

    fun updateFrame(bitmap: Bitmap) {
        val old = currentBitmap
        currentBitmap = bitmap
        postInvalidate()
        if (old != null && old != bitmap && !old.isRecycled) {
            // Let GC reclaim safely after draw
        }
    }

    fun resetZoom() {
        zoom = 1f
        panX = 0f
        panY = 0f
        invalidate()
    }

    fun zoomIn() {
        zoom = (zoom + 0.5f).coerceIn(1f, 4f)
        invalidate()
    }

    fun zoomOut() {
        zoom = (zoom - 0.5f).coerceIn(1f, 4f)
        if (zoom <= 1.02f) {
            zoom = 1f
            panX = 0f
            panY = 0f
        }
        invalidate()
    }

    fun getZoomPercent(): Int = (zoom * 100).toInt()

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        canvas.drawColor(Color.rgb(12, 16, 23))

        val bmp = currentBitmap ?: return
        if (bmp.isRecycled) return

        val vw = width.toFloat()
        val vh = height.toFloat()
        val bw = bmp.width.toFloat()
        val bh = bmp.height.toFloat()
        if (vw <= 0 || vh <= 0 || bw <= 0 || bh <= 0) return

        val baseScale = min(vw / bw, vh / bh)
        val drawW = bw * baseScale * zoom
        val drawH = bh * baseScale * zoom

        val maxPanX = max(0f, (drawW - vw) / 2f)
        val maxPanY = max(0f, (drawH - vh) / 2f)
        panX = panX.coerceIn(-maxPanX, maxPanX)
        panY = panY.coerceIn(-maxPanY, maxPanY)

        val left = (vw - drawW) / 2f + panX
        val top = (vh - drawH) / 2f + panY
        destRect.set(left, top, left + drawW, top + drawH)

        canvas.drawBitmap(bmp, null, destRect, paint)

        // Draw golden mouse cursor indicator in Mouse (Trackpad) mode
        if (trackpadMode && destRect.width() > 0f && destRect.height() > 0f) {
            val cx = destRect.left + (cursorNormX.toFloat() * destRect.width())
            val cy = destRect.top + (cursorNormY.toFloat() * destRect.height())
            canvas.drawCircle(cx, cy, 9f, cursorFillPaint)
            canvas.drawCircle(cx, cy, 9f, cursorStrokePaint)
        }
    }

    private fun screenToNorm(x: Float, y: Float): Pair<Double, Double> {
        if (destRect.width() <= 0f || destRect.height() <= 0f) return Pair(0.5, 0.5)
        val nx = ((x - destRect.left) / destRect.width()).toDouble().coerceIn(0.0, 1.0)
        val ny = ((y - destRect.top) / destRect.height()).toDouble().coerceIn(0.0, 1.0)
        cursorNormX = nx
        cursorNormY = ny
        return Pair(nx, ny)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        scaleDetector.onTouchEvent(event)
        gestureDetector.onTouchEvent(event)

        if (event.pointerCount == 2) {
            when (event.actionMasked) {
                MotionEvent.ACTION_POINTER_DOWN -> {
                    isTwoFingerScrolling = true
                    lastScrollY = (event.getY(0) + event.getY(1)) / 2f
                }
                MotionEvent.ACTION_MOVE -> {
                    if (isTwoFingerScrolling && !scaleDetector.isInProgress) {
                        val avgY = (event.getY(0) + event.getY(1)) / 2f
                        val dy = avgY - lastScrollY
                        if (kotlin.math.abs(dy) > 12f) {
                            val wheelDelta = if (dy > 0) -120 else 120
                            mouseListener?.onMouseEvent("wheel", cursorNormX, cursorNormY, wheelDelta)
                            lastScrollY = avgY
                        }
                    }
                }
                MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_UP -> {
                    isTwoFingerScrolling = false
                }
            }
            return true
        }

        if (scaleDetector.isInProgress || isTwoFingerScrolling) return true

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                lastTouchX = event.x
                lastTouchY = event.y
            }
            MotionEvent.ACTION_MOVE -> {
                val dx = event.x - lastTouchX
                val dy = event.y - lastTouchY
                lastTouchX = event.x
                lastTouchY = event.y

                if (zoom > 1.05f && !trackpadMode) {
                    panX += dx
                    panY += dy
                    invalidate()
                } else if (trackpadMode) {
                    val sensitivity = 1.4 / max(width, 1)
                    cursorNormX = (cursorNormX + dx * sensitivity).coerceIn(0.0, 1.0)
                    cursorNormY = (cursorNormY + dy * sensitivity).coerceIn(0.0, 1.0)
                    mouseListener?.onMouseEvent("move", cursorNormX, cursorNormY)
                    invalidate()
                } else if (touchInputEnabled) {
                    val (nx, ny) = screenToNorm(event.x, event.y)
                    mouseListener?.onMouseEvent("move", nx, ny)
                }
            }
        }
        return true
    }
}
