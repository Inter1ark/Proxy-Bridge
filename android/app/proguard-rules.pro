# gomobile bindings are reached through JNI by name
-keep class org.proxybridge.pbmobile.** { *; }
-keep class go.** { *; }
# kotlinx.serialization models
-keep,includedescriptorclasses class org.proxybridge.android.data.**$$serializer { *; }
-keepclassmembers class org.proxybridge.android.data.** { *** Companion; }
-keepclasseswithmembers class org.proxybridge.android.data.** { kotlinx.serialization.KSerializer serializer(...); }
