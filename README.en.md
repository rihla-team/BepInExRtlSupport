# BepInEx RTL Support
### Support for Right-to-Left (RTL) Languages

[English](README.en.md) | [العربية](README.md) | [فارسی](README.fa.md) | [اردو](README.ur.md)

## 📖 About the Project

**BepInEx RTL Support** is a BepInEx plugin that provides full support for Right-to-Left (RTL) languages in Unity games, such as:
- Arabic
- Persian
- Urdu

---

## ✨ Features

### Text Processing
- ✅ Automatic Arabic character shaping
- ✅ RTL text rendering (reversing)
- ✅ Support for diacritics (Fatha, Damma, Kasra, etc.)
- ✅ Support for Lam-Alef ligatures
- ✅ Mixed text handling (Arabic + English)

### Number Support
- ✅ Conversion of Western Arabic numerals (0-9) to Eastern Arabic numerals (٠-٩)
- ✅ Correct number ordering in RTL context

### Compatibility
- ✅ TextMeshPro (TMP) support
- ✅ Rich Text Tags support
- ✅ Handling of variables `{variable}` and `[tags]`
- ✅ Automatic text alignment

### Performance
- ⚡ Smart Cache system using `ConcurrentDictionary`
- ⚡ `StringBuilder` Pooling to reduce GC pressure
- ⚡ Asynchronous cache cleanup

---

## 📥 Installation

1. Ensure [BepInEx](https://github.com/BepInEx/BepInEx) is installed in your game.
2. Copy `BepInExRtlSupport.dll` to the `BepInEx/plugins/` folder.
3. Launch the game.

---

## ⚙️ Configuration

You can modify settings in the file:
```
BepInEx/config/com.rihla.bepinex.rtlsupport.cfg
```

### Available Settings

| Setting | Description | Default Value |
|---------|-------------|---------------|
| `TextAlignment` | Text alignment (Auto/Right/Left/Center) | Auto |
| `ConvertToEasternArabicNumerals` | Convert Western numerals to Eastern | false |
| `CacheSize` | Maximum cache size | 1000 |
| `EnableArabic` | Enable Arabic support | true |
| `EnablePersian` | Enable Persian support | true |
| `EnableUrdu` | Enable Urdu support | true |

---

## 🛠️ Build from Source

### Requirements
- Visual Studio 2019 or later
- .NET Framework 4.7.2+
- Unity and BepInEx references

### Steps
```bash
git clone https://github.com/rihla-team/BepInExRtlSupport.git
cd BepInExRtlSupport
dotnet build -c Release
```

---

## 📁 Project Structure

```
BepInExRtlSupport/
├── main.cs                  # Main entry point
├── ArabicTextProcessor.cs   # Text processing engine
├── ArabicGlyphForms.cs      # Glyph forms dictionary
├── RTLHelper.cs             # RTL character utility functions
├── Patches.cs               # Harmony Patches for TextMeshPro
├── ModConfiguration.cs      # Configuration system
├── PerformanceMonitor.cs    # Performance monitoring
└── Diagnostics.cs           # Diagnostic tools
```

---

## 🤝 Contributing

We welcome your contributions! You can:
- 🐛 Report bugs
- 💡 Suggest new features
- 🔧 Submit Pull Requests

---

## 📄 License

This project is licensed under the MIT License.

---

## 👥 Development Team

<div align="center">

### Rihla Team

---

**Development & Programming:**
**Ibn Al-Sadeem** ([@lub131](https://github.com/lub131))

</div>

---

## 🙏 Special Thanks

- **Mohammed** ([@momaqbol](https://github.com/momaqbol))
- [BepInEx](https://github.com/BepInEx/BepInEx) team
- Unity Modding community
- All contributors and testers

---

<div align="center">

**Made with ❤️ by Rihla Team**

[![GitHub](https://img.shields.io/badge/GitHub-Rihla_Team-00796B?style=flat-square&logo=github)](https://github.com/rihla-team)

</div>

