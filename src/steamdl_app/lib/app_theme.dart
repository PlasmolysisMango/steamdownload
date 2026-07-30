import 'package:flutter/material.dart';

/// 全局视觉规范：集中管理颜色、字体、组件圆角与密度。
///
/// Windows 中文字体显示异常通常来自默认字体缺少 CJK glyph。
/// 这里使用系统字体 fallback 链，不内置字体文件，避免显著增加 APK/桌面包体积。
class AppTheme {
  static const seed = Color(0xFF66C0F4);
  static const background = Color(0xFF101923);
  static const surface = Color(0xFF172432);
  static const surfaceHigh = Color(0xFF1E3142);

  static const fontFallback = <String>[
    'Microsoft YaHei',
    'Microsoft JhengHei',
    'SimHei',
    'Noto Sans CJK SC',
    'Noto Sans SC',
    'Source Han Sans SC',
    'PingFang SC',
    'Hiragino Sans GB',
    'WenQuanYi Micro Hei',
    'Arial Unicode MS',
    'sans-serif',
  ];

  static const monoFontFallback = <String>[
    'Cascadia Mono',
    'Consolas',
    'JetBrains Mono',
    'Noto Sans Mono CJK SC',
    'Microsoft YaHei UI',
    'Microsoft YaHei',
    'Noto Sans CJK SC',
    'monospace',
  ];

  static ThemeData dark() {
    final scheme = ColorScheme.fromSeed(
      seedColor: seed,
      brightness: Brightness.dark,
      surface: surface,
    );

    final base = ThemeData(
      useMaterial3: true,
      brightness: Brightness.dark,
      colorScheme: scheme,
      scaffoldBackgroundColor: background,
      fontFamily: 'Microsoft YaHei',
      fontFamilyFallback: fontFallback,
      visualDensity: VisualDensity.adaptivePlatformDensity,
    );

    return base.copyWith(
      textTheme: base.textTheme.apply(
        bodyColor: Colors.white.withValues(alpha: 0.92),
        displayColor: Colors.white,
      ),
      cardTheme: CardThemeData(
        color: surface,
        elevation: 0,
        margin: EdgeInsets.zero,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
          side: BorderSide(color: Colors.white.withValues(alpha: 0.06)),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: surfaceHigh.withValues(alpha: 0.72),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: 0.10)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: 0.10)),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: const BorderSide(color: seed, width: 1.4),
        ),
        isDense: true,
      ),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: surface,
        indicatorColor: seed.withValues(alpha: 0.22),
        labelTextStyle: WidgetStateProperty.resolveWith(
          (states) => TextStyle(
            fontSize: 12,
            fontWeight: states.contains(WidgetState.selected)
                ? FontWeight.w700
                : FontWeight.w500,
          ),
        ),
      ),
      navigationRailTheme: NavigationRailThemeData(
        backgroundColor: surface,
        indicatorColor: seed.withValues(alpha: 0.22),
        selectedIconTheme: const IconThemeData(color: Colors.white),
        selectedLabelTextStyle: const TextStyle(fontWeight: FontWeight.w700),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          shape:
              RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          shape:
              RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
          side: BorderSide(color: Colors.white.withValues(alpha: 0.18)),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        ),
      ),
      chipTheme: base.chipTheme.copyWith(
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(999)),
        side: BorderSide(color: Colors.white.withValues(alpha: 0.10)),
      ),
      dividerTheme:
          DividerThemeData(color: Colors.white.withValues(alpha: 0.08)),
    );
  }
}
