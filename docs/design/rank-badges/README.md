# Rank badges: redesign (Phases A to D)

Status: **proposed, awaiting Philip's approval**, 25/10/2026. Nothing in the application has changed: no production code, no badge storage, no uploaded badge. Phases E (production) and F (verification) follow approval.

| File | What it is |
|---|---|
| `contact-sheet.html` / `contact-sheet.png` | The approval sheet: all ten badges in order, with symbolism, at the application's real sizes, inside the real medallion treatment, on the student interface's colours |
| `proposed/01-recruit.svg` … `proposed/10-akolouthos.svg` | The ten proposed badges (named `{order}-{rank code}` from `seed/curriculum.json`) |
| `exploration/*.svg`, `exploration/exploration-sheet.html` / `.png` | Phase B alternatives for Recruit, Shield-bearer, Torch-bearer, Cross-bearer and Akolouthos |
| `source/build_badges.py` | The source of every badge and both review pages: hand-authored vector geometry, palette and rules in one file. Run `python source/build_badges.py` to rebuild; render a PNG with any headless browser |

## 1. Audit (Phase A)

**Storage and administration.** One `RankBadge` row per rank (`src/Vlms.Domain/RankBadge.cs`); bytes at `rank-badges/{rankId}{ext}` in blob storage. Upload on `/administration/rank-badges` (National Administrator policy; `RankBadgeService` also requires the Admin role). Accepted: PNG, JPEG, WebP, SVG, at most 2 MB, content checked against the extension (an SVG must start with `<` and contain `<svg`). Served from `GET /badges/{rankId}/image` behind the lesson-library policy, with `nosniff`; an SVG response also gets `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`. No badge is uploaded in the local environment (`App_Data/blobs/` holds only lessons). Tests: `tests/Vlms.Tests/Infrastructure/RankBadgeServiceTests.cs`. **No change to any of this is needed**: the proposed SVGs upload through the existing page.

**Display.** `Components/Ui/RankMedallion.razor` (used by `RankProcession.razor`, `/student`, `/students/{id}` and `/parent/dashboard`) shows the uploaded image inside a round "patch" medallion; without an upload it shows the rank number (Akolouthos: the eagle). CSS in `wwwroot/app.css` (`.medallion`):

| Medallion | Medallion size | Badge as displayed (76%) | Where |
|---|---|---|---|
| `sm` | 2.75 rem = 44 px | **33 px** | Through the ranks (every page), next rank, earlier ranks, parent badge list |
| `md` (default) | 4 rem = 64 px | **49 px** | Student rank header, parent dashboard child header |
| `lg` | 7.5 rem = 120 px | 91 px | Defined, **not used anywhere** |
| Admin page | `<img width=64 height=64>` | 64 px, unclipped | `/administration/rank-badges` |

The image is drawn with `object-fit: contain` and **clipped to a circle** (`border-radius: 50%`), centred on the medallion's rank-field colour, inside a dashed "stitch" ring and a coloured outer ring. A rank in progress is shown at 60% opacity; a rank not yet earned is greyscale at 45% opacity; ranks ahead sit on a white medallion.

**Backgrounds.** Paper `#F7F8FA` (page), white `#FFFFFF` (panels, the procession), the rank tint behind the current station and the parent child header (`--rank-tint`), and the lapis bar `#14254F`.

**Constraints on the artwork, from the above.**
1. **Round**: anything outside the circle is clipped. A full circular badge on a transparent square is the right shape.
2. **Tiny**: 33 px and 49 px in practice. One bold central symbol; ornament must survive as texture; fine detail is lost.
3. **No fonts**: the SVG is shown through `<img>` under `default-src 'none'`, so no web font, image or other external resource can load. Any lettering must be drawn as vector strokes (it is). This also makes the files safe for the endpoint as it stands: no script, no external reference.
4. **Greyscale must still read** (not yet earned): symbols are separated from fields by value, not hue alone, and every shape carries a dark outline.
5. Existing patches (`seed/admin/Illustration of Badges.jpg`) carry lettering round the rim; at 33 to 49 px lettering would be illegible, so the proposed badges have none (decision D2).

## 2. The design brief and system rules

