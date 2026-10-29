// 顶层构建脚本：只声明插件版本，不在此应用。
// 版本组合已在本机验证：Gradle 8.11.1 + AGP 8.10.1 + Kotlin 2.1.21 + JDK 21。
plugins {
    id("com.android.application") version "8.10.1" apply false
    id("org.jetbrains.kotlin.android") version "2.1.21" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.1.21" apply false
}
