package org.proxybridge.android

import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** What the UI shows about the tunnel; written by the service and the core callbacks. */
object TunnelState {
    enum class Phase { Off, Connecting, On, Error }

    data class Snapshot(
        val phase: Phase = Phase.Off,
        val message: String = "",   // the error text, or empty
        val up: Long = 0,
        val down: Long = 0,
    )

    private val _state = MutableStateFlow(Snapshot())
    val state: StateFlow<Snapshot> get() = _state

    private val lines = ArrayDeque<String>()
    private val _log = MutableStateFlow("")
    val log: StateFlow<String> get() = _log

    fun set(phase: Phase, message: String = "") {
        _state.value = _state.value.copy(phase = phase, message = message)
    }

    fun stats(up: Long, down: Long) {
        _state.value = _state.value.copy(up = up, down = down)
    }

    fun log(line: String) {
        Log.i("pbcore", line)
        synchronized(lines) {
            lines.addLast(line)
            while (lines.size > 500) lines.removeFirst()
            _log.value = lines.joinToString("\n")
        }
    }
}
