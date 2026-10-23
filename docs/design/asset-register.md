# Asset register

Every font, icon, image and ornament shipped in the application, with its source and licence. Add a row before any new asset is committed. Sacred images (icons of Christ, the Theotokos, saints, feasts) must be authentic, never generated, and recorded here with their source and rights (`docs/design/redesign-brief.md`).

| Asset | Where | Source | Licence / rights | Attribution required | Added |
|---|---|---|---|---|---|
| Alegreya (variable, Latin, Latin Extended, Greek; normal and italic) | `src/Vlms.Web/wwwroot/fonts/alegreya-*.woff2` | The Alegreya Project Authors (Juan Pablo del Peral, Huerta Tipográfica), via the `@fontsource-variable/alegreya` 5.3.0 package files | SIL Open Font License 1.1, text in `wwwroot/fonts/OFL-alegreya.txt` | Keep the licence file with the fonts | 23/10/2026 |
| Source Sans 3 (variable, Latin, Latin Extended, Greek; normal and italic) | `src/Vlms.Web/wwwroot/fonts/source-sans-3-*.woff2` | Adobe, via the `@fontsource-variable/source-sans-3` 5.3.1 package files | SIL Open Font License 1.1, text in `wwwroot/fonts/OFL-source-sans-3.txt` | Keep the licence file with the fonts | 23/10/2026 |
| Interface icons | `src/Vlms.Web/Components/Ui/Icon.razor` (inline SVG paths) | Mostly adapted from Lucide (https://lucide.dev); some drawn for this project (home, certificate, id-card) | ISC licence (https://lucide.dev/license); permissive, notice to be kept | Keep the notice in `Icon.razor` and this row | 23/10/2026 |
| Product mark: budded Golgotha cross with IC XC NI KA | `Components/Ui/BrandMark.razor`, `wwwroot/icons/icon.svg` | Drawn for this project after the group's own Varangian Guard patch | Original work for Varangians | None | 23/10/2026 |
| Interlace band (two-strand twist) | `Components/Ui/Band.razor` | Drawn for this project, after Anglo-Saxon two-strand interlace | Original work | None | 23/10/2026 |
| Headpiece frame, stitched rim, tesserae | CSS in `wwwroot/app.css` and `MyProgress/StudentHome.razor.css` | Drawn for this project, after Byzantine manuscript headpieces, embroidered patch rims and mosaic | Original work | None | 23/10/2026 |
| Rank badge images | Uploaded by an Admin at runtime (`/administration/rank-badges`), not in the repository | The group's own embroidered patches | The group's own | None | n/a |

Not shipped: the photographs of the patches cropped for local screenshots (`seed/admin/badge-crops/`, outside version control), used only by the opt-in design capture.
