package com.guardpulse.parentcontrol.shared

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class PackageKeysTest {
    @Test
    fun roundTripPackageName() {
        val packageName = "com.google.android.youtube.tv"

        val encoded = PackageKeys.encode(packageName)

        assertFalse(encoded.contains("."))
        assertEquals(packageName, PackageKeys.decode(encoded))
    }
}