The collection is a set of embroidered patches in an Orthodox, Byzantine and Anglo-Saxon vocabulary: the Ephesians 6 armour for the Guard, the servers' procession for the middle ranks, and the sanctuary at the end (the reading already agreed in `docs/design/redesign-direction.md`). Weapons appear only where the rank names them, and then as Ephesians 6 does.

**Shared rules (every badge, built by one `frame()` and one `badge()` function):**

| Element | Rule |
|---|---|
| Silhouette | Circle, viewBox `0 0 512 512`, transparent outside r 254 |
| Rim | Merrowed edge: ink ring (r 254) with a dark field-shade band overstitched in short gold satin stitches (dashed stroke), then a 5-unit gold keyline (r 234) |
| Ornament band | r 207 to 230, on the field shade, between gold keylines; **one pattern per movement** (below) |
| Field | r 203, the rank colour, with faint one-direction satin stitches (never crossed: no grid) and the shared ground ornament |
| Ground ornament | Two interlaced squares (an eight-pointed figure) with eight pellets, tone on tone at 50% (20% on light fields), after Lukovich's medallion |
| Central symbol | Drawn on a 190-radius field and scaled ×1.14; stays inside r 188; one symbol, centred, occupying about 70% of the field |
| Outline | One ink thread `#1F1828`, 11 units (about 1 px at 49 px), round joins |
| Shading | One light source, upper left: a flat offset shadow (5, 8) at 42% for raised embroidery; thin satin sheen stitches on lit edges. No gradients, no metallic effects |
| Typography | None, except Greek abbreviations that belong to the symbol (IC XC, NI KA), drawn as monoline strokes with the titlo bar, never as font text |

**Ornament band by movement** (the "selected ornamental detail" that changes; matches the stage labels in `RankProcession.razor`):

| Movement | Ranks | Band | Why |
|---|---|---|---|
| Joining the Guard | 1–4 | Anglo-Saxon two-strand twist with pellets | The Guard's English origin; manuscript knotwork |
| Proclaiming | 5 | O'Rourke's notched (sawtooth) border, bent into a ring | A border of going-out, from the OAJ icon frames |
| In the procession | 6–8 | Pearled border | Pearls on Byzantine processional crosses and Gospel covers |
| Bearing Christ, Leading | 9–10 | Pearls alternating with gold-set jewels | The jewelled border of the sanctuary |

The higher ranks are not bigger or brighter: every badge has the same frame weight, symbol scale and detail budget. The progression is in the ideas (armour, then proclamation, then the procession's candle, banner and cross, then Communion and the Church's emblem) and in the band.

## 3. Palette

Shared threads (every badge): ink `#1F1828`, gold `#EDB637`, gold shade `#B47C17`, gold sheen `#FFE7A3`, ivory `#F6EBD2`, pearl `#FFF8EA` / `#F4E2B0`, silver `#D9DFE6` / `#8D99A8`, crimson `#A51C2E` / `#6F1120`, flame `#F08A24` / `#FFD25A`.

