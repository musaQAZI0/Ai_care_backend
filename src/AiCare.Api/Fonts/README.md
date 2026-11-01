# Embedded finance PDF fonts

These unmodified static Noto font binaries are distributed under the SIL Open Font License 1.1. The upstream licences are included beside them and copied on publish. Rendering uses embedded resources without operating-system fonts or runtime downloads.

Static replacements retrieved on 2026-10-31:

| File | Upstream source | SHA-256 |
|---|---|---|
| NotoSans.ttf | https://github.com/notofonts/noto-fonts/blob/main/hinted/ttf/NotoSans/NotoSans-Regular.ttf | B85C38ECEA8A7CFB39C24E395A4007474FA5A4FC864F6EE33309EB4948D232D5 |
| NotoSansArabic.ttf | https://github.com/notofonts/noto-fonts/blob/main/hinted/ttf/NotoSansArabic/NotoSansArabic-Regular.ttf | CEEA25B464A656DC3B26849BAB9356740401AF62AEDF1BFA8B7F0D9B75925B1B |
| NotoSansSC.otf | https://github.com/notofonts/noto-cjk/blob/main/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf | 2C76254F6FC379FDDFCE0A7E84FB5385BB135D3E399294F6EEB6680D0365B74B |

Skia exports variable fonts as Type 3 outlines; static fonts are required for the embedded font-program assertions. The loader rejects assets containing an fvar table. The explicit resource list excludes the superseded variable NotoSansSC.ttf asset.

The checked-in binaries and hashes pin these assets; upstream main branches are not runtime dependencies. Replacing a font requires licence, coverage and document-rendering review. Unsupported glyphs and malformed UTF-16 are rejected before archival. HarfBuzz shapes script runs. Runtime rendering and extraction verification remain required before UAT.
