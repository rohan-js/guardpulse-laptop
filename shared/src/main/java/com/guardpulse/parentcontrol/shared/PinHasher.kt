package com.guardpulse.parentcontrol.shared

import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec

data class PinHash(
    val salt: String,
    val hash: String,
    val version: Int = PinHasher.CURRENT_VERSION,
    val algorithm: String = PinHasher.CURRENT_ALGORITHM,
    val iterations: Int = PinHasher.CURRENT_ITERATIONS
)

object PinHasher {
    const val LEGACY_VERSION = 1
    const val CURRENT_VERSION = 2
    const val CURRENT_ALGORITHM = "PBKDF2WithHmacSHA256"
    const val CURRENT_ITERATIONS = 210_000
    private const val KEY_LENGTH_BITS = 256
    private val random = SecureRandom()

    fun create(pin: String): PinHash {
        require(pin.matches(Regex("\\d{6}"))) { "PIN must be exactly six digits" }
        val saltBytes = ByteArray(16)
        random.nextBytes(saltBytes)
        val salt = Base64.getUrlEncoder().withoutPadding().encodeToString(saltBytes)
        return PinHash(salt, pbkdf2(pin, salt, CURRENT_ITERATIONS))
    }

    fun verify(
        pin: String,
        salt: String,
        expectedHash: String,
        version: Int = CURRENT_VERSION,
        algorithm: String? = null,
        iterations: Int? = null
    ): Boolean {
        if (!pin.matches(Regex("\\d{6}")) || salt.isBlank() || expectedHash.isBlank()) return false
        val actual = when (version) {
            LEGACY_VERSION -> legacyHash(pin, salt)
            CURRENT_VERSION -> {
                if (algorithm != null && algorithm != CURRENT_ALGORITHM) return false
                val rounds = iterations ?: CURRENT_ITERATIONS
                if (rounds !in CURRENT_ITERATIONS..1_000_000) return false
                pbkdf2(pin, salt, rounds)
            }
            else -> return false
        }
        return MessageDigest.isEqual(
            actual.toByteArray(Charsets.UTF_8),
            expectedHash.toByteArray(Charsets.UTF_8)
        )
    }

    internal fun createLegacyForTest(pin: String, salt: String): PinHash {
        return PinHash(salt, legacyHash(pin, salt), LEGACY_VERSION, "SHA-256", 1)
    }

    private fun pbkdf2(pin: String, salt: String, iterations: Int): String {
        val spec = PBEKeySpec(
            pin.toCharArray(),
            Base64.getUrlDecoder().decode(salt),
            iterations,
            KEY_LENGTH_BITS
        )
        return try {
            val bytes = SecretKeyFactory.getInstance(CURRENT_ALGORITHM).generateSecret(spec).encoded
            Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
        } finally {
            spec.clearPassword()
        }
    }

    private fun legacyHash(pin: String, salt: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val bytes = digest.digest("$salt:$pin".toByteArray(Charsets.UTF_8))
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    }
}