| Rank | Field | Shade (rim, band) | Rationale |
|---|---|---|---|
| Recruit | `#A51C2E` crimson | `#5E0F1B` | The group's own colours: the logo is crimson and gold. Joining means receiving them |
| Guardsman | `#1E7B4F` malachite | `#0E3F28` | Steadfast green behind silver armour |
| Shield-bearer | `#2346A8` lapis | `#122459` | Faith and heaven; the crimson-and-gold shield stands out on it |
| Sword-bearer | `#C8431F` vermilion | `#6A200C` | Courage, warm and bright without being blood red |
| Herald | `#178AA0` turquoise | `#0B4652` | Open sky: proclaiming outwards |
| Torch-bearer | `#1C1E4C` midnight | `#0B0C24` | Light shines in the darkness: the flame needs a dark ground |
| Standard-bearer | `#DE9A1E` saffron | `#7A4C06` | Red banner on gold: the Byzantine imperial colours (as in the group's flag asset) |
| Cross-bearer | `#F3E6C6` ivory | `#7A1424` | Paschal white: the one light field, so the Cross stands out in the set |
| Christ-bearer | `#7A1736` garnet | `#3C0A1A` | The colour of the wine of the Eucharist |
| Akolouthos | `#4A2A74` porphyry purple | `#221238` | Imperial purple: the akolouthos stood beside the Emperor (decision D3) |

Neighbouring ranks never share a hue family. Gold is the common thread, not the theme: no field is the app's lapis-and-gold pairing except, by choice, the Shield-bearer's lapis.

## 4. The ten badges (Phase C)

| # | Rank | Central symbol | Symbolism |
|---|---|---|---|
| 1 | Recruit | Eight-pointed star with pearls (after Gould's stars) | The star that led the Magi to Christ: the first light on the way, the beginning of a journey made together. The Nativity is a Recruit lesson |
| 2 | Guardsman | Eleventh-century spangenhelm with nasal, mail and the Cross on its front plate | The helmet of salvation (Ephesians 6:17): vigilance and service. Drawn wider than tall, with plates and a dark face opening, so it cannot read as a mitre |
| 3 | Shield-bearer | Kite shield, crimson in gold, with a flared cross with Gould's knobbed terminals | The shield of faith (Ephesians 6:16), in the shape the Guard carried (the shape embroidered on the Bayeux Tapestry) |
| 4 | Sword-bearer | Upright sword over the open Gospel | The sword of the Spirit, which is the word of God (Ephesians 6:17): the sword and the book are one meaning |
| 5 | Herald | Straight trumpet with a swallow-tailed pennant bearing a flared cross; sound going out | Proclamation: the Faith announced and carried outwards |
| 6 | Torch-bearer | The server's processional candle with its flame in a nimbus of light | The light of Christ that enlightens all; the first of the procession's three roles |
| 7 | Standard-bearer | The labarum: banner on a cross-staff, bearing the Chi-Rho (☧) | The standard of the Cross carried before Constantine's army and in the Church's processions |
| 8 | Cross-bearer | Processional cross, crimson and gold, pearled terminals, on its staff, with IC XC NI KA | Jesus Christ conquers: the Cross at the centre of the procession and of the faith |
| 9 | Christ-bearer | The chalice, marked with the Cross, with IC XC either side of the stem | Receiving Christ in Holy Communion, the communicant carries Him into the world (Christophoros; cf. Galatians 2:20). No figure of Christ is drawn (decision D4) |
| 10 | Akolouthos | The double-headed eagle in gold | The emblem of the Orthodox Church, for the commander of the Guard who stood closest to the Emperor |

Checked as a collection (contact sheet): each is distinguishable at 33 px by silhouette alone (star, dome, kite, cross-on-book, diagonal, candle, square banner, cross, cup, eagle); one frame, one outline, one light; colours vary deliberately; no badge departs from the system.

## 5. Phase B alternatives and the choices

See `exploration/exploration-sheet.png`.

| Rank | A | B | C | Chosen, and why |
|---|---|---|---|---|
| Recruit | Star of the Magi | Budded cross rising from a seed | The group's cross with IC XC NI KA | **A.** The strongest, most distinct silhouette at 33 px. B is lovely in meaning, but the set already has four crosses. C duplicates the product mark and, at size, the Cross-bearer |
| Shield-bearer | Kite shield with the Cross | Anglo-Saxon round shield | Kite shield with the Chi-Rho on gold | **A.** B reads as a target at small sizes (a round shield in a round badge); C takes the labarum's Chi-Rho from the Standard-bearer |
| Torch-bearer | Processional candle | Hand-held torch | Hanging oil lamp | **A.** It is what servers actually carry in the procession and links rank 6 to ranks 7 and 8. B echoes the old patch but reads as an Olympic torch; C is a lamp before the icons, not a borne light |
| Cross-bearer | Processional cross, IC XC NI KA | Golgotha cross with spear and reed | Flowering cross with rosettes | **A.** The rank bears the processional cross; the letters say why it is carried. B is the strongest image of the sacrifice, but heavy for the set; C loses its rosettes at 33 px |
| Akolouthos | Gold eagle on purple | Gold eagle on lapis (as agreed 23/10/2026) | Gold eagle on crimson | **A, for Philip to confirm (D3).** Purple is the imperial colour, and it separates rank 10 from the Shield-bearer's lapis and the Torch-bearer's midnight. C would echo the Recruit |

**Critique and iteration (recorded honestly).** Round 1: symbols too small for 33 px, the frame too heavy, and too clean and "sticker-like", with little visible OAJ character. Fixed with a thinner frame, symbols scaled up, a bolder ink line, Gould's knobbed terminals, the shared Lukovich ground, and smaller gold-tinted pearls (white pearls read as a dotted cog). Round 2: lettering collided with crosses; the candle halo of tick-rays looked like an alarm clock; several symbols overran the field; the medallion rings still used today's colours. All fixed. Round 3: the Guardsman helmet read as a bishop's mitre (redrawn as a spangenhelm); the labarum's Chi-Rho was malformed and could be misread as a hammer and sickle (redrawn as a correct ☧); the Herald's white cross on red read as the Swiss flag or the Red Cross (now a gold flared cross on a swallow-tailed pennant).

## 6. Reference images studied, and what was taken

Downloaded fresh (25/10/2026) from the Orthodox Arts Journal's Orthodox Illustration Project (https://orthodoxartsjournal.org/orthodox-illustration-project/) through the project's own download links, 52 drawings, and viewed. **No OAJ drawing is copied, traced or embedded in any badge**; these are principles, redrawn from scratch.

| Image (artist) | Taken |
|---|---|
| Byzantine crosses 1–5, Cross and dots, Jerusalem Cross (Gould, small ornaments) | Flared arms with concave ends and round knobs at the corners: the cross shape used on the shield, helmet, pennant, chalice and processional cross. The bold, solid, slightly hand-cut silhouette |
| Stars 1–3 (Gould) | The eight-pointed star with a ring of dots: Recruit |
| Medallion (Lukovich) | A circle enclosing two interlaced squares: the shared ground ornament. The idea of geometric ornament on a field around a centre |
| Ornament from a 12th-century Armenian Gospel (Crider), St Cuthbert's Gospel cover (Crider), Russian interlace and knot bars | Double-ruled frames and interlace bindings: the Guard's twisted cable band and its pellets |
| Sts George and Demetrios, Entry into Jerusalem, Christ Enthroned notched border (O'Rourke) | The notched (sawtooth) border: the Herald's band. The confident, even ink line |
| Byzantine flower cross, Tree of Life cross (O'Rourke) | Rosette and pearl terminals: Cross-bearer alternative C and the pearled border |
| Golgotha cross, Three-bar cross, Greek cross with letters (Gould) | The titlo over IC XC and the placement of IC XC NI KA in the four quadrants: Cross-bearer. Golgotha: alternative B |
| Prosphora seal (Lukovich) | The IC XC NI KA arrangement; considered and rejected for Recruit (a liturgical seal, not a rank sign) |
| Divine Liturgy 4 and 10 (ONL) | A child lighting candles; young servers beside the priest: the Torch-bearer as the server with his candle, not a torch |
| Architectural frame (O'Rourke), Carpet page and Header illuminations (Gould) | Viewed; not used. They contain domes and towers, which the badges avoid entirely |

Also viewed: the group's patches (`seed/admin/Illustration of Badges.jpg`: their merrowed rims, lettering and colours informed the rim and the bright fields), the logo (crimson and gold: the Recruit's colours), the Byzantine imperial flag (red and gold: the Standard-bearer), and the double-headed eagle.

Avoided by rule: no domes, skylines or Hagia Sophia; no grid or grout textures (the satin stitches run in one direction only); no horned helmets, runes or fantasy styling; no figure of Christ, the Theotokos, saints or angels.

## 7. Sources, attribution and licences

- **Badge artwork**: original, drawn for this project in `source/build_badges.py`. No raster images, no fonts, no external references.
- **Double-headed eagle** (Akolouthos): the app's existing asset `src/Vlms.Web/wwwroot/img/eagle.svg`, public domain (Wikimedia Commons, "Double-headed eagle of the Greek Orthodox Church.svg" by F l a n k e r; see `docs/design/asset-register.md`). Embedded unchanged as a path, filled gold. No attribution required.
- **Orthodox Illustration Project**: used only as visual reference; nothing reproduced, so its terms (free for non-commercial use, credit OAJ and the artist, modification not addressed: `docs/design/redesign-research.md` section 1) are not engaged. Credit is still given here: Andrew Gould, Scott Patrick O'Rourke, Vojislav Lukovich, Bess Crider, Christabel Anderson, and the anonymous ONL Divine Liturgy drawings, via the Orthodox Arts Journal.
- **Fonts in the review pages only**: Alegreya and Source Sans 3 (SIL OFL), from `wwwroot/fonts/`.
- **Asset register**: no row is needed until Phase E; the badges are uploaded data, already covered by the "Rank badge images" row, which Phase E should update to "original vector badges, `docs/design/rank-badges/`".

**Akolouthos.** Greek ἀκόλουθος, "follower, attendant" (from κέλευθος, "path", with copulative ἀ-: "one on the same path"); English *acolyte* descends from it ([Wiktionary, citing Beekes 2010](https://en.wiktionary.org/wiki/%E1%BC%80%CE%BA%CF%8C%CE%BB%CE%BF%CF%85%CE%B8%CE%BF%CF%82)). As a Byzantine title it is first attested in the *Kletorologion* of 899 among the officers of the Vigla; from the early eleventh century it is an independent command tied to the foreign guard units, chiefly the Varangian Guard; Pseudo-Kodinos (mid-fourteenth century) still names the akolouthos as chief of the Varangians, "always in close attendance to the emperor" ([Wikipedia, "Akolouthos"](https://en.wikipedia.org/wiki/Akolouthos), citing A. Kazhdan (ed.), *The Oxford Dictionary of Byzantium*, 1991, p. 42, and R. Guilland, *Revue des études byzantines* 18, 1960). This supports what was recorded with Philip on 23/10/2026. The badge carries no claim beyond "the commander of the Guard who stood closest to the Emperor". The secondary sources were read online; the Oxford Dictionary of Byzantium itself was not consulted.

## 8. Tools

| Tool | Available? | Used? |
|---|---|---|
| Recraft | No: no tool or MCP server in this environment | No |
| SVG.new | No | No |
| Figma | The design plugin's Figma server is installed but **not authenticated** (only an authenticate tool is exposed) | No; the contact sheet serves the comparison |
| Any image-generation model | None available | No |
| Headless Chrome (installed) | Yes | Renders every PNG here |

Nothing was installed and nothing paid was used. What a paid tool could add: Recraft could generate raster or vector concepts quickly, but would still need hand redrawing for clean, legible 33 px vectors and lettering. Hand-authored SVG gives exact control at the sizes that matter, so no paid tool is recommended.

## 9. Decisions (confirmed by Philip, 25/10/2026)

Philip, 25/10/2026: "I'm happy with these, certainly as a MVP. We can always review them at a later date, but this is a good start." D1 approved; D2, D3, D5 and D6 agreed; D4 left as proposed (the chalice). The drafts as put to him follow.

- **D1. Approve the collection** as on the contact sheet: Recruit star, Guardsman helmet, Shield-bearer kite shield, Sword-bearer sword and Gospel, Herald trumpet, Torch-bearer candle, Standard-bearer labarum, Cross-bearer processional cross, Christ-bearer chalice, Akolouthos eagle.
- **D2. No rank names on the badges.** The rank name is always written beside the medallion, and lettering at 33 to 49 px is illegible. This changes the agreed Akolouthos patch ("AKOLOUTHOS around a stitched gold rim", 23/10/2026) for the on-screen badge only; a lettered version can be made for a real embroidered patch.
- **D3. Akolouthos in imperial purple** (`#4A2A74`) rather than the agreed lapis, to follow the brief's colour instruction and separate it from the Shield-bearer. Lapis (exploration B) remains ready if you prefer it.
- **D4. Christ-bearer shows the chalice, not a figure of Christ.** The existing patch depicts Christ; the brief asks that sacred figures not be generated, and a rank badge should not carry a depiction of Christ as a reward. If you want Him shown, the option is an authentic non-icon drawing (for example Anderson's OAJ "Christ the Saviour roundel"), used unmodified under OAJ's terms and credited; I do not recommend it for a badge.
- **D5. Phase E: let the badge fill the whole medallion.** Today the badge is shown at 76% inside a second coloured ring, so it appears at 33 px. Draft: when a rank has a badge, `RankMedallion` shows it at 100% with no extra ring or dashed stitch (the badge has its own rim); that is 44 px and 64 px, a third larger. Shown on the contact sheet as "option: badge fills".
- **D6. Phase E: update the rank colour tokens** (`.rank-1` … `.rank-10` in `app.css`) to the fields above, so the rings, pills, tints and hero bars match the badges. Each needs a contrast check for `--rank-on` text (saffron and ivory fields take ink text).

Not done in this phase, by instruction: replacing or uploading badges, changing `RankMedallion` or tokens, and inspecting the badges in the running application (Phase F).
