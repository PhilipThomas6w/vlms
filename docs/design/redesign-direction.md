# UI/UX redesign: design direction

Status: **proposed**, 23/10/2026. Phase 3 of `docs/design/redesign-brief.md`. To be judged by Philip at the prototype approval gate, together with the two working prototypes. Inputs: `redesign-discovery.md` (audit), `redesign-research.md`, and the embroidered rank patches in `seed/admin/Illustration of Badges.jpg`.

## 1. Concept: from the Guard to the Sanctuary

The rank ladder already tells a story. Read in order, the ten ranks move from **guarding** to **serving at the altar**:

| Movement | Ranks | Meaning in the design |
|---|---|---|
| The Guard | Recruit, Guardsman, Shield-bearer, Sword-bearer | Joining and being equipped. Ephesians 6 gives the Orthodox reading: the shield of faith, the sword of the Spirit, which is the word of God. |
| The Herald | Herald | Turning outward: announcing what has been learnt. |
| The Procession | Torch-bearer, Standard-bearer, Cross-bearer | The servers' roles in a liturgical procession: candle, banner, cross. |
| The Sanctuary | Christ-bearer, Akolouthos | Bearing Christ, and serving at the altar (*akolouthos*, follower, the Byzantine title for an attendant). |

The Varangian Guard escorted the Emperor, defender of the Faith, in procession to the Great Church. So the identity's spine is a **procession**: the student moves from the palace gate into the Church's service. This keeps the order the brief sets: Orthodox purpose first, Varangian story as the frame, with no militarism. Weapons appear only where the rank names them, and then as Ephesians 6 does.

Alternatives considered and set aside:

- **A journey map from England to Constantinople.** Strong history, but it reads as a game level map, and the destination of formation is the Church, not a city.
- **The Ladder of Divine Ascent.** Deeply Orthodox, but its imagery (monks falling from the ladder) is heavy for eight-year-olds, and it is an icon, not a motif to borrow.
- **A mosaic that fills in as lessons are completed.** Kept, but as the *texture* of progress, not the whole concept. A sacred image must never be the reward being "filled in", so the mosaic is abstract.

## 2. Influences: what each contributes to the design language

- **Orthodox (primary):** the Golgotha cross with IC XC NIKA, already the group's emblem on its Varangian Guard patch, becomes the product mark. Sacred imagery appears only as authentic icons, in lesson content and devotional contexts, never as badges, buttons or progress. Feasts, Scripture and prayer are presented with typographic dignity (quotation and Scripture blocks), not decoration.
- **Byzantine:** the **tessera**. Progress is shown as small square tiles, one per lesson, that fill with the rank's colour and a gold edge: progress without points. Lapis and gold grounds, from mosaic, for the shell and the summit rank. The Π-shaped manuscript **headpiece** (studied in the Orthodox Illustration Project) becomes the frame of the student's rank header: a simple architectural frame with an open title slot, drawn by us.
- **Anglo-Saxon:** **embroidery and interlace**. English embroidery was famous across Europe (*opus anglicanum*; the Bayeux embroidery is widely thought to be English work), and the group's ranks are embroidered patches the children wear. Rank medallions therefore have a **stitched rim**, a dashed inner ring echoing the patches' merrowed edge. A simple interlace band, drawn by us, is the one divider ornament.
- **Varangian:** the procession, the rank names, the patches' colours, and the language of belonging ("Your rank", "The way ahead") in the student experience only. Staff and legal language stays plain.

## 3. Colour

Built from the patches the children actually wear, then tuned for WCAG 2.2 AA (ratios computed and listed below).

**Keep blue and gold, deliberately.** They are on almost every patch's rim, and they are the mosaic ground of Byzantine churches. In the rank ladder they are the **destination**: Akolouthos, the last rank, is lapis and gold. A child's journey ends in the group's own colours.

