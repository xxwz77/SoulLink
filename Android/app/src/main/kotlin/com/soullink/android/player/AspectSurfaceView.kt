package com.soullink.android.player

import android.content.Context
import android.graphics.Rect
import android.view.SurfaceView

/**
 * Aspect-preserving SurfaceView (aligned with Moonlight StreamView.onMeasure).
 * desiredAspectRatio > 0: shrink to video ratio (letterbox via container background).
 * = 0: stretch to fill (user opt-in, Moonlight DEFAULT_STRETCH=false).
 */
class AspectSurfaceView(context: Context) : SurfaceView(context) {

    var desiredAspectRatio: Double = 0.0
        set(value) {
            field = value
            requestLayout()
        }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        if (desiredAspectRatio <= 0.0) {
            super.onMeasure(widthMeasureSpec, heightMeasureSpec)
            android.util.Log.i("SoulLinkLayout", "onMeasure 拉伸模式 wanted=" + MeasureSpec.getSize(widthMeasureSpec) + "x" + MeasureSpec.getSize(heightMeasureSpec))
            return
        }
        val widthSize = MeasureSpec.getSize(widthMeasureSpec)
        val heightSize = MeasureSpec.getSize(heightMeasureSpec)
        val dw: Int
        val dh: Int
        if (widthSize > heightSize * desiredAspectRatio) {
            dh = heightSize
            dw = (dh * desiredAspectRatio).toInt()
        } else {
            dw = widthSize
            dh = (dw / desiredAspectRatio).toInt()
        }
        // ★ P0-2 诊断日志（装机一眼看是否生效）：wanted=约束 measured=实际收缩 ratio=比例
        android.util.Log.i("SoulLinkLayout", "onMeasure 等比 wanted=" + widthSize + "x" + heightSize + " measured=" + dw + "x" + dh + " ratio=" + String.format("%.3f", desiredAspectRatio))
        setMeasuredDimension(dw, dh)
    }

    fun videoRectInView(): Rect = Rect(0, 0, width, height)
}