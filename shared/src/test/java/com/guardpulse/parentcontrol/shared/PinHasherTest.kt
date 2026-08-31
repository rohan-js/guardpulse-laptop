package com.guardpulse.parentcontrol.shared

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PinHasherTest {
    @Test
    fun verifiesOnlyMatchingPin() {
        val created = PinHasher.create("123456")

        assertTrue(PinHasher.verify("123456", created.salt, created.hash, created.version, created.algorithm, created.iterations))
        assertFalse(PinHasher.verify("654321", created.salt, created.hash, created.version, created.algorithm, created.iterations))
        assertFalse(PinHasher.verify("", created.salt, created.hash, created.version, created.algorithm, created.iterations))
    }

    @Test
    fun legacyHashesRemainCompatible() {
        val created = PinHasher.createLegacyForTest("123456", "c2FsdC1mb3ItdGVzdA")

        assertTrue(PinHasher.verify("123456", created.salt, created.hash, PinHasher.LEGACY_VERSION))
        assertFalse(PinHasher.verify("654321", created.salt, created.hash, PinHasher.LEGACY_VERSION))
    }
}
