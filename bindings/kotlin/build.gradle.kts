plugins {
    kotlin("jvm") version "2.0.21"
    application
}

repositories { mavenCentral() }

kotlin { jvmToolchain(21) }

application {
    mainClass.set("io.glyph11.MainKt")
}

// java.lang.foreign (Panama / FFM) is a preview feature on JDK 21.
val previewArgs = listOf("--enable-preview", "--enable-native-access=ALL-UNNAMED")

tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach {
    compilerOptions { freeCompilerArgs.add("-Xjvm-enable-preview") }
}

tasks.named<JavaExec>("run") {
    jvmArgs(previewArgs)
    System.getenv("GLYPH11_LIB")?.let { systemProperty("glyph11.lib", it) }
}
