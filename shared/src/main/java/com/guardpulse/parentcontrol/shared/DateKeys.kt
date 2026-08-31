package com.guardpulse.parentcontrol.shared

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object DateKeys {
    fun today(): String = SimpleDateFormat("yyyy-MM-dd", Locale.US).format(Date())
}
