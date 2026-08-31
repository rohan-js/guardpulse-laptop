package com.guardpulse.parentcontrol.parent

import android.app.Application
import com.guardpulse.parentcontrol.shared.FirebaseConfiguration
import com.guardpulse.parentcontrol.shared.FirebaseRuntime

class ParentControlApp : Application() {
    override fun onCreate() {
        super.onCreate()
        FirebaseRuntime.initialize(
            this,
            FirebaseConfiguration(
                appId = BuildConfig.FIREBASE_APP_ID,
                apiKey = BuildConfig.FIREBASE_API_KEY,
                projectId = BuildConfig.FIREBASE_PROJECT_ID,
                databaseUrl = BuildConfig.FIREBASE_DATABASE_URL
            )
        )
    }
}
