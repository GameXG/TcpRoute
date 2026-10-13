plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "io.tcproute.android"
    compileSdk = 35
    ndkVersion = "28.2.13676358"

    defaultConfig {
        applicationId = "io.tcproute.android"
        minSdk = 26
        targetSdk = 35
        versionCode = 111
        versionName = "1.0"
        testInstrumentationRunner = "io.tcproute.android.NativeBridgeInstrumentation"
        ndk {
            abiFilters += "arm64-v8a"
        }

    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    packaging {
        jniLibs {
            // iCloud conflict copies are generated artifacts, not loadable libraries.
            excludes += "**/* *.so"
            useLegacyPackaging = true
        }
    }

    sourceSets {
        getByName("main").assets.srcDir("../../assets")
    }

    bundle {
        language {
            enableSplit = false
        }
    }
}
