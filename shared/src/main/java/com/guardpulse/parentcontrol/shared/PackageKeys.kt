package com.guardpulse.parentcontrol.shared

import java.util.Base64

object PackageKeys {
    fun encode(packageName: String): String {
        return Base64.getUrlEncoder()
            .withoutPadding()
            .encodeToString(packageName.toByteArray(Charsets.UTF_8))
    }

    fun decode(key: String): String {
        return String(Base64.getUrlDecoder().decode(key), Charsets.UTF_8)
    }
}
