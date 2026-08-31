package com.guardpulse.parentcontrol.shared

import android.content.Context
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions
import com.google.firebase.database.FirebaseDatabase

data class FirebaseStatus(
    val configured: Boolean,
    val message: String
)

data class FirebaseConfiguration(
    val appId: String,
    val apiKey: String,
    val projectId: String,
    val databaseUrl: String
)

object FirebaseBootstrap {
    fun initialize(context: Context): FirebaseStatus {
        if (FirebaseApp.getApps(context).isNotEmpty()) {
            return FirebaseStatus(configured = true, message = "Firebase initialized")
        }
        return FirebaseStatus(
            configured = false,
            message = "Firebase must be configured by the application"
        )
    }

    fun initialize(context: Context, configuration: FirebaseConfiguration): FirebaseStatus {
        if (FirebaseApp.getApps(context).isNotEmpty()) {
            return FirebaseStatus(configured = true, message = "Firebase initialized")
        }
        val missing = listOf(
            "appId" to configuration.appId,
            "apiKey" to configuration.apiKey,
            "projectId" to configuration.projectId,
            "databaseUrl" to configuration.databaseUrl
        ).filter { (_, value) -> isPlaceholder(value) }

        if (missing.isNotEmpty()) {
            return FirebaseStatus(
                configured = false,
                message = "Firebase configuration is not available in this build"
            )
        }

        val options = FirebaseOptions.Builder()
            .setApplicationId(configuration.appId)
            .setApiKey(configuration.apiKey)
            .setProjectId(configuration.projectId)
            .setDatabaseUrl(configuration.databaseUrl)
            .build()
        FirebaseApp.initializeApp(context, options)
        return FirebaseStatus(configured = true, message = "Firebase initialized")
    }

    private fun isPlaceholder(value: String): Boolean {
        val normalized = value.trim().lowercase()
        return normalized.isBlank() ||
            normalized.startsWith("replace_") ||
            normalized.startsWith("your_") ||
            normalized.contains("your-firebase-project") ||
            normalized.contains("example.invalid")
    }
}

object FirebaseRuntime {
    @Volatile
    private var persistenceAttempted = false

    @Synchronized
    fun initialize(context: Context): FirebaseStatus {
        val status = FirebaseBootstrap.initialize(context)
        return enablePersistence(status)
    }

    @Synchronized
    fun initialize(context: Context, configuration: FirebaseConfiguration): FirebaseStatus {
        val status = FirebaseBootstrap.initialize(context, configuration)
        return enablePersistence(status)
    }

    private fun enablePersistence(status: FirebaseStatus): FirebaseStatus {
        if (!status.configured || persistenceAttempted) return status
        persistenceAttempted = true
        runCatching {
            FirebaseDatabase.getInstance().setPersistenceEnabled(true)
        }
        return status
    }
}
