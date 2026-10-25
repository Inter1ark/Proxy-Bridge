package org.proxybridge.android.ui

import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/** The desktop palette: dark navy, blue action, cyan accents. */
object Pb {
    val bg = Color(0xFF0A0F1E)
    val surface = Color(0xFF111A2E)
    val surface2 = Color(0xFF172238)
    val line = Color(0xFF223050)
    val blue = Color(0xFF3A62FF)
    val blueDark = Color(0xFF2C50E6)
    val cyan = Color(0xFF19D3FF)
    val text = Color(0xFFEEF1F8)
    val muted = Color(0xFF8B95B0)
    val danger = Color(0xFFFF5C5C)
    val warn = Color(0xFFFFC14D)
    val ok = Color(0xFF34D399)
}

private val scheme: ColorScheme = darkColorScheme(
    primary = Pb.blue,
    onPrimary = Pb.text,
    secondary = Pb.cyan,
    background = Pb.bg,
    onBackground = Pb.text,
    surface = Pb.surface,
    onSurface = Pb.text,
    surfaceVariant = Pb.surface2,
    onSurfaceVariant = Pb.muted,
    outline = Pb.line,
    error = Pb.danger,
)

private val type = Typography(
    titleLarge = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.Bold, letterSpacing = 0.5.sp),
    titleMedium = TextStyle(fontSize = 16.sp, fontWeight = FontWeight.SemiBold),
    bodyMedium = TextStyle(fontSize = 14.sp),
    bodySmall = TextStyle(fontSize = 12.sp, color = Pb.muted),
    labelSmall = TextStyle(fontSize = 11.sp, fontFamily = FontFamily.Monospace, letterSpacing = 1.sp),
)

@Composable
fun PbTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = scheme, typography = type, content = content)
}
