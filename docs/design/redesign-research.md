> Research for `docs/design/redesign-brief.md`, by a Sonnet research agent on 23/10/2026; reviewed by the main session. **Not adopted from this report:** recommendation 5 (age-banded "younger/older" modes and audio) contradicts the brief's single student design system and the out-of-scope list; "parchment" as a surface name is avoided because the brief rules out fake parchment (a warm off-white surface is still possible). Items marked unverified stay unverified. To do before shipping any Orthodox Illustration Project artwork: written confirmation from the Orthodox Arts Journal, especially on modification, filed with an asset register.

# Varangians UI/UX redesign: research report

Date: 23/10/2026. Method: web fetches and searches, plus direct queries to the Wikimedia Commons and Fontsource APIs. Items I could not verify are marked **UNVERIFIED**. Wikipedia and popular-history sources are used only for background and are flagged as secondary.

---

## 1. Orthodox Arts Journal: Orthodox Illustration Project

**What it is.** An online collection of graphic illustrations "of the highest quality" for printed Orthodox publications, aimed at book designers and graphic artists ([project page](https://orthodoxartsjournal.org/orthodox-illustration-project/)). It was announced in 2014 ([announcement](https://orthodoxartsjournal.org/announcing-the-orthodox-illustration-project/)). A web search of the announcement says about 150 graphics at launch and that some ornaments are scans edited from medieval Russian and other antique books ([search summary of the announcement](https://orthodoxartsjournal.org/announcing-the-orthodox-illustration-project/)). The collection has likely grown since; I could not count the current files.

**Who made it.** The attribution licence document names these artists (it says each known-author file carries the artist's surname in its filename): Scott Patrick O'Rourke, Christabel Anderson, Andrew Gould, Alvin Alexsi Currier, Fr. Silouan Justiniano, Lauren Pope, Bess Crider and Vojislav Lukovich. Divine Liturgy drawings marked "ONL" come from a 1970s Finnish Orthodox Youth Association colouring book whose artist could not be traced. Un-attributed images are "digital adaptations of scans from antique books or manuscripts which are anonymous and out of copyright". Source: the licence text below. Historical sources named in the announcement: Gladzor Gospels, a 1499 Venetian Greek book, 1581 Ostrog Bible, 1566 horologion, a Hilandar woodblock print, and others ([announcement](https://orthodoxartsjournal.org/announcing-the-orthodox-illustration-project/)). Contact for questions: Andrew Gould (address in the licence document).

**Asset categories** (six, each with a raster and a vector download folder) ([project page](https://orthodoxartsjournal.org/orthodox-illustration-project/)):
1. Bars, Headers, and Full-Page Ornaments
2. Borders
3. Churches & Village Life
4. Divine Liturgy
5. Icons
6. Small Ornaments

**File formats.** The page says raster is at "a high - yet limited - resolution" and vector scales without loss ([project page](https://orthodoxartsjournal.org/orthodox-illustration-project/)). The announcement asks contributors to scan at 300 dpi or higher and supply GIFs ([announcement](https://orthodoxartsjournal.org/announcing-the-orthodox-illustration-project/)). The public preview images are GIF. **UNVERIFIED:** the exact vector format (SVG, EPS or AI) and the per-folder file lists. The folders (e.g. [bhfpo-vector](https://orthodoxartsjournal.org/bhfpo-vector)) render their listings with JavaScript, so I could not enumerate the files. A human needs to open them in a browser.

### Licence: verbatim

The project page says only "Click here to read the attribution license", linking to goo.gl/fd0dzq. That short link redirects to a Google Docs file (`docs.google.com/file/d/0B1MLai552F5yZkJIQ3B0QWx3cDA`). I downloaded its plain text. It reads, in full for the usage terms:

> "Images are available free of charge for use in non-commercial publications such as parish bulletins, parish websites, church school materials, etc. For commercial and professional uses, such as published books that are printed for sale, and commercial websites, we ask for a voluntary donation to the Orthodox Arts Journal."
>
> "We also request that credit be given to the Orthodox Arts Journal and to the author of the image."
>
> "For any commercial usage, and for most usage of larger images (such as icons, illustrations of village life, and full-page ornaments), we ask that both the author of the image and the Orthodox Arts Journal be credited."
>
> "Attribution is not necessary for non-commercial use of minor marginal ornaments and informal uses such as coloring pages or parish bulletins for limited distribution."
>
> "In most cases, images with no author indicated are digital adaptations of scans from antique books or manuscripts which are anonymous and out of copyright. Our adaptations of these images are substantially original, and therefore within copyrights. We ask that use of them indicate the Orthodox Arts Journal as the source."

The announcement adds: "No users will be granted exclusive rights to use any image." ([announcement](https://orthodoxartsjournal.org/announcing-the-orthodox-illustration-project/)). The site footer reads "All Rights Reserved" ([about page](https://orthodoxartsjournal.org/about/)); it is a site-wide notice, not specific to the illustrations.

**Answers to your specific questions**

| Question | Finding |
|---|---|
| Non-commercial church charity web app permitted? | Very likely yes. "Parish websites" and "church school materials" are named explicitly. A catechesis app for a church charity fits the stated intent, but it is not a named case. Recommend a short email to confirm, and keep the reply on file. |
| Modification permitted? | **Not addressed.** Neither the licence document nor the announcement grants or forbids modification. No standard licence (e.g. Creative Commons) is named. Treat recolouring, scaling and cropping as probably fine in spirit but get written confirmation before altering the art itself (e.g. editing or merging motifs). |
| Sacred images (Christ, Theotokos, saints) allowed? | No restriction stated. The Icons category is explicitly part of the offering. Separate pastoral point for the design: use sacred images respectfully and not as decorative or interactive game elements. |
| Attribution | Required for credit-worthy uses: OAJ plus the artist for larger images; OAJ only for anonymous adaptations. Minor marginal ornaments in a non-commercial setting need none. A credits page is the simple route. |
| Commercial use | Voluntary donation requested. Not relevant unless the app is monetised. |

I could not find a machine-readable licence (no SPDX or CC identifier). Legally this is an informal permission statement rather than a formal licence, so it is revocable and carries ambiguity. Keep a dated copy of the licence text and the email reply in the repository.

### Visual language (from the preview images I inspected)

I viewed four preview GIFs from the page: an architectural frame by O'Rourke, a zigzag page border, a seraph, and Sts George and Demetrios.

- **Line and fill.** Pure black on white, hard-edged line art, no greys or colour. Solid black fills are used sparingly as counterweight (the seraph sits on a solid black oval). Pattern is built from dots, hatching and small repeated cells rather than shading.
- **Weight.** Medium-fine uniform contour weight with dense interior ornament; reads as pen-and-ink or woodcut. Suitable for 1-bit-style rendering.
- **Architecture.** The architectural frame is a Byzantine Π-shaped headpiece: a rectangular tympanum with a half-rosette lattice, a pair of peacocks facing a central lotus/palmette at the top, flared lily finials, and two carved columns with beaded shafts. The central panel is deliberately empty, which is the pattern for a title or content slot.
- **Borders.** The zigzag border is a narrow band of repeated triangular cells with square corner blocks, a rectangular frame for a whole page.
- **Typography relationship.** These are intended to sit around type in books (headers, bars, initials, page ornaments), with a plain central space for text. The announcement mentions Byzantine, Slavic and early-Romanesque calligraphic styles ([announcement](https://orthodoxartsjournal.org/announcing-the-orthodox-illustration-project/)).
- **Implication.** Strictly monochrome, so recolouring into a palette (via CSS `currentColor` on SVG, or a CSS mask on raster) is a natural token strategy. That is a modification question (see above) only if the art itself is altered; tinting is the lightest-touch approach.

### Assets that would suit

Named assets I could confirm exist (from previews and the announcement):
- Architectural frame by O'Rourke (Π-shaped headpiece with peacocks): page or section header.
- Zigzag border 1: card or page frame.
- Seraph: small decorative or "guardianship" motif.
- Village-church illustration by Currier ("Village-church-1-Currier"): church architecture.
- Divine-Liturgy illustration 25 (ONL): Liturgy-scene example.
- O'Rourke icons incl. Sts George and Demetrios (warrior saints: apt for a guard theme), Christ Enthroned, Transfiguration; Andrew Gould's Jerusalem Cross and full-page ornament; Christabel Anderson's Gladzor-derived ornament and iconographic rondel ([announcement](https://orthodoxartsjournal.org/announcing-the-orthodox-illustration-project/)).

**Double-headed eagle:** none confirmed. I found no reference to one in the pages I could read. **UNVERIFIED**, since I could not enumerate the folders. Use a Commons file instead (section 6).

Small ornaments, dividers and per-category contents beyond the above: **UNVERIFIED**; the folder listings must be browsed manually.

---

## 2. Iconography and image sources with clear licences

Rule of thumb: the underlying 2D reproduction of a public-domain work is generally treated as public domain in the US and as per-file in the UK; UK law is less settled for faithful photographic reproductions of 2D works. Always read the individual file page. I queried the Commons API, which returns the licence tag per file; tags are uploader-asserted.

| Source | What | Licence |
|---|---|---|
| The Met Open Access | Byzantine icons, textiles, ivories, enamels (where artwork is public domain) | CC0 for Open Access images; "more than 375,000 images" ([Met article](https://www.metmuseum.org/articles/open-access?rss=1), [Met perspectives](https://www.metmuseum.org/perspectives/open-access)). The current policy page returned HTTP 429 and a bot challenge to me, so I did not read the primary text; secondary summary says CC0, commercial use permitted. Check each object record for the "Open Access" tag. |
| Wikimedia Commons, Hagia Sophia Deesis mosaic | e.g. [File:Christ Pantocrator Deesis mosaic Hagia Sophia.jpg](https://commons.wikimedia.org/wiki/File:Christ_Pantocrator_Deesis_mosaic_Hagia_Sophia.jpg) | Tagged Public domain. Other Deesis photos are CC BY-SA 3.0 (avoid; share-alike). |
| Commons, Sinai Christ Pantocrator (6th century) | [File:CHRIST PANTOCRATOR-SINAI(6th Century).jpg](https://commons.wikimedia.org/wiki/File:CHRIST_PANTOCRATOR-SINAI(6th_Century).jpg) | Tagged Public domain (another version is CC BY 2.0). |
| Commons, Rublev Trinity | [Angelsatmamre-trinity-rublev-1410.jpg](https://commons.wikimedia.org/wiki/File:Angelsatmamre-trinity-rublev-1410.jpg) | Tagged Public domain. |
| Commons, Ravenna San Vitale (Justinian) | Several photos | CC BY-SA 3.0, CC BY 2.0, CC BY-SA 4.0 (attribution and, for some, share-alike obligations). Not recommended unless a CC BY file is chosen and credited. |
| Commons, Pala d'Oro cloisonné details | [Irina (Pala d'Oro).jpg](https://commons.wikimedia.org/wiki/File:Irina_(_Pala_d%27Oro).jpg) | Tagged Public domain. |
| Commons, Skylitzes Chronicle (Varangian scenes) | [A Thracesian woman kills a Varangian.jpg](https://commons.wikimedia.org/wiki/File:A_Thracesian_woman_kills_a_Varangian.jpg) | Tagged Public domain. |
| Commons, Walters Art Museum Byzantine gospel bindings | [Walters W.526 upper board](https://commons.wikimedia.org/wiki/File:Gospels,_Binding,_Walters_Manuscript_W.526,_Upper_board_outside.jpg) | Tagged CC0. |
| British Library | Lindisfarne Gospels (Cotton MS Nero D IV) and Benedictional of St Æthelwold (Add MS 49598) | **UNVERIFIED.** Searches did not surface the BL's own reuse terms for Nero D IV. One Commons copy of a Benedictional page ([Entry into Jerusalem, f.45v](https://commons.wikimedia.org/wiki/File:The_Entry_into_Jerusalem_-_Benedictional_of_St._Aethelwold_(971-984),_f.45v_-_BL_Add_MS_49598.jpg)) is tagged CC0; several others are tagged Public domain. Prefer these Commons-tagged files, and verify the BL record. |

Cautions:
- A photograph of a three-dimensional object (e.g. Sutton Hoo, Staffordshire Hoard) carries its own copyright; most of the Commons photographs I found are CC BY or CC BY-SA, not PD (section 3).
- **Living iconographers' work and modern icon prints are copyright.** Do not lift images from icon shops or church websites.
- Pastoral note: for a children's catechesis app, a limited set of canonical, recognisable icons (public-domain Pantocrator, Rublev Trinity) is better than many; consult the clergy over any use in interactive or game contexts.

---

## 3. Visual vocabulary references

### 3.1 Anglo-Saxon and insular

**Lindisfarne Gospels** (c. 715-720 per the British Library; shelfmark Cotton MS Nero D.IV) ([Wikipedia, secondary](https://en.wikipedia.org/wiki/Lindisfarne_Gospels)).
- Carpet pages: pure decorative cross designs with geometric ornament and metalwork-inspired motifs.
- Interlace of birds and animals, spirals, knotwork; large incipit letters; one page has about 10,600 red dots as outline and background texture.
- Palette: indigo (not lapis, according to 2000s Raman analysis), red lead, a little gold, and a dense dark brown-black carbon ink; egg-white binder. Eadfrith knew of lapis but could not obtain it.
- Abstractable traits: modular grid with a central cross, repeating bands, dotted contours, dense pattern within clear frames, strong black outline.

**Book of Durrow** (7th century): carpet pages with interlace and spiral fields. Several Commons files are tagged Public domain ([e.g. BookDurrowCarpetPage.jpg](https://commons.wikimedia.org/wiki/File:BookDurrowCarpetPage.jpg)). Palette not verified by me.

**Sutton Hoo** (c. 625): purse lid with cloisonné garnet and glass cells, gold, birds of prey. Commons photographs are CC BY-SA ([e.g. Sutton hoo purse lid](https://commons.wikimedia.org/wiki/File:Sutton_hoo_purse_lid02017.JPG)). Traits: cell-based geometry, garnet red against gold, symmetrical animal pairs. The garnet-and-gold palette is a general art-historical fact, but I did not verify it from a primary source in this session.

**Staffordshire Hoard**: gold filigree and garnet cloisonné, animal interlace (Portable Antiquities Scheme photos on Commons are CC BY or CC BY-SA, e.g. [pommel cap fragment](https://commons.wikimedia.org/wiki/File:Staffordshire_hoard_pommel_cap_fragment_(FindID_458325).jpg)). Use as inspiration only; abstract the patterns, do not reuse photographs.

**Benedictional of St Æthelwold** (c. 971-984, Winchester school): architectural frames with acanthus leaf borders, gold, rich colour. Several Commons files are tagged Public domain or CC0 (section 2). The Winchester style is an English bridge to Byzantine influence; its leafy frame borders suit page/card frames. Colour specifics: **UNVERIFIED** in this session.

### 3.2 Byzantine

- **Mosaics (Hagia Sophia etc.).** Gold-leaf-in-glass tesserae and coloured glass smalti. Research summarised by search: pale transparent blues and greens were easy to make; opaque yellows, dark blues and reds were harder; red glass was expensive; gold was applied by layering leaf between glass ([sources via search](https://research.unipd.it/handle/11577/3465168) and [Ifao glass database](https://www.ifao.egnet.net/bases/verre/?id=654)). A 2022 study of 5th-6th century tesserae from Tyana groups colours as blue, green, yellow, red, black, gold. I did not read the papers in full.
- **Headpieces.** The OAJ frame in section 1 is the model: rectangular or Π-shaped arch over a title, with peacocks, palmettes, rosettes and columns. Strong symmetry and a clear empty central field.
- **Cloisonné enamel.** Metal cell walls (cloisons) filled with coloured enamel, gold lines separating colour fields (e.g. Pala d'Oro details, section 2). I did not find a primary source on the enamel palette in this session. **UNVERIFIED.**
- **Palaiologan double-headed eagle.** See section 6.

### 3.3 Historic Varangian Guard (facts for copy and imagery)

From the Wikipedia article ([secondary](https://en.wikipedia.org/wiki/Varangian_Guard)) and a search of popular history sources:
- After 1066 the Guard received many Anglo-Saxons. A large arrival (reported as 5,000+ in 235 ships) is dated 1088; the number, the Alexios attribution and the Crimean "New England" colony rest largely on late saga evidence and are debated. One account argues the emperor in question was more likely Michael VII; treat this as contested, not settled.
- Better-supported contemporary evidence: Anna Komnene (Alexiad) and John Kinnamos; Jonathan Shepard is quoted as saying that "There are so many references outside the saga to English soldiers in the Varangian Guard that we know many must have made their way to Byzantium"; seals of two twelfth-century Byzantine interpreters name them as English translators ([The New European, "English by the Black Sea"](https://www.theneweuropean.co.uk/english-by-the-black-sea/)).
- Role: imperial bodyguard who swore oaths of loyalty; their characteristic weapon was the broad axe; Anna Komnene says they regard loyalty to the emperors "as a family tradition". The Guard was led by the *akolouthos* (hence the top rank in your ladder). The Wikipedia article also notes this loyalty was not always perfect (e.g. 1071, 1078), so avoid absolute claims in copy.
- The Guard persisted to Constantinople's fall in 1453 per the article, with last-mention and numbers varying by source.
- Design note: the weapon is an axe, not a sword. Your rank names (Shield-bearer, Sword-bearer) are an in-app ladder; imagery based on historical equipment (axe, round or kite shield) is fine but keep it subordinate to the Orthodox, liturgical identity. Avoid Norse horned-helm and rune imagery.

### 3.4 Palette (tokens)

Historic sources, with confidence:
- Verified in this session: indigo blue, red lead, gold, dark carbon-brown ink (Lindisfarne); glass-based blues, greens, reds, blacks and gold (Byzantine tesserae).
- **UNVERIFIED** here: vermilion, malachite green, porphyry purple as named pigments or stones. They are standard iconographic and manuscript terms, but I did not verify them from a cited source. Treat the following as a design suggestion, not a historical claim: deep indigo/ultramarine, red-lead/vermilion, verdigris or malachite green, burnished gold, porphyry purple, parchment cream, carbon ink.
- Accessibility: gold on cream or white fails contrast for text; use gold only for ornament, rules and large non-text elements, and use dark ink for text.

---

## 4. Khan Academy and BBC Bitesize UX

### 4.1 Khan Academy (transferable principles)

I could not fetch Khan's own pages directly (help-centre fetches were blocked by bot protection). The mastery model below comes from a search summary of the Khan help centre article ([How do Khan Academy's Mastery levels work?](https://support.khanacademy.org/hc/tr/articles/5548760867853)); verify against the article before copying wording.
- Five named levels plus "Not Started": Attempted (<70%), Familiar (70-99%), Proficient (100%), Mastered (needs a later mixed-skill check); levels can fall back after misses. A related article says moving to Familiar earns 50 of 100 mastery points, Proficient 80, Mastered 100 ([self-paced Mastery](https://support.khanacademy.org/hc/da/articles/360007253831-What-is-self-paced-Mastery)).
- Course and unit progress are aggregates of skill-level states. Goals can be set at unit level (short-term) or course level (long-term) ([Unit/Course Mastery Goals](https://support.khanacademy.org/hc/en-us/articles/360030694212)).
- Anecdotal (forum): topics "light up" as earlier ones are mastered, but learners can skip ahead. Unit map layout details: **UNVERIFIED**.

Transferable principles:
1. Name progress states in plain words and make them reversible (spaced retrieval) rather than a single "done" tick. For catechesis, the equivalent is progress as formation (seen, practised, can explain) rather than quiz-score gating.
2. Two time horizons: a unit and a whole course.
3. Never lock content; guide, do not gate.
4. Keep the state vocabulary small and consistent across every screen.

### 4.2 BBC Bitesize

Secondary sources only; the design write-ups (uxdesign.cc, Design Week) were blocked (HTTP 403) when I fetched them, so this is from search summaries: the 2018 redesign served three audiences (Primary, Secondary, Post-16) while keeping one brand, with typography, illustration, colour and tone shifting by phase; BBC Reith replaced Gill Sans; orange was kept ([Design Week](https://www.designweek.co.uk/issues/17-23-september-2018/bbc-bitesize-redesigns-attract-kids-teenagers-all-ages/), [uxdesign.cc](https://uxdesign.cc/rebranding-bbc-bitesize-how-to-keep-your-cool-as-a-ux-designer-f73e895e226e)). Treat as unverified claims. The "80% of secondary and 40% of primary students" figure is from one blog and should not be quoted.

Transferable principle: one identity with age-banded presentation. For ages 8-18, consider two density modes (younger: larger type, more illustration, fewer choices; older: denser, more text) from one token set.

### 4.3 BBC GEL (primary, fetched with curl; the standard fetch tool was blocked)

**Typography** ([GEL typography](https://www.bbc.co.uk/gel/features/typography)): four size groups by width and input (0-319, 320-599, 600+, and 600+ without touch). Body copy 15/20, 16/22 (320-599), 18/24 (600+ touch), 16/22 (desktop) px/line-height. Titles 20/24 to 36/40 (article title); use relative units "to allow for the changing nature of the user's browser"; do not adjust the tracking of Reith; for Helvetica, apply -3% only to bold above 18px. Heavier weights for headlines, titles and links, "anything interactive or that needs to shout". Line-length guidance not found on that page.

**Touch targets** ([GEL touch](https://www.bbc.co.uk/gel/features/how-to-design-for-touch)): recommended 44px (minimum 32px) at 320-599 and 600+ touch; 32px (minimum 24px) for non-touch. Physical sizes: 7mm recommended, 5mm absolute minimum; smaller targets need a 7mm exclusion zone. Design "touch-first"; avoid hover reliance "at all costs"; every complex gesture (drag and drop, pinch) needs a simple tap fallback; provide touch and hover states (style `touchstart`).

**Accessibility** ([GEL accessibility](https://www.bbc.co.uk/gel/features/how-to-design-for-accessibility)): "Use visible focus, hover or touch states"; consider focus order and how it is indicated; consider colour contrast and "whether any meaning is implied by colour alone"; give users control (do not suppress pinch zoom or force orientation); test with people with cognitive, motor, hearing or vision impairments; 18% of the UK population is impaired in some way. I did not find numeric contrast ratios or a focus-ring spec in the GEL pages (the dedicated focus and colour URLs I tried returned errors). Use WCAG 2.2 AA numbers for those.

**Designing for children** ([GEL children](https://www.bbc.co.uk/gel/features/how-to-design-for-children-2)), seven pillars: positive feedback and moments of joy (incorrect answers get encouraging, not demoralising, animation); be visual and reduce text, with audio alternatives; make the goal immediately clear (show where they are and what to do next); be heard (audio feedback); animate with personality; character-led experiences; forgiving design (e.g. snapping for puzzles) for varying motor skill. The research also notes that from about age 12 children interact much like adults, so the guidance targets 12 and under.

Other GEL patterns worth reading for the build: [Cards, Tabs, Filter, Bottom navigation, Inputs, Games framework](https://www.bbc.co.uk/gel/guidelines/category/design-patterns); [how to design forms](https://www.bbc.co.uk/gel/guidelines/category/how-to).

**Responsive.** Start single-column, enhance up; breakpoints where content needs them ([GEL web](https://www.bbc.co.uk/gel/features/how-to-design-for-the-web)).

---

## 5. Typefaces

All sizes below are Fontsource-distributed WOFF2 file sizes measured by HTTP download from jsDelivr (`@fontsource/*` and `@fontsource-variable/*`) on 23/10/2026. These are per-subset files, not the full family. Self-host by downloading these files (or the Google Fonts GitHub sources) and committing them; do not link to the CDN at runtime. Subset coverage and licence come from the [Fontsource API](https://api.fontsource.org/v1/fonts/alegreya). Every font below is reported as OFL-1.1.

### (a) Display and headings

| Font | Greek? | Variable? | WOFF2 (Latin / Greek, regular weight) | Notes |
|---|---|---|---|---|
| **Alegreya** | Yes (greek, greek-ext) | Yes (wght 400-900) | static 23 KB / 8 KB; variable 43 KB / 15 KB | Calligraphic, humanist book face with a manuscript feel; italic is attractive. Strongest "scribal but legible" candidate with real Greek. |
| **EB Garamond** | Yes | Yes (400-800) | static 24 KB / 11 KB; variable 44 KB / 20 KB | Classical, restrained, and very legible. Less distinctive; best for lengthy texts. |
| **Literata** | Yes | Yes (200-900) | static 20 KB / 8 KB; variable 52 KB / 19 KB | Designed for on-screen reading; warm and sturdy. |
| **Source Serif 4** | Yes | Yes (200-900) | static 20 KB / 8 KB; variable 51 KB / 20 KB | Neutral, excellent; optical sizes. |
| **Noto Serif** | Yes | Yes (100-900) | static 14 KB / 9 KB; variable 37 KB / 28 KB | Safe, broad coverage, plain. |
| **GFS Didot** | Yes | No (400 only) | 15 KB / 7 KB | Greek-first Didone; high contrast, hairlines get thin on small screens. Single weight. |
| **GFS Neohellenic** | Yes | No (400, 700) | 24 KB / 11 KB | Greek-first, plain and sturdy. |
| Cormorant / Cormorant Garamond | **No** (cyrillic, latin only) | Yes | 23 KB Latin | Elegant but fine hairlines and no Greek. Reject for this brief. |
| Cinzel | **No** | Yes | 14 KB Latin | Roman capitals only in effect; strongly associated with "epic/fantasy/film poster"; **cliché warning** and no Greek. Do not use as the main heading face. |
| Fraunces | **No** | Yes | 18 KB Latin | Characterful "soft serif"; no Greek, and its quirkiness reads more modern than Byzantine. |

Recommendation: **Alegreya** for headings (Latin and Greek both included; calligraphic roots echo manuscripts without costume). Alternative: Literata or EB Garamond if a calmer tone is wanted. If Greek words must also appear in the body, both display and body fonts need Greek subsets or the Greek glyphs will fall back to a system font with mismatched weight.

### (b) Body and UI

| Font | Greek? | Variable? | WOFF2 (Latin / Greek) | Licence | Dyslexia notes |
|---|---|---|---|---|---|
| **Atkinson Hyperlegible Next** | **UNVERIFIED / likely no** (Fontsource lists latin, latin-ext only; the vendor claims 150+ languages but Greek is not confirmed in anything I found) | Yes (200-800) | variable 34 KB Latin; static ~12 KB | OFL 1.1 (GitHub repository `googlefonts/atkinson-hyperlegible-next`) | Designed by the Braille Institute for low-vision legibility with distinct letterforms ([press summary](https://ophthalmologymanagement.com/news/2025/applied-design-and-braille-institute-partner-to-launch-typeface-for-making-reading-easier/)). |
| Atkinson Hyperlegible (original) | No | No (400, 700) | 17 KB Latin | OFL 1.1 | As above; 27 languages. |
| **Lexend** | **No** (latin, latin-ext, vietnamese) | Yes | variable 40 KB Latin; static 14 KB | OFL 1.1 | Designed to improve reading fluency; evidence on dyslexia benefits is mixed. I did not verify a study in this session, so do not claim it as a dyslexia fix. |
| **Source Sans 3** | Yes | Yes (200-900) | variable 29 KB / 15 KB; static 16 KB / 8 KB | OFL 1.1 | Clean, neutral humanist sans; good small-size legibility. |
| **Noto Sans** | Yes | Yes (100-900) | variable 36 KB / 22 KB; static 13 KB / 8 KB | OFL 1.1 | Plain, broadest coverage. |
| **Inter** | Yes | Yes (100-900) | variable 48 KB / 19 KB; static 24 KB / 8 KB | OFL 1.1 | Excellent screen UI; slightly cold and generic. |

Dyslexia considerations: I did not verify specific evidence in this session. Practical, widely recommended behaviour (confirm against the British Dyslexia Association style guide if you need a citation): left-aligned text, 16px or larger body, generous line height (about 1.5), avoid italics and all-caps for long text, and give users a text-size setting. Do not claim that any font "cures" dyslexia.

Recommendation: **Source Sans 3** (variable, Latin + Greek, about 29 KB + 15 KB) as the single body/UI face. If you want the "hyperlegible" distinct-letterform quality in Latin text, Atkinson Hyperlegible Next is a candidate for Latin-only UI, but Greek words would fall back; weigh that against Source Sans 3. Subset budget: one variable heading file plus one variable body file, Latin and Greek, is roughly 120-150 KB WOFF2 total, which is small for a self-hosted Blazor app.

Download sources: `npm` packages are unavailable to you (no npm), but the files are plain WOFF2 on jsDelivr at paths such as `https://cdn.jsdelivr.net/npm/@fontsource-variable/source-sans-3/files/source-sans-3-greek-wght-normal.woff2`; download once with `curl` and commit. Alternatively use the Google Fonts GitHub repository `google/fonts` (OFL fonts) for the source TTFs and convert with `woff2_compress`. Keep each font's `OFL.txt` in the repository.

---

## 6. Double-headed eagle

**History.** Secondary source (Wikipedia, so verify before quoting): the motif is Bronze Age in origin (Hittite and Mesopotamian), reappears in medieval art around the 10th-11th centuries, and became a Byzantine imperial emblem only in the final century of the Palaiologos dynasty. Early Byzantium used a single-headed eagle. The Western sources call it a Byzantine state emblem from at least the 15th century ([Double-headed eagle](https://en.wikipedia.org/wiki/Double-headed_eagle)). A Palaiologan eagle on a 15th-century miniature is on Commons. A popular source says Byzantium "certainly used" it after 1261 under the Palaiologoi ([search summary](https://www.medievalists.net/2021/04/double-headed-eagle-byzantine/)); the date conflicts with the Wikipedia wording, so I would say only "late Byzantine, Palaiologan period".

**Heraldic conventions** (same Wikipedia article): medieval Serbia used a white eagle on red; the Kastrioti (Albania) a black eagle on red; Holy Roman eagles were often haloed; Russia's imperial eagle was black and the current one gold. I did not verify a standard gold-on-red Palaiologan tincture from a primary source (the article does not specify). Common modern depictions are gold or black on red or yellow; verify if historical accuracy matters.

**Public-domain and permissive vector renderings on Commons** (licence tags per the Commons API; verify on each file page):
- [Palaiologos-Dynasty-Eagle.svg](https://commons.wikimedia.org/wiki/File:Palaiologos-Dynasty-Eagle.svg): Public domain.
- [Double-headed eagle of the Greek Orthodox Church.svg](https://commons.wikimedia.org/wiki/File:Double-headed_eagle_of_the_Greek_Orthodox_Church.svg): Public domain (but see the cautions below; this is the Church of Greece flag device).
- [CoA of Byzantine Empire.svg](https://commons.wikimedia.org/wiki/File:CoA_of_Byzantine_Empire.svg): CC0.
- [Byzantine Palaiologos Eagle.svg](https://commons.wikimedia.org/wiki/File:Byzantine_Palaiologos_Eagle.svg): **CC BY-SA 4.0**, share-alike. Avoid.
- [Device of the Palaiologos Dynasty.svg](https://commons.wikimedia.org/wiki/File:Device_of_the_Palaiologos_Dynasty.svg): CC BY-SA 3.0. Avoid.
- Dragases (a Commons contributor) has released several heraldic SVGs of Byzantine families into the public domain (e.g. [Kantakouzinos-Arms.svg](https://commons.wikimedia.org/wiki/File:Kantakouzinos-Arms.svg)).

Because the eagle is such a simple shape, you can also redraw your own geometric version; a motif of this age cannot be owned, only the particular artwork.

**Cautions and sensitivities**
- It is a current state or institutional emblem of Albania, Serbia, Montenegro and Russia, and of the Greek Orthodox Church and the Orthodox Autocephalous Church of Albania; also used by the Hellenic Army General Staff, Turkey's General Directorate of Security, Scottish Rite Freemasonry, and some football clubs (AEK, PAOK) ([Wikipedia](https://en.wikipedia.org/wiki/Double-headed_eagle)). A user of Serbian, Albanian, Greek or Russian heritage may read it as national. Ownership of the symbol is contested: Albanian and Greek commentators argue over whether Albania's eagle is Byzantine ([Protothema](https://origin01-en.protothema.gr/2021/06/27/albanian-university-professor-the-double-headed-eagle-on-our-flag-is-byzantine-it-does-not-represent-us)). Russian passports in 1997 drew objections in the North Caucasus as an imperial symbol ([RFE/RL, via search](https://rferl.org/a/1087045.html)).
- **I could not verify** claims of use by specific modern nationalist or far-right groups: the searches returned nothing reliable on it. Do not assert it either way; if it matters, ask a specialist (e.g. the parish priest or a historian of heraldry) and do a current search.
- Design guidance: use it, if at all, as a quiet historical ornament (a small seal or watermark) and not as the app's logo; avoid pairing with flags, national colours, or military framing. The app serves English-born children, so a cross-and-shield or axe-and-cross device is a safer primary mark than an emblem that reads as "Greek" or "Serbian" or "Russian".

---

## Recommendations for the design system

1. **Anchor the look in the OAJ Byzantine headpiece idea, not Norse or fantasy.** One frame component (Π-shaped header with a clear empty title slot) plus one border and a few small dividers. Monochrome line art tinted by CSS token. Get written confirmation of the licence, including modification, before shipping.
2. **Palette tokens:** deep indigo (primary), red lead/vermilion (accent), verdigris green (success), burnished gold (ornament only), porphyry purple (rank/prestige), parchment (surface), carbon-brown ink (text). Check gold-on-parchment and all text pairs against WCAG 2.2 AA.
3. **Type:** Alegreya (headings, Latin + Greek) and Source Sans 3 (body/UI, Latin + Greek), both variable and self-hosted, about 120-150 KB for all four files. Avoid Cinzel, Cormorant and Fraunces (no Greek; Cinzel is cliché).
4. **Progress model:** a small, named, reversible state vocabulary (as in Khan's levels), tied to the ten-rank ladder as the long-term horizon and units as the short-term horizon. Present rank as service and formation, not a points leaderboard.
5. **Age-banded density from one token set:** younger mode with larger type, fewer choices, more illustration and audio; older mode denser and more text-forward (the Bitesize approach).
6. **GEL-derived interaction rules:** 44px touch targets (32px minimum, with exclusion zones), no hover-only affordances, a tap fallback for every gesture, visible focus on every control, no meaning by colour alone, relative units, and encouraging (never punishing) feedback.
7. **Sacred imagery:** a short curated set of public-domain canonical icons (Commons PD files above), shown respectfully and never as game pieces; clergy sign-off.
8. **Eagle:** optional, small, self-drawn or CC0/PD; not the logo.
9. **Record provenance:** keep a `docs/` asset register with source URL, licence text, date retrieved and attribution wording for every image and font; add a credits page.

---

## Licence summary table

| Asset / source | Licence | Attribution text required | OK for this use? |
|---|---|---|---|
| OAJ Orthodox Illustration Project (named-artist images) | Informal permission: free for non-commercial use incl. parish websites and church school materials ([licence text](https://docs.google.com/file/d/0B1MLai552F5yZkJIQ3B0QWx3cDA/view)); no named licence | Credit OAJ and the artist for larger images (icons, full-page ornaments); not needed for minor marginal ornaments in non-commercial use | Likely Y; confirm by email, especially for modification |
| OAJ (anonymous adaptations of antique scans) | Same terms | "Indicate the Orthodox Arts Journal as the source" | Likely Y |
| OAJ modification of artwork | Not addressed | n/a | Unclear |
| The Met Open Access images | CC0 ([Met](https://www.metmuseum.org/articles/open-access?rss=1)) | None (credit courteous) | Y (per object tag; primary policy page not read by me) |
| Commons: Hagia Sophia Deesis (PD-tagged file), Sinai Pantocrator (PD-tagged), Rublev Trinity (PD-tagged), Pala d'Oro (PD-tagged), Skylitzes (PD-tagged) | Public domain as tagged | None; courtesy credit | Y (verify each file page) |
| Commons: CC BY / CC BY-SA photographs (Ravenna mosaics, Sutton Hoo, Staffordshire Hoard) | CC BY 2.0 / CC BY-SA 2.0-4.0 | Author, licence, link; share-alike for derivatives | Unclear; avoid, use as inspiration only |
| Commons: Walters Byzantine bindings | CC0 | None | Y |
| Commons: Benedictional of St Æthelwold f.45v (CC0), other pages (PD-tagged) | CC0 / PD as tagged | None | Y (verify) |
| British Library digitisation (Lindisfarne etc.) | **UNVERIFIED** | Unknown | Unclear; prefer Commons-tagged copies |
| Commons: Palaiologos-Dynasty-Eagle.svg, Greek Orthodox Church eagle SVG | Public domain as tagged | None | Y technically; see sensitivity cautions |
| Commons: CoA of Byzantine Empire.svg | CC0 | None | Y |
| Commons: Byzantine Palaiologos Eagle.svg; Device of the Palaiologos Dynasty.svg | CC BY-SA 4.0 / 3.0 | Author + share-alike | N (avoid) |
| Alegreya, EB Garamond, Literata, Source Serif 4, Noto Serif/Sans, Source Sans 3, Inter, GFS Didot/Neohellenic, Atkinson Hyperlegible (Next), Lexend | SIL OFL 1.1 ([Fontsource API](https://api.fontsource.org/v1/fonts/alegreya)) | Keep copyright and OFL text with the fonts; do not sell the font alone; renaming rule applies to modified versions | Y |
| Cormorant, Cinzel, Fraunces | SIL OFL 1.1 | As above | Licence Y; design N (no Greek, Cinzel cliché) |
| BBC GEL guidance (reference only) | BBC site terms; no assets copied | Cite when quoting | Y for ideas (do not copy BBC fonts or assets) |
