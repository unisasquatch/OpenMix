# Third-party components

Full licence texts are in `licenses/`. The installer copies them, plus the .NET runtime's, into the install folder.
The import tools are pinned to exact releases by SHA-256 in `installer/build.ps1`.

| Component | Used for | License | Text |
|---|---|---|---|
| [Photino.NET](https://github.com/tryphotino/photino.NET) | The app window (WebView2) | Apache-2.0 | `Photino.NET-LICENSE.txt`, `Apache-2.0.txt` |
| [NAudio](https://github.com/naudio/NAudio) | Audio output | MIT | `NAudio-LICENSE.txt` |
| [NVorbis](https://github.com/NVorbis/NVorbis) | Ogg Vorbis decoding | MIT | `NVorbis-LICENSE.txt` |
| [QR Code Generator for JavaScript](https://github.com/kazuhikoarase/qrcode-generator) | Join QR code (`wwwroot/vendor/qrcode.js`) | MIT | `qrcode-generator-LICENSE.txt` |
| [.NET runtime](https://github.com/dotnet/runtime) | App runtime, plus a private copy for AssetStudioModCLI in `tools/dotnet` | MIT | `dotnet-LICENSE.txt`, `dotnet-THIRD-PARTY-NOTICES.txt` (installer) |
| [AssetStudioModCLI](https://github.com/aelurum/AssetStudio) 0.20.0 | Reading Unity files during import (`tools/AssetStudioCLI`) | MIT | `AssetStudio-LICENSE.txt` |
| ↳ [Texture2DDecoder](https://github.com/KiruyaMomochi/Texture2DDecoder) (Kyaru.Texture2DDecoder) | Texture decoding | MIT | `Texture2DDecoder-LICENSE.txt` |
| ↳↳ crunch / unity crunch (Richard Geldreich, Jr., Binomial LLC) | Crunched texture decoding | zlib | `crunch-LICENSE.txt` |
| ↳↳ [FP16](https://github.com/Maratyszcza/FP16) | Half-float conversion | MIT | `fp16-LICENSE.txt` |
| ↳ [ImageSharp](https://github.com/SixLabors/ImageSharp), [Fonts](https://github.com/SixLabors/Fonts), ImageSharp.Drawing | Image handling | Six Labors Split License; Apache-2.0 for open-source use | `ImageSharp-LICENSE.txt`, `Fonts-LICENSE.txt`, `Apache-2.0.txt` |
| ↳ [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) | Decompression | MIT | `K4os.LZ4-LICENSE.txt` |
| ↳ [ZstdSharp](https://github.com/oleg-st/ZstdSharp) | Decompression | MIT | `ZstdSharp-LICENSE.txt` |
| ↳ [Mono.Cecil](https://github.com/jbevain/cecil) | Assembly reading | MIT | `Mono.Cecil-LICENSE.txt` |
| ↳ System.IO.Hashing | Hashing | MIT (.NET) | `dotnet-LICENSE.txt` |
| [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) 6.7.46 | Type information for reading the game's scene settings during import (`tools/Il2CppDumper`) | MIT | `Il2CppDumper-LICENSE.txt` |
| [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools) (adb) r35.0.0 | Optional install component: copy DropMix off an Android phone over USB (`tools/platform-tools`) | Apache-2.0 | `tools/platform-tools/NOTICE.txt`, `Apache-2.0.txt` |
| [vgmstream](https://github.com/vgmstream/vgmstream) | `src/OpenMix.Importer/Fsb5.cs` follows its FADPCM decoder | ISC-style | `vgmstream-COPYING.txt` |

Not shipped: AssetStudio's FBX exporter (Autodesk FBX SDK), FMOD and Oodle libraries. OpenMix never calls them.
