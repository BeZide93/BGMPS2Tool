# Credits

This export helper folder uses **VGMTrans** as its upstream conversion tool.

## Upstream Project

- **Project:** VGMTrans - Video Game Music Translator
- **Repository:** https://github.com/vgmtrans/vgmtrans
- **Authors:** The VGMTrans Team

VGMTrans is an open-source tool for detecting, inspecting, and converting sequenced video game music into standard formats such as:

- MIDI
- SoundFont 2 (SF2)
- DLS

## License

VGMTrans is distributed under the **zlib license**.

Copyright (c) 2002-2025 The VGMTrans Team

This package includes a locally built `vgmtrans-cli.exe` based on the VGMTrans source code, plus the runtime files needed to use the KH2 export batch in this folder.

## Local Packaging Note

This `VGMTransExportBatch` folder is only a convenience wrapper around VGMTrans for KH2 workflow use.

- `VGMTransExportKh2.bat` is a local helper batch
- `vgmtrans-cli.exe` is derived from the VGMTrans source project
- VGMTrans itself remains the original upstream tool and should be credited accordingly

If you use or redistribute this helper, please keep this credit notice with it.