| Token | Hex | Use | Contrast |
|---|---|---|---|
| `--lapis-900` | `#14254F` | Shell bar, headings on light, summit rank | 14.9:1 on white |
| `--lapis-700` | `#1F45A3` | Primary buttons, links | 8.6:1 on white |
| `--gold-500` | `#E2B33C` | Ornament, tesserae edges, accents on lapis | 7.6:1 on lapis-900; **never text on white** (1.95:1) |
| `--gold-700` | `#8A6410` | Gold when it must be text on light | 5.4:1 |
| `--ink` | `#172033` | Body text | 16.3:1 |
| `--ink-soft` | `#4A5568` | Secondary text | 7.5:1 |
| `--paper` | `#F7F8FA` | Page ground (cool marble white, not cream) | |
| `--line` | `#C9D1DD` | Hairlines (decorative only; form borders use `--ink-soft`, 3:1+) | |

Semantic: success `#1B6E46`, warning text `#7A4D00` on `#FFF4D6`, danger `#A4262C` (kept from today's error styling), info `--lapis-700`. Meaning is never carried by colour alone: every state has a word and an icon.

**Rank colours** (field = strong colour; tint = light ground; on-field = text colour on the field):

| Rank | Patch it comes from | Field | On-field | Tint |
|---|---|---|---|---|
| Recruit | Varangian Guard (gold ground) | `#E2B33C` | ink | `#FDF3D1` |
| Guardsman | Varangian Guard (red cross) | `#B3261E` | white (6.5:1) | `#FBE4E1` |
| Shield-bearer | navy and gold | `#1E2F8F` | white (11.3:1) | `#E3E7F8` |
| Sword-bearer | green | `#1C7A3E` | white (5.4:1) | `#DFF3E6` |
| Herald | sky blue | `#1A6FA8` | white (5.4:1) | `#DDEFFA` |
| Torch-bearer | black with flame | `#2A211C` (flame accent `#E8590C`) | white | `#FDE6D8` |
| Standard-bearer | lime | `#4D7C0F` | white (5.0:1) | `#ECF7D9` |
| Cross-bearer | navy and yellow | `#233B8C` | white (10.1:1) | `#FFF3C4` |
| Christ-bearer | sand and red | `#9B2C2C` | white (7.5:1) | `#F6E9D6` |
| Akolouthos | none yet: blue and gold | `#14254F` (gold accent) | white (14.9:1) | `#F7EBC8` |

**Intensity by audience** (one palette, different doses):

- **Student, vivid:** rank field colour on the hero, gold, tesserae, the headpiece frame, the interlace band.
- **Parent, warm:** each child's rank colour as a band and medallion; otherwise paper and ink.
- **Teacher, focused:** paper and ink, lapis actions; rank colour only as small pills identifying a student's rank.
- **Safeguarding and Admin, quiet:** paper and ink, lapis actions, no ornament, no rank colour except pills.

## 4. Typography

- **Alegreya** (Juan Pablo del Peral, SIL OFL) for headings and the wordmark: a calligraphic book face with the rhythm of a scribe's hand, Latin and Greek (IC XC NIKA, liturgical words), legible at large sizes, not a "medieval" costume face. Used from 1.25 rem up only.
- **Source Sans 3** (Adobe, SIL OFL) for body, forms and data: open apertures, a large x-height and tabular figures for tables, Latin and Greek.
- Both self-hosted as variable WOFF2 under `wwwroot/fonts/` with their OFL licences: no CDN, so no third-party request from a children's app.
- Scale (1.25 ratio, base 1 rem = 16 px, body 1.0625 rem on student and parent pages): 0.875 / 1 / 1.25 / 1.563 / 1.953 / 2.441 / 3.052 rem. Line height 1.55 body, 1.15 display. Measure capped at about 68 characters for reading text.
- Sentence case throughout. No all-caps labels, no letter-spaced eyebrows.

## 5. Layout and components

- **Shell:** a lapis bar with the cross mark and "Varangians", the current person's name, and an account menu. Navigation is grouped by area (Learn, Family, Teach, Curriculum, Safeguarding, Administration), showing only the areas the person holds. At 1024 px and wider, staff get a left sidebar; below that, the navigation collapses into a menu button (native `<details>`, no JavaScript). Students and parents, with few destinations, get them in the bar.
- **Spacing:** a 4 px base, 8 px rhythm (4, 8, 12, 16, 24, 32, 48, 64). Content width capped at 72 rem for app pages, 44 rem for reading text.
- **Radius philosophy:** square-ish. Tesserae are square (2 px), controls 6 px, panels 10 px, medallions round. Hierarchy shows in radius, not one radius everywhere.
- **Surfaces:** paper ground, white panels with a 1 px line, elevation by border first; one soft shadow only for overlays.
- **Targets:** 44 px for primary controls and list rows (BBC GEL), never under 24 px (WCAG 2.5.8).
- **Focus:** a 3 px gold-and-lapis double ring on every control, never removed.
- **Shared Razor components** (`Components/Ui/`): `PageHeader`, `Notice`, `StatusPill`, `RankMedallion`, `TesseraBar`, `RankPill`, `EmptyState`, plus CSS classes for buttons, fields, data tables (stacking to rows below 640 px), and a `.band` ornament.
- **Motion:** one moment only. When a student's page loads, the newest tessera settles into place once. Status changes elsewhere use a short fade. `prefers-reduced-motion` removes both.

## 6. Ornament and imagery

- **Ornament:** two original SVGs, drawn for this project and non-sacred: the interlace band and the headpiece frame. Plus the stitched rim (CSS). Nothing else. Never behind text or controls; absent on Safeguarding and Admin pages.
- **Product mark:** the Golgotha cross with IC XC NIKA from the Varangian Guard patch, redrawn as a clean vector. The double-headed eagle is reserved as a secondary motif (for example, on certificates and the about page), drawn properly later; it is not forced into the interface, and no internet image is used.
- **Rank badges:** the Admin-uploaded badge images (FR-014) appear inside the rank medallion; photographs or scans of the real patches would suit. Without an upload, the medallion shows the rank's number in its colours.
- **Sacred imagery:** authentic, public-domain icons only, credited in an asset register, in lesson content and devotional contexts. Never generated, never a reward or control.

## 7. The student progression concept

`/student` becomes **"Your rank"**:

1. **Rank header:** the headpiece frame on the rank's colour field; the rank medallion (badge in a stitched rim); the rank name large in Alegreya; "Rank 3 of 10"; a row of tesserae, one per lesson in the rank, filled for each completed lesson; and plain words: "5 of 8 lessons complete".
2. **Next lesson:** the first lesson in the rank not yet completed, with its tags and a link to it in the library. This is what the student does at home this week.
3. **This rank's lessons:** in teaching order; completed ones ticked with the date and their certificate; the rest shown as ahead, not locked (all lessons stay open to read, FR-006).
4. **The way ahead:** all ten ranks as a vertical procession. Past ranks show their badge and the date earned; the current rank is highlighted; future ranks show their name, colour outline and number of lessons. Ranks without lessons yet say "Lessons being written".

No points, streaks or comparisons with other children. Anticipation comes from seeing the next rank's colours and badge outline, and its name.

## 8. Record completions concept (staff prototype)

`/progress/record` stays one page, in three clear steps:

1. **Lesson:** choose the rank (buttons in rank colours), then one of its lessons (a list of up to 8 large rows, not a 56-item dropdown).
2. **Students:** students at that lesson's rank first; students at other ranks folded underneath. Each row is a 44 px target with the name, rank pill and a state: ready, already completed (ticked, not selectable), or consent needed (a warning pill with the reason). "Select everyone ready" and "Only my students".
3. **Record:** an action bar that stays visible ("Record 4 completions"), the optional note, and a result panel announced to screen readers that names promotions.

Same service call, same checks and the same consent block; nothing about who may record what changes.

## 9. Open questions for Philip (at the approval gate)

1. May a student be recorded for a lesson outside their current rank? The service allows it today; the design puts other ranks second, but keeps it possible.
2. The Christ-bearer patch is an icon of St Christopher. The brief rules out icons as reward badges. Keep the existing patch as that rank's uploaded badge, or design a non-iconographic Christ-bearer emblem?
3. Rank meaning lines (Ephesians 6 for the Guard ranks, procession roles after) are Claude's draft: clergy or Philip to confirm the wording.
