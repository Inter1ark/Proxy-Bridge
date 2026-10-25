plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "org.proxybridge.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "org.proxybridge.android"
        minSdk = 29
        targetSdk = 35
        versionCode = 1
        versionName = "3.7.0-dev"
    }
    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // pbcore: built from core-go/mobile with gomobile (see core-go/build-android.ps1)
    implementation(files("libs/pbcore.aar"))
}
