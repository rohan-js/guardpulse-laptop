import java.io.FileInputStream
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val firebaseProperties = Properties().apply {
    rootProject.file("firebase.local.properties").takeIf { it.isFile }?.inputStream()?.use(::load)
}
val signingProperties = Properties().apply {
    rootProject.file("signing.local.properties").takeIf { it.isFile }?.inputStream()?.use(::load)
}
val releaseRequested = gradle.startParameter.taskNames.any { it.contains("release", ignoreCase = true) }
fun quoted(value: String) = "\"${value.replace("\\", "\\\\").replace("\"", "\\\"")}\""
fun firebaseValue(name: String, placeholder: String) = firebaseProperties.getProperty(name) ?: placeholder

android {
    namespace = "com.guardpulse.parentcontrol.parent"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.guardpulse.laptop.parent"
        minSdk = 26
        targetSdk = 34
        versionCode = providers.gradleProperty("guardpulse.versionCode").orElse("2").get().toInt()
        versionName = providers.gradleProperty("guardpulse.versionName").orElse("0.2.0").get()
    }

    signingConfigs {
        create("privateRelease") {
            if (signingProperties.isNotEmpty()) {
                storeFile = file(signingProperties.getProperty("storeFile"))
                storePassword = signingProperties.getProperty("storePassword")
                keyAlias = signingProperties.getProperty("keyAlias")
                keyPassword = signingProperties.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        getByName("debug") {
            buildConfigField("String", "FIREBASE_APP_ID", quoted(firebaseValue("parent.appId", "YOUR_PARENT_FIREBASE_APP_ID")))
            buildConfigField("String", "FIREBASE_API_KEY", quoted(firebaseValue("firebase.apiKey", "YOUR_FIREBASE_API_KEY")))
            buildConfigField("String", "FIREBASE_PROJECT_ID", quoted(firebaseValue("firebase.projectId", "your-firebase-project-id")))
            buildConfigField(
                "String",
                "FIREBASE_DATABASE_URL",
                quoted(firebaseValue("firebase.databaseUrl", "https://your-firebase-project-id-default-rtdb.firebaseio.com"))
            )
        }
        getByName("release") {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.getByName("privateRelease")
            buildConfigField(
                "String",
                "FIREBASE_APP_ID",
                quoted(firebaseValue("parent.appId", "YOUR_PARENT_FIREBASE_APP_ID"))
            )
            buildConfigField(
                "String",
                "FIREBASE_API_KEY",
                quoted(firebaseValue("firebase.apiKey", "YOUR_FIREBASE_API_KEY"))
            )
            buildConfigField(
                "String",
                "FIREBASE_PROJECT_ID",
                quoted(firebaseValue("firebase.projectId", "your-firebase-project-id"))
            )
            buildConfigField(
                "String",
                "FIREBASE_DATABASE_URL",
                quoted(firebaseValue("firebase.databaseUrl", "https://example.invalid"))
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    composeOptions {
        kotlinCompilerExtensionVersion = "1.5.14"
    }
}

val verifyParentReleaseConfiguration = tasks.register("verifyParentReleaseConfiguration") {
    doLast {
        if (!releaseRequested) return@doLast
        val requiredFirebase = listOf("firebase.apiKey", "firebase.projectId", "firebase.databaseUrl", "parent.appId")
        require(requiredFirebase.all { !firebaseProperties.getProperty(it).isNullOrBlank() }) {
            "Release Firebase values are missing from ignored firebase.local.properties"
        }
        val requiredSigning = listOf("storeFile", "storePassword", "keyAlias", "keyPassword", "expectedSha256")
        require(requiredSigning.all { !signingProperties.getProperty(it).isNullOrBlank() }) {
            "Release signing values are missing from ignored signing.local.properties"
        }
        val store = rootProject.file(signingProperties.getProperty("storeFile"))
        val keyStore = KeyStore.getInstance(signingProperties.getProperty("storeType", "JKS"))
        FileInputStream(store).use {
            keyStore.load(it, signingProperties.getProperty("storePassword").toCharArray())
        }
        val certificate = keyStore.getCertificate(signingProperties.getProperty("keyAlias"))
            ?: error("Release signing certificate was not found")
        val actual = MessageDigest.getInstance("SHA-256")
            .digest(certificate.encoded)
            .joinToString("") { "%02x".format(it) }
        require(actual.equals(signingProperties.getProperty("expectedSha256").replace(":", ""), true)) {
            "Release signing certificate SHA-256 does not match the installed TV certificate"
        }
    }
}
tasks.matching { it.name == "preReleaseBuild" }.configureEach {
    dependsOn(verifyParentReleaseConfiguration)
}

dependencies {
    implementation(project(":shared"))
    implementation(platform("com.google.firebase:firebase-bom:33.4.0"))
    implementation(platform("androidx.compose:compose-bom:2024.06.00"))
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.3")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.3")
    implementation("androidx.fragment:fragment-ktx:1.8.5")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("com.google.firebase:firebase-auth")
    implementation("com.google.firebase:firebase-database")
    debugImplementation("androidx.compose.ui:ui-tooling")
    testImplementation("junit:junit:4.13.2")
}
